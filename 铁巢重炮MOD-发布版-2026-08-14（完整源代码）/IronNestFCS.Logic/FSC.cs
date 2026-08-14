using HarmonyInstance = HarmonyLib.Harmony;
using System.Collections;
using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace IronNestFCS.Logic;

public enum LeftRight {
    Left,
    Right,
}

/// <summary>
/// 纯火控领域逻辑：查找游戏对象、读取游戏数据、操控游戏内交互（dial 等）。
/// 不含任何 UI / IMGUI / 生命周期框架代码——那些在 <see cref="FcsModule"/> 和 <see cref="FcsWindow"/> 里。
///
/// 重载安全规则：
///  - 不要在这里注册新的 IL2CPP 类型（同一类型进程内只能注册一次）。
///  - 每次实例用独立的 Harmony 实例；Shutdown 时 UnpatchSelf。
///  - 所有对 IL2CPP 对象的引用在 Shutdown 时清空，便于旧 ALC 回收。
/// </summary>
public class FSC
{
    private const string HarmonyId = "com.svr2kos2.ironnestfcs.logic";

    // ===== 药包自动补充 =====
    // 两炮共用一个装药余量池：余量低于 PowderReplenishThreshold 时，每 PowderCheckInterval 秒
    // 自动购买一次装药卡，把药包维持在充足水位，避免任务流程因装药不足卡在装填/击发阶段。
    // 只做检测与补充，不干预 RunTaskRoutine 里的任何现有步骤。
    private const float PowderCheckInterval = 5f;
    private const int PowderReplenishThreshold = 10;
    private const float AutoTargetScanInterval = 2f;
    private const float ValveMaintenanceInterval = 0.5f;
    private const float ValveLooseThreshold = 0.001f;
    private const int AutoTargetCapacity = 2;
    private const float AutoTargetRetryDelay = 20f;
    private const float TargetOutcomeTimeout = 180f;
    // 自动模式绕过实体弹道计算器和确认开关，收敛后直接请求炮管开火。从停止跟踪到
    // 炮弹真正离膛只保留很短的原生响应时间，以此预测我方炮位和移动目标。
    private const float MovingSolutionUpdateInterval = 0.2f;
    private const int MovingSolutionStableUpdates = 3;
    private const float MovingSolutionTimeout = 30f;
    private const int AutoShellCost = 3;
    private const int HcheMinimumAreaTargets = 3;
    private const int DesiredRequisitionFloor = 50;
    private const int AbsoluteRequisitionReserve = 1;
    private const int ScoringInterleaveThreshold =
        DesiredRequisitionFloor + AutoShellCost * AutoTargetCapacity;

    private HarmonyInstance? _harmony;
    
    private FcsSceneInteractor _sceneInteractor;
    private readonly PurchaseDeck _purchaseDeck = new();
    public readonly MapTable MapTable = new MapTable();
    public readonly BallisticCalculator BallisticCalculator = new BallisticCalculator();
    public readonly GunSystem LeftGun = new GunSystem();
    public readonly GunSystem RightGun = new GunSystem();
    public readonly Turret Turret = new Turret();
    public readonly TriggerConsole TriggerConsole = new();
    
    // ===== 任务调度 =====
    // 用户不再指定炮管：任务入队后由调度器派给空闲炮管，炮管打完一发自动拉下一个。
    // 所有读写都在 Unity 主线程（入队来自点击回调，派发/完成来自协程），无并发，无需锁。
    private readonly Queue<ArtilleryTask> _taskQueue = new();
    private readonly HashSet<string> _autoTargetIds = new();
    private readonly Dictionary<string, float> _autoTargetRetryAfter = new();
    private readonly HashSet<string> _destroyedTargetIds = new();
    private readonly List<PendingTargetOutcome> _pendingTargetOutcomes = new();
    private bool _targetStatsInitialized;
    private int _lastTargetsDestroyed;
    private int _lastHitsOnTargets;
    private int _lastMissedShots;
    private int _untrackedShotsInFlight;
    private int _hiddenOutcomeCountersToIgnore;
    // 炮兵与 FDC 同时可用时，首次从炮兵开始，此后在两个类别之间交替。
    private bool _preferArtilleryNext = true;
    private bool _budgetPauseLogged;
    private bool _powderBudgetPauseLogged;
    private int _lastLoggedValveCount = -1;
    private long _nextTaskScheduleOrder;

    /// <summary>当前各炮管正在执行的任务；null 表示该炮管空闲。供 UI 显示与调度判断。</summary>
    public ArtilleryTask? LeftTask { get; private set; }
    public ArtilleryTask? RightTask { get; private set; }

    /// <summary>等待派发的任务数（已入队但还没分到炮管）。供 UI 显示。</summary>
    public int PendingCount => _taskQueue.Count;
    public Queue<ArtilleryTask> QueueCan => new Queue<ArtilleryTask>(_taskQueue);
    public bool AutoTargetEnabled => _sceneInteractor.AutoTarget;
    public bool DesktopOnlyEnabled => _sceneInteractor.DesktopOnly;
    public bool FreeCameraActive => _freeCamera.IsActive;
    public bool ColliderOverlayActive => _freeCamera.ColliderOverlayActive;
    public int DetectedEnemyCount { get; private set; }
    public bool AutoTargetBudgetPaused { get; private set; }
    public int RequisitionTarget => DesiredRequisitionFloor;
    public int DetectedValveCount { get; private set; }
    public int LooseValveCount { get; private set; }
    public int RepairedValveCount { get; private set; }

    /// <summary>
    /// 控制台互斥锁：保护弹道计算器、确认开关台、采购台这三组全局唯一的"短操作"硬件。
    /// 临界区都很短（解算 / 确认弹 / 击发前的确认+击发），用完即放。
    /// </summary>
    private readonly CoroutineLock _deskLock = new();

    /// <summary>
    /// 炮塔方向角锁：方向角由两管炮共享。解算后允许任务短暂取得锁做提前转向，
    /// 与后续装填和本管仰角调整重叠；提前转向完成后立即释放。仰角就绪的任务以更高
    /// 优先级重新取得锁、校验最终方向，并一直持有到击发结束。
    ///
    /// 防死锁：同时需要两把锁时始终先取得 turret，再取得 desk。
    /// </summary>
    private readonly CoroutineLock _turretLock = new();

    // 已完成仰角、正在等待最终方向和击发的任务数。预转向在取得炮塔前检查此值，
    // 防止尚未就绪的任务抢在可立即发射的炮管前面进行长距离旋转。
    private int _fireReadyTurretWaiters;

    // 完成项自动移除；Dispose 时停止剩余协程，避免热重载后旧 ALC 继续访问 IL2CPP 对象。
    private readonly CoroutineTracker _coroutines = new();
    private readonly FreeCameraController _freeCamera = new();
    public FSC() {
        this._sceneInteractor = new FcsSceneInteractor(this);
    }

    public bool IsBound { get; private set; } = false;

    /// <summary>查找并绑定游戏对象。返回 false 表示当前场景还没有目标控件。</summary>
    public bool TryBind()
    {
        // 每次重载创建全新的 Harmony 实例，避免与上一版补丁冲突。
        _sceneInteractor = new FcsSceneInteractor(this);
        _sceneInteractor.Initialize();
        _harmony = new HarmonyInstance(HarmonyId);
        _deskLock.Reset();
        _turretLock.Reset();
        _fireReadyTurretWaiters = 0;
        IsBound = MapTable.TryBind()
                  && BallisticCalculator.TryBind()
                  && LeftGun.TryBind("Left")
                  && RightGun.TryBind("Right")
                  && _purchaseDeck.TryBind()
                  && Turret.TryBind()
                  && TriggerConsole.TryBind();
        MelonLogger.Msg("[FCS] Initialize: " + (IsBound ? "success" : "failed"));
        if (IsBound) {
            RequisitionPointsMonitor.Install(_harmony);
            InitializeTargetOutcomeTracking();
            // 常驻药包自动补充协程：仅保证装药余量充足，不改动任务流程。
            _coroutines.Start(ReplenishPowderLoop());
            _coroutines.Start(AutoTargetLoop());
            _coroutines.Start(MaintainPressureValvesLoop());
        }
        // _coroutines.Start(ExposeAllEntities());
        
        return IsBound;
    }

    public void Update() {
        if (IsBound) MapTable.UpdateTurretMarker();
        _freeCamera.Update();
        // Prevent inspection clicks from operating the tactical map or console.
        if (_freeCamera.IsActive)
            return;
        _sceneInteractor.Update();
    }

    /// <summary>释放：撤销补丁、清空 IL2CPP 引用。</summary>
    public void Dispose()
    {
        _freeCamera.Shutdown();
        // 停掉所有未完成的协程，否则热重载后旧 ALC 的协程仍会被 Unity 驱动 → 崩溃。
        _coroutines.StopAll();

        // 清空调度状态，避免热重载后残留任务/槽位影响新一轮绑定。
        _taskQueue.Clear();
        _autoTargetIds.Clear();
        _autoTargetRetryAfter.Clear();
        _destroyedTargetIds.Clear();
        _pendingTargetOutcomes.Clear();
        _targetStatsInitialized = false;
        _lastTargetsDestroyed = 0;
        _lastHitsOnTargets = 0;
        _lastMissedShots = 0;
        _untrackedShotsInFlight = 0;
        _hiddenOutcomeCountersToIgnore = 0;
        _preferArtilleryNext = true;
        _budgetPauseLogged = false;
        _powderBudgetPauseLogged = false;
        _lastLoggedValveCount = -1;
        _nextTaskScheduleOrder = 0;
        AutoTargetBudgetPaused = false;
        LeftTask = null;
        RightTask = null;
        DetectedEnemyCount = 0;
        DetectedValveCount = 0;
        LooseValveCount = 0;
        RepairedValveCount = 0;
        _fireReadyTurretWaiters = 0;

        _sceneInteractor.ShutDown();
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { MelonLogger.Error($"[FCS] UnpatchSelf failed: {ex}"); }
        _harmony = null;
    }

    /// <summary>
    /// 常驻后台协程：周期性检测装药余量（两炮共用池），低于阈值时自动购买一次装药卡。
    /// 购买必须持 _deskLock——采购台是共享硬件，与任务流程的采购互斥（阻塞等待，不破坏临界区）。
    /// 必须在 TryBind 成功后交给 _coroutines；Dispose 时随其它协程一起 Stop，
    /// 迭代器被 Stop 时 Dispose 会执行 finally，锁不会泄漏。
    /// </summary>
    private IEnumerator ReplenishPowderLoop() {
        while (true) {
            yield return new WaitForSeconds(PowderCheckInterval);
            // 两炮共用一个装药余量池，读数应一致；取较小值保守触发。
            var charges = Math.Min(LeftGun.RemainingCharges(), RightGun.RemainingCharges());
            if (charges >= PowderReplenishThreshold) continue;
            var powderCost = _purchaseDeck.GetPowderCost();
            if (!CanSpendKeepingDesiredFloor(powderCost)) {
                if (!_powderBudgetPauseLogged) {
                    MelonLogger.Warning(
                        $"[FCS] AutoReplenish: RP budget paused " +
                        $"(total={RequisitionPointsMonitor.CurrentTotal}, cost={powderCost}, " +
                        $"targetFloor={DesiredRequisitionFloor})");
                    _powderBudgetPauseLogged = true;
                }
                continue;
            }
            _powderBudgetPauseLogged = false;
            MelonLogger.Msg(
                $"[FCS] AutoReplenish: powder charges {charges} < {PowderReplenishThreshold}, buying one");
            yield return _deskLock.Acquire();
            try {
                yield return _purchaseDeck.BuyPowders();
            }
            finally {
                _deskLock.Release();
            }
        }
    }

    /// <summary>
    /// 自动检查当前场景中的高压系统阀门。Damage01 大于零表示阀门已经松动；
    /// 使用游戏原生 ForceFixValve 动作复位旋钮并同步压力系统及相关事件。
    /// </summary>
    private IEnumerator MaintainPressureValvesLoop() {
        while (true) {
            // 使用实时时间，即使玩家暂停查看面板，也能完成阀门检测与拧紧。
            yield return new WaitForSecondsRealtime(ValveMaintenanceInterval);

            var activeCount = 0;
            var looseCount = 0;
            var valves = Resources.FindObjectsOfTypeAll<ValveController>();
            foreach (var valve in valves) {
                if (valve == null || valve.gameObject == null || !valve.gameObject.activeInHierarchy) continue;
                activeCount++;

                float damage;
                try {
                    damage = valve.Damage01;
                }
                catch (Exception ex) {
                    MelonLogger.Warning($"[FCS] Valve monitor: cannot read {valve.name}: {ex.Message}");
                    continue;
                }
                if (damage <= ValveLooseThreshold) continue;

                looseCount++;
                var systemId = string.IsNullOrEmpty(valve.systemId) ? "unknown" : valve.systemId;
                var dialBefore = valve.CurrentDialValue;
                try {
                    valve.ForceFixValve();
                    if (valve.Damage01 <= ValveLooseThreshold) {
                        RepairedValveCount++;
                        MelonLogger.Msg(
                            $"[FCS] Valve maintenance: tightened {valve.name} " +
                            $"(system={systemId}, damage={damage:F3}, dial={dialBefore:F3}->{valve.CurrentDialValue:F3})");
                    }
                    else {
                        MelonLogger.Warning(
                            $"[FCS] Valve maintenance: {valve.name} remained loose " +
                            $"(system={systemId}, damage={damage:F3}->{valve.Damage01:F3})");
                    }
                }
                catch (Exception ex) {
                    MelonLogger.Warning($"[FCS] Valve maintenance: failed to tighten {valve.name}: {ex.Message}");
                }
            }

            DetectedValveCount = activeCount;
            LooseValveCount = looseCount;
            if (_lastLoggedValveCount != activeCount) {
                MelonLogger.Msg($"[FCS] Valve maintenance: monitoring {activeCount} active valves");
                _lastLoggedValveCount = activeCount;
            }
        }
    }

    /// <summary>
    /// 自动索敌只补满当前两条炮管任务槽，不无限堆积队列。
    /// 候选目标由 MapTable 过滤友军、隐藏、移动和已摧毁实体，并按距离排序。
    /// </summary>
    private IEnumerator AutoTargetLoop() {
        while (true) {
            yield return new WaitForSeconds(AutoTargetScanInterval);

            // 无论自动索敌开关是否开启，都要结算已经发射的手动/自动任务。
            // 目标在炮弹落地前始终保持占用，避免远距离弹道尚未命中就被第三发重复选择。
            PollTargetOutcomes();

            if (!_sceneInteractor.AutoTarget) {
                DetectedEnemyCount = 0;
                _preferArtilleryNext = true;
                AutoTargetBudgetPaused = false;
                _budgetPauseLogged = false;
                continue;
            }

            RequisitionPointsMonitor.Poll();

            // 两个自动任务槽都被占用时，不需要再次遍历全部地图实体。命中结算仍已在
            // 上方执行，所以任务一旦结束，下一轮会立即恢复索敌。
            var occupied = _taskQueue.Count;
            if (LeftTask != null) occupied++;
            if (RightTask != null) occupied++;
            var available = AutoTargetCapacity - occupied;
            AutoTargetBudgetPaused = false;
            if (available <= 0) continue;

            // 初始任务只使用当前炮位。未来炮位不能按固定装填时间猜测；真正击发前会
            // 以高频连续解算跟踪当前炮位。
            var candidates = MapTable.GetAutoTargets(
                _sceneInteractor.selectedBulletType,
                desktopVisibleOnly: _sceneInteractor.DesktopOnly);
            DetectedEnemyCount = candidates.Count;

            var committedShells = CountPendingAutoShellPurchases();

            var eligible = new List<ArtilleryTask>();
            foreach (var task in candidates) {
                if (_destroyedTargetIds.Contains(task.sourceEntityId)) continue;
                if (_autoTargetRetryAfter.TryGetValue(task.sourceEntityId, out var retryAfter)) {
                    if (Time.realtimeSinceStartup < retryAfter) continue;
                    _autoTargetRetryAfter.Remove(task.sourceEntityId);
                }
                if (_autoTargetIds.Contains(task.sourceEntityId)) continue;
                eligible.Add(task);
            }

            PlanAreaShells(eligible);
            PlanEconomySingleTargetShells(eligible);

            while (available > 0 && eligible.Count > 0) {
                committedShells = CountPendingAutoShellPurchases();
                var task = SelectNextAutoTarget(eligible);
                var purchaseCost = EstimateNextAutoTaskPurchaseCost(task);
                if (!CanFundAutoTask(committedShells, purchaseCost)) {
                    AutoTargetBudgetPaused = true;
                    if (!_budgetPauseLogged) {
                        var total = RequisitionPointsMonitor.HasCurrentTotal
                            ? RequisitionPointsMonitor.CurrentTotal.ToString()
                            : "unknown";
                        MelonLogger.Warning(
                            $"[FCS] AutoTarget: insufficient RP to preserve the absolute reserve " +
                            $"(total={total}, target={task.targetName}, " +
                            $"committedShells={committedShells}, purchaseCost={purchaseCost})");
                        _budgetPauseLogged = true;
                    }
                    break;
                }
                _budgetPauseLogged = false;

                eligible.Remove(task);
                if (!_autoTargetIds.Add(task.sourceEntityId)) continue;
                ReserveAreaCoverage(task, eligible);

                MelonLogger.Msg(
                    $"[FCS] AutoTarget: acquired {task.targetName} [{task.sourceEntityId}] " +
                    $"({task.angel:F1} deg, {task.distance:F2} km, " +
                    $"HP {task.sourceHealth}/{task.sourceMaxHealth}, armour {task.sourceArmour}, " +
                    $"stars {task.sourceStars}, role {task.sourceRole}, " +
                    $"state {task.sourceState}, " +
                    $"icon={task.sourceIcon}, sprite={task.sourceIconSprite}, " +
                    $"statusSprites=[{task.sourceStatusSprites}], " +
                    $"immune=[{task.sourceImmuneShells}], reward={RewardLogValue(task)}, " +
                    $"rewardSource={task.sourceRewardSource}, artillery={task.isArtillery}, " +
                    $"antiAir={task.isAntiAir}, " +
                    $"commander={task.isCommander}, supply={task.isSupply}, " +
                    $"mechanized={task.isMechanized}, recon={task.isRecon}, " +
                    $"infantry={task.isInfantry}, ship={task.isShip}, hidden={task.isHidden}, " +
                    $"moving={task.isMoving}, motionSamples={task.motionSamples}, " +
                    $"velocity={MapTable.GetSpeedKmPerSecond(task.sourceVelocity):F3}km/s, " +
                    $"estimatedReward={EstimatedScoringReward(task)}, " +
                    $"underground={task.isUnderground}, requiresAP={task.requiresAp}, " +
                    $"shell={task.bulletType}, areaTargets={task.areaTargetCount}, " +
                    $"impactRadius={task.impactRadiusKm:F2}km)");
                EnqueueTask(task);
                available--;
            }
        }
    }

    /// <summary>
    /// 读取关卡实际加载的炮弹伤害/爆炸半径，为每个可选落点寻找能一次摧毁的敌军群。
    /// 地下目标继续强制 AP；炮弹免疫与友军爆炸半径会使该方案失效。
    /// </summary>
    private void PlanAreaShells(List<ArtilleryTask> candidates) {
        if (candidates.Count < 2 || _purchaseDeck.BlastProfiles.Count == 0) return;

        foreach (var center in candidates) {
            center.areaTargetCount = 1;
            center.impactRadiusKm = 0f;
            center.areaCoveredTargetIds.Clear();
            center.usesAreaAimPoint = false;
            center.areaAimOffsetFromPrimary = Vector3.zero;
        }

        // 一轮规划只读取一次友军位置。旧实现会在每个候选落点上重新扫描整张地图，
        // 目标密集或移动群组实时校正时会造成明显的主线程卡顿。
        var allyPositions = MapTable.GetLiveAllyPositions();
        var plansByTask = new Dictionary<ArtilleryTask, AreaFirePlan>();

        // 按弹种生成一次有效目标和候选落点，再把同一个结果分配给覆盖范围内的主目标。
        // 这与旧结果等价，但避免为每一个可能的主目标重复整套几何搜索。
        foreach (var profile in _purchaseDeck.BlastProfiles) {
            if (profile.ImpactRadiusMission <= 0f || !profile.CanKillTargets) continue;
            var minimumCoveredTargets = profile.Type == BulletType.HCHE
                ? HcheMinimumAreaTargets
                : 2;

            var effectiveTargets = new List<ArtilleryTask>();
            foreach (var target in candidates) {
                if (string.IsNullOrWhiteSpace(target.sourceEntityId)) continue;
                if (!MapTable.IsShellEffectiveAgainst(target, profile.Type)) continue;
                if (!CanDestroyWithProfile(target, profile)) continue;
                effectiveTargets.Add(target);
            }
            if (effectiveTargets.Count < minimumCoveredTargets) continue;

            foreach (var aimPoint in BuildAreaAimCandidates(
                         effectiveTargets, profile.ImpactRadiusMission)) {
                if (MapTable.HasPositionWithin(
                        allyPositions, aimPoint, profile.ImpactRadiusMission)) continue;

                var coveredTargets = new List<ArtilleryTask>();
                var maxDistance = 0f;
                foreach (var target in effectiveTargets) {
                    var distance = Vector3.Distance(aimPoint, target.position);
                    if (distance > profile.ImpactRadiusMission + 0.001f) continue;
                    coveredTargets.Add(target);
                    maxDistance = Mathf.Max(maxDistance, distance);
                }
                if (coveredTargets.Count < minimumCoveredTargets) continue;

                // 范围弹至少要比逐个使用当前有效弹种更划算，避免为了多覆盖一个目标
                // 自动购买 ATMC 等极昂贵特种弹，反而迅速耗尽积分。
                var separateCost = 0;
                foreach (var coveredTask in coveredTargets) {
                    separateCost += Math.Max(
                        0, _purchaseDeck.GetShellCost(coveredTask.bulletType));
                }
                if (profile.Cost > separateCost) continue;

                var clearance = profile.ImpactRadiusMission - maxDistance;
                foreach (var center in coveredTargets) {
                    if (center.requiresAp) continue;
                    var coveredIds = coveredTargets
                        .Where(task => !ReferenceEquals(task, center))
                        .Select(task => task.sourceEntityId)
                        .ToList();

                    plansByTask.TryGetValue(center, out var current);
                    var sameCoverage = current != null
                                       && coveredIds.Count == current.CoveredIds.Count;
                    var betterProfile = sameCoverage
                                        && IsBetterAreaProfile(
                                            profile, current!.Profile, center.bulletType);
                    var betterClearance = sameCoverage
                                          && current!.Profile.Type == profile.Type
                                          && clearance > current.Clearance;
                    var better = current == null
                                 || coveredIds.Count > current.CoveredIds.Count
                                 || betterProfile
                                 || betterClearance;
                    if (!better) continue;

                    plansByTask[center] = new AreaFirePlan {
                        Task = center,
                        Profile = profile,
                        CoveredIds = coveredIds,
                        AimPoint = aimPoint,
                        Clearance = clearance
                    };
                }
            }
        }

        foreach (var plan in plansByTask.Values) {
            if (!MapTable.TrySetAreaImpactPoint(
                    plan.Task, plan.AimPoint, out var reason)) {
                MelonLogger.Warning(
                    $"[FCS] Area fire plan rejected for {plan.Task.targetName}: {reason}");
                continue;
            }
            plan.Task.bulletType = plan.Profile.Type;
            plan.Task.areaTargetCount = plan.CoveredIds.Count + 1;
            plan.Task.impactRadiusKm = MapTable.MissionDistanceToKm(
                plan.Profile.ImpactRadiusMission);
            plan.Task.areaCoveredTargetIds.AddRange(plan.CoveredIds);
        }
    }

    private static IEnumerable<Vector3> BuildAreaAimCandidates(
        List<ArtilleryTask> targets, float radius) {
        foreach (var target in targets) yield return target.position;

        var centroid = Vector3.zero;
        foreach (var target in targets) centroid += target.position;
        if (targets.Count > 0) yield return centroid / targets.Count;

        for (var i = 0; i < targets.Count; ++i) {
            for (var j = i + 1; j < targets.Count; ++j) {
                var first = targets[i].position;
                var second = targets[j].position;
                var delta = second - first;
                delta.z = 0f;
                var distance = delta.magnitude;
                if (distance > radius * 2f + 0.001f) continue;

                var midpoint = (first + second) * 0.5f;
                yield return midpoint;
                if (distance <= 0.0001f) continue;

                var half = distance * 0.5f;
                var heightSquared = radius * radius - half * half;
                if (heightSquared <= 0f) continue;
                var height = Mathf.Sqrt(heightSquared);
                var perpendicular = new Vector3(-delta.y, delta.x, 0f) / distance;
                yield return midpoint + perpendicular * height;
                yield return midpoint - perpendicular * height;
            }
        }
    }

    private void PlanEconomySingleTargetShells(List<ArtilleryTask> candidates) {
        foreach (var task in candidates) {
            if (task.areaTargetCount > 1 || task.requiresAp) continue;

            // 自动索敌继承控制台当前弹种作为初始值，但它可能是 STAR/SMK/PHGN
            // 等仅供手动使用的功能弹。必须先验证“能直接消灭当前目标”，再比较价格；
            // 不能因为功能弹比 DRIL 便宜就保留它。
            var currentType = task.bulletType;
            var currentIsLethal = _purchaseDeck.TryGetShellProfile(
                                      currentType, out var currentProfile)
                                  && MapTable.IsShellEffectiveAgainst(task, currentType)
                                  && CanDestroyWithProfile(task, currentProfile);

            // DRIL 是既定的单目标经济弹；若未来更新使它不再适用，则退回到当前
            // 关卡实际加载的、能够直接致死且最便宜的弹种，保持版本兼容性。
            ShellBlastProfile? selected = null;
            if (_purchaseDeck.TryGetShellProfile(BulletType.DRIL, out var training)
                && MapTable.IsShellEffectiveAgainst(task, BulletType.DRIL)
                && CanDestroyWithProfile(task, training)) {
                selected = training;
            }
            else {
                selected = _purchaseDeck.BlastProfiles
                    .Where(profile => profile.Type != BulletType.AP)
                    // HCHE 只用于至少覆盖三个可被直接消灭目标的范围方案；
                    // 禁止单目标经济弹兜底把它重新选回来。
                    .Where(profile => profile.Type != BulletType.HCHE)
                    .Where(profile => MapTable.IsShellEffectiveAgainst(task, profile.Type))
                    .Where(profile => CanDestroyWithProfile(task, profile))
                    .OrderBy(profile => profile.Cost)
                    .ThenBy(profile => profile.ImpactRadiusMission)
                    .FirstOrDefault();
            }
            if (selected == null) continue;

            // 当前弹种确实可直接致死时才有资格参与价格比较。
            // HCHE 即使是当前控制台弹种，也只有范围规划确认至少三个致死覆盖时才能保留；
            // 单目标/双目标路径不能因为它价格不高于普通弹而绕过范围门槛。
            if (currentType != BulletType.HCHE
                && currentIsLethal
                && currentProfile!.Cost <= selected.Cost) continue;
            if (currentType == selected.Type) continue;

            task.bulletType = selected.Type;
            MelonLogger.Msg(
                $"[FCS] Economy shell plan: {selected.Type} selected for single target " +
                $"{task.targetName} [{task.sourceEntityId}], previous={currentType}, " +
                $"previousLethal={currentIsLethal}, damage={selected.Damage}, " +
                $"cost={selected.Cost}");
        }
    }

    private static bool CanDestroyWithProfile(
        ArtilleryTask task, ShellBlastProfile profile) {
        if (!profile.CanKillTargets
            || task.sourceHealth > 0 && profile.Damage < task.sourceHealth) return false;

        // 实测 FLCH 覆盖装甲/车辆群时只有其中的纯步兵产生奖励；PRPG 同样是
        // 针对人员的宣传/杀伤效果。它们的通用 Damage 字段不能证明可消灭车辆或设施。
        if (profile.Type == BulletType.FLCH || profile.Type == BulletType.PRPG) {
            return task.isInfantry;
        }
        return true;
    }

    private static bool IsBetterAreaProfile(
        ShellBlastProfile candidate, ShellBlastProfile current, BulletType originalType) {
        var candidateKeepsType = candidate.Type == originalType;
        var currentKeepsType = current.Type == originalType;
        if (candidateKeepsType != currentKeepsType) return candidateKeepsType;
        if (candidate.Cost != current.Cost) return candidate.Cost < current.Cost;
        if (candidate.ImpactRadiusMission != current.ImpactRadiusMission) {
            return candidate.ImpactRadiusMission < current.ImpactRadiusMission;
        }
        return candidate.Damage > current.Damage;
    }

    private void ReserveAreaCoverage(
        ArtilleryTask task, List<ArtilleryTask> remainingCandidates) {
        if (task.areaCoveredTargetIds.Count == 0) return;
        var covered = new HashSet<string>(task.areaCoveredTargetIds);
        foreach (var entityId in covered) {
            // 主目标结算前持续占用；若任务取消则 ReleaseTargetReservation 会立即释放。
            _autoTargetRetryAfter[entityId] = float.PositiveInfinity;
        }
        remainingCandidates.RemoveAll(candidate => covered.Contains(candidate.sourceEntityId));
        MelonLogger.Msg(
            $"[FCS] Area fire plan: {task.bulletType} at {task.targetName} covers " +
            $"{task.areaTargetCount} enemies within {task.impactRadiusKm:F2}km; " +
            $"optimizedAim=({task.position.x:F3},{task.position.y:F3}); " +
            $"reserved=[{string.Join(",", task.areaCoveredTargetIds)}]");
    }

    private bool CanFundAutoTask(int committedShells, int nextShellCost) {
        if (!RequisitionPointsMonitor.HasCurrentTotal) return false;
        var projected = RequisitionPointsMonitor.CurrentTotal
                        - committedShells * AutoShellCost
                        - nextShellCost;
        return projected >= AbsoluteRequisitionReserve;
    }

    private static bool CanSpendKeepingDesiredFloor(int cost) {
        if (cost <= 0) return true;
        RequisitionPointsMonitor.Poll();
        return RequisitionPointsMonitor.HasCurrentTotal
               && RequisitionPointsMonitor.CurrentTotal - cost >= DesiredRequisitionFloor;
    }

    private static bool CanSpendForAutoTask(int cost) {
        if (cost <= 0) return true;
        RequisitionPointsMonitor.Poll();
        return RequisitionPointsMonitor.HasCurrentTotal
               && RequisitionPointsMonitor.CurrentTotal - cost >= AbsoluteRequisitionReserve;
    }

    private static bool IsScoringTarget(ArtilleryTask task) {
        // 实测 OptionalTarget/Tank（机械化）命中后不增加征用积分。
        // 机械化仍保留战术分类和常规排序，但不能再承担低分恢复任务。
        return task.isSupply || task.isRecon;
    }

    private static int EstimatedScoringReward(ArtilleryTask task) {
        if (!IsScoringTarget(task)) return 0;
        if (task.sourceRewardPoints > 0) return task.sourceRewardPoints;
        return Math.Max(1, task.sourceStars) * 10;
    }

    private int EstimateNextAutoTaskPurchaseCost(ArtilleryTask task) {
        var gunSys = LeftTask == null ? LeftGun : RightGun;
        var cost = gunSys.HaveBulletInCylinder(task.bulletType)
            ? 0
            : Math.Max(0, _purchaseDeck.GetShellCost(task.bulletType));

        var powderCount = _sceneInteractor.maxCharge
            ? 6
            : BallisticCalculator.MinimumCharge(task.distance);
        if (gunSys.RemainingCharges() < powderCount) {
            cost += Math.Max(0, _purchaseDeck.GetPowderCost());
        }
        return cost;
    }

    private int CountPendingAutoShellPurchases() {
        var count = 0;
        if (NeedsAutoShellBudget(LeftTask)) count++;
        if (NeedsAutoShellBudget(RightTask)) count++;
        foreach (var task in _taskQueue) {
            if (NeedsAutoShellBudget(task)) count++;
        }
        return count;
    }

    private static bool NeedsAutoShellBudget(ArtilleryTask? task) {
        if (task == null || !task.isAutoTarget) return false;
        return task.progress == Progress.Pending
               || task.progress == Progress.Calculating
               || task.progress == Progress.SelectingBullet;
    }

    /// <summary>
    /// 战术优先级：炮兵与指挥官高于普通目标；两者同时存在时从炮兵开始交替攻击。
    /// 防空炮紧随指挥官，排在其余支援与普通目标之前。
    /// 某一高优先类别暂时为空时立即选择另一类，不让空闲炮管等待。
    /// </summary>
    private ArtilleryTask SelectNextAutoTarget(List<ArtilleryTask> candidates) {
        var artillery = FindBestCandidate(candidates, task => task.isArtillery);
        var commander = FindBestCandidate(candidates, task => task.isCommander);
        var antiAir = FindBestCandidate(candidates, task => task.isAntiAir);
        var scoring = FindBestCandidate(
            candidates,
            IsScoringTarget,
            CompareStarsFirst);
        var supply = FindBestCandidate(candidates, task => task.isSupply, CompareStarsFirst);
        var mechanized = FindBestCandidate(
            candidates, task => task.isMechanized, CompareStarsFirst);
        var recon = FindBestCandidate(candidates, task => task.isRecon, CompareStarsFirst);
        var other = FindBestCandidate(candidates,
            task => !task.isArtillery && !task.isCommander && !task.isAntiAir
                    && !task.isSupply && !task.isMechanized && !task.isRecon
                    && !task.isInfantry);
        var infantry = FindBestCandidate(candidates, task => task.isInfantry);
        var prioritizeScoring = scoring != null
                                && RequisitionPointsMonitor.HasCurrentTotal
                                && RequisitionPointsMonitor.CurrentTotal <= ScoringInterleaveThreshold;

        ArtilleryTask selected;
        var selectedFromAlternatingPair = false;
        if (prioritizeScoring) {
            selected = scoring!;
        }
        else if (artillery != null && commander != null) {
            selected = _preferArtilleryNext ? artillery : commander;
            selectedFromAlternatingPair = true;
        }
        else if (artillery != null) {
            selected = artillery;
        }
        else if (commander != null) {
            selected = commander;
        }
        else if (antiAir != null) {
            selected = antiAir;
        }
        else if (supply != null) {
            selected = supply;
        }
        else if (mechanized != null) {
            selected = mechanized;
        }
        else if (recon != null) {
            selected = recon;
        }
        else if (other != null) {
            selected = other;
        }
        else {
            selected = infantry!;
        }

        if (selectedFromAlternatingPair) {
            _preferArtilleryNext = !selected.isArtillery;
        }
        else if (artillery == null || commander == null) {
            // 组合中断后，下一次两类重新同时出现时仍从炮兵开始。
            _preferArtilleryNext = true;
        }
        return selected;
    }

    /// <summary>同一战术类别内：已知积分高者优先，其次星级高者，最后距离近者。</summary>
    private static ArtilleryTask? FindBestCandidate(
        List<ArtilleryTask> candidates, Predicate<ArtilleryTask> predicate,
        Comparison<ArtilleryTask>? comparison = null) {
        ArtilleryTask? best = null;
        comparison ??= CompareTargetValue;
        foreach (var candidate in candidates) {
            if (!predicate(candidate)) continue;
            if (best == null || comparison(candidate, best) < 0) best = candidate;
        }
        return best;
    }

    private static int CompareStarsFirst(ArtilleryTask a, ArtilleryTask b) {
        var starsOrder = b.sourceStars.CompareTo(a.sourceStars);
        if (starsOrder != 0) return starsOrder;

        var areaOrder = b.areaTargetCount.CompareTo(a.areaTargetCount);
        if (areaOrder != 0) return areaOrder;

        var aReward = Math.Max(0, a.sourceRewardPoints);
        var bReward = Math.Max(0, b.sourceRewardPoints);
        var rewardOrder = bReward.CompareTo(aReward);
        if (rewardOrder != 0) return rewardOrder;
        return a.distance.CompareTo(b.distance);
    }

    private static int CompareTargetValue(ArtilleryTask a, ArtilleryTask b) {
        var areaOrder = b.areaTargetCount.CompareTo(a.areaTargetCount);
        if (areaOrder != 0) return areaOrder;

        var aReward = Math.Max(0, a.sourceRewardPoints);
        var bReward = Math.Max(0, b.sourceRewardPoints);
        var rewardOrder = bReward.CompareTo(aReward);
        if (rewardOrder != 0) return rewardOrder;

        var starsOrder = b.sourceStars.CompareTo(a.sourceStars);
        if (starsOrder != 0) return starsOrder;
        return a.distance.CompareTo(b.distance);
    }

    private static string RewardLogValue(ArtilleryTask task) {
        return task.sourceRewardPoints >= 0 ? task.sourceRewardPoints.ToString() : "unknown";
    }

    public IEnumerator ExposeAllEntities() {
        while (true) {
            foreach (var m in MapTable.GetAllFireMissionEntities()) {
                var vr = m.transform.FindChild("VisualRoot");
                vr.gameObject.SetActive(true);
                vr.FindChild("Info").gameObject.SetActive(true);
            }
            yield return new WaitForSeconds(1f);
        }
    }

    /// <summary>
    /// 把任务加入调度队列。用户不指定炮管——调度器自动派给空闲炮管。
    /// 入队后立即尝试派发；若两管炮都忙，任务留在队列里，等某管炮打完自动拉取。
    /// 必须在主线程调用（点击回调即是）。
    /// </summary>
    private void InitializeTargetOutcomeTracking() {
        try {
            var tracker = MissionStatsTracker.Instance;
            if (tracker == null) return;
            _lastTargetsDestroyed = tracker.TargetsDestroyed_Mission;
            _lastHitsOnTargets = tracker.HitsOnTargets_Mission;
            _lastMissedShots = tracker.MissedShots_Mission;
            _untrackedShotsInFlight = Math.Max(
                0,
                tracker.ShotsFired_Mission - _lastHitsOnTargets - _lastMissedShots);
            _targetStatsInitialized = true;
            MelonLogger.Msg(
                $"[FCS] Target tracking: baseline destroyed={_lastTargetsDestroyed}, " +
                $"hits={_lastHitsOnTargets}, misses={_lastMissedShots}, " +
                $"untrackedInFlight={_untrackedShotsInFlight}");
        }
        catch (Exception ex) {
            _targetStatsInitialized = false;
            MelonLogger.Warning($"[FCS] Target tracking: baseline failed: {ex.Message}");
        }
    }

    private void PollTargetOutcomes() {
        try {
            var tracker = MissionStatsTracker.Instance;
            if (tracker == null) return;
            if (!_targetStatsInitialized) {
                InitializeTargetOutcomeTracking();
                return;
            }

            var destroyed = tracker.TargetsDestroyed_Mission;
            var hits = tracker.HitsOnTargets_Mission;
            var misses = tracker.MissedShots_Mission;

            if (destroyed < _lastTargetsDestroyed
                || hits < _lastHitsOnTargets
                || misses < _lastMissedShots) {
                _pendingTargetOutcomes.Clear();
                _autoTargetIds.Clear();
                _autoTargetRetryAfter.Clear();
                _destroyedTargetIds.Clear();
                _lastTargetsDestroyed = destroyed;
                _lastHitsOnTargets = hits;
                _lastMissedShots = misses;
                _untrackedShotsInFlight = 0;
                _hiddenOutcomeCountersToIgnore = 0;
                MelonLogger.Msg("[FCS] Target tracking: mission counters reset");
                return;
            }

            var destroyedDelta = destroyed - _lastTargetsDestroyed;
            var hitDelta = hits - _lastHitsOnTargets;
            var missDelta = misses - _lastMissedShots;
            _lastTargetsDestroyed = destroyed;
            _lastHitsOnTargets = hits;
            _lastMissedShots = misses;

            // 热重载时已经在空中的旧炮弹没有对应任务。先消费它们的落点，
            // 防止旧炮弹的命中/未命中结果错误释放刚建立的新目标锁定。
            while (_untrackedShotsInFlight > 0 && (hitDelta > 0 || missDelta > 0)) {
                if (missDelta > 0) {
                    missDelta--;
                }
                else {
                    hitDelta--;
                    if (destroyedDelta > 0) destroyedDelta--;
                }
                _untrackedShotsInFlight--;
                MelonLogger.Msg(
                    $"[FCS] Target tracking: ignored pre-reload outcome, " +
                    $"remaining={_untrackedShotsInFlight}");
            }

            ConsumeDeferredHiddenCounters(ref hitDelta, ref missDelta, ref destroyedDelta);
            ResolveHiddenEntityOutcomes(ref hitDelta, ref missDelta, ref destroyedDelta);

            ResolvePendingDestroyedTargets(destroyedDelta);

            // 摧毁通常也计为命中；剩余命中暂缓数秒，等待可能延迟的摧毁计数。
            var survivingHitDelta = Math.Max(0, hitDelta - destroyedDelta);
            for (var i = 0; i < survivingHitDelta; i++) MarkPendingTargetHit();
            for (var i = 0; i < missDelta; i++) ResolvePendingMissedTarget();

            var now = Time.realtimeSinceStartup;
            for (var i = _pendingTargetOutcomes.Count - 1; i >= 0; i--) {
                var pending = _pendingTargetOutcomes[i];
                if (pending.HitObservedAt > 0f && now - pending.HitObservedAt >= 4f) {
                    _pendingTargetOutcomes.RemoveAt(i);
                    if (pending.HealthBeforeShot <= 1 || pending.MaxHealth <= 1) {
                        MarkPendingTargetDestroyed(pending, "confirmed lethal hit");
                    }
                    else {
                        ReleasePendingTarget(pending, "hit but target survived");
                    }
                }
                else if (pending.IsHidden && pending.MissObservedAt > 0f
                         && now - pending.MissObservedAt >= 4f) {
                    _pendingTargetOutcomes.RemoveAt(i);
                    ReleasePendingTarget(pending, "hidden shot missed; entity unchanged");
                }
                else if (now - pending.FiredAt >= TargetOutcomeTimeout) {
                    _pendingTargetOutcomes.RemoveAt(i);
                    ReleasePendingTarget(pending, "outcome timeout");
                }
            }
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS] Target tracking: poll failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 正式版会把部分隐藏任务实体的真实命中写入 MissionManager.ShotsHit，
    /// 却同时让 MissionStatsTracker 增加 miss。实体状态已经给出精确结果后，
    /// 要消费这条失真的全局计数，避免它被错误分配给另一发炮弹。
    /// </summary>
    private void ConsumeDeferredHiddenCounters(
        ref int hitDelta, ref int missDelta, ref int destroyedDelta) {
        while (_hiddenOutcomeCountersToIgnore > 0 && (hitDelta > 0 || missDelta > 0)) {
            if (missDelta > 0) missDelta--;
            else hitDelta--;
            if (destroyedDelta > 0) destroyedDelta--;
            _hiddenOutcomeCountersToIgnore--;
            MelonLogger.Msg(
                $"[FCS] Target tracking: ignored hidden-target stats mismatch, " +
                $"remaining={_hiddenOutcomeCountersToIgnore}");
        }
    }

    /// <summary>
    /// 隐藏实体不依赖地图图标和 MissionStatsTracker；直接读取目标的 Health/State。
    /// 这也是 enemyassemblyarea 等任务实体最可靠的命中与摧毁判据。
    /// </summary>
    private void ResolveHiddenEntityOutcomes(
        ref int hitDelta, ref int missDelta, ref int destroyedDelta) {
        for (var i = _pendingTargetOutcomes.Count - 1; i >= 0; i--) {
            var pending = _pendingTargetOutcomes[i];
            if (!pending.IsHidden) continue;
            if (!MapTable.TryGetEntityStatus(
                    pending.EntityId, out var isAlive, out var health,
                    out _, out var state)) continue;

            var destroyed = !isAlive || health <= 0
                            || (state & MapEntityStates.Destroyed) != 0;
            var damaged = pending.HealthBeforeShot > 0 && health < pending.HealthBeforeShot;
            if (!destroyed && !damaged) continue;

            _pendingTargetOutcomes.RemoveAt(i);
            if (!pending.CounterObserved) {
                var consumed = false;
                // 已实测这类命中优先落入错误的 miss 计数，因此先消费 miss。
                if (missDelta > 0) {
                    missDelta--;
                    consumed = true;
                }
                else if (hitDelta > 0) {
                    hitDelta--;
                    consumed = true;
                }
                if (!consumed) _hiddenOutcomeCountersToIgnore++;
            }
            if (destroyedDelta > 0) {
                destroyedDelta -= Math.Min(
                    destroyedDelta, Math.Max(1, pending.AreaTargetCount));
            }

            if (destroyed) {
                MarkPendingTargetDestroyed(pending, "hidden entity state confirmed");
            }
            else {
                ReleasePendingTarget(pending, "hidden entity damaged but survived");
            }
        }
    }

    private void ResolvePendingDestroyedTargets(int destroyedCount) {
        var remaining = destroyedCount;
        while (remaining > 0) {
            if (_pendingTargetOutcomes.Count == 0) {
                MelonLogger.Msg(
                    $"[FCS] Target tracking: {remaining} destroy(s) observed without a tracked shot");
                return;
            }

            var index = _pendingTargetOutcomes.FindIndex(item => item.HitObservedAt > 0f);
            if (index < 0) index = 0;
            var pending = _pendingTargetOutcomes[index];
            _pendingTargetOutcomes.RemoveAt(index);
            MarkPendingTargetDestroyed(pending, "mission destroy counter");
            // 一发范围弹可能让摧毁计数一次增加多项，不能把余下计数误配给其它在途炮弹。
            remaining -= Math.Min(remaining, Math.Max(1, pending.AreaTargetCount));
        }
    }

    private void MarkPendingTargetDestroyed(PendingTargetOutcome pending, string reason) {
        _autoTargetIds.Remove(pending.EntityId);
        _autoTargetRetryAfter.Remove(pending.EntityId);
        _destroyedTargetIds.Add(pending.EntityId);
        ReleaseCollateralTargets(pending.CollateralTargetIds);
        MelonLogger.Msg(
            $"[FCS] Target tracking: destroyed {pending.TargetName} [{pending.EntityId}], " +
            $"reason={reason}");
    }

    private void MarkPendingTargetHit() {
        var pending = _pendingTargetOutcomes.Find(
            item => item.HitObservedAt <= 0f && item.MissObservedAt <= 0f);
        if (pending == null) {
            MelonLogger.Msg("[FCS] Target tracking: hit observed without a tracked shot");
            return;
        }
        pending.CounterObserved = true;
        pending.HitObservedAt = Time.realtimeSinceStartup;
        MelonLogger.Msg(
            $"[FCS] Target tracking: hit observed for {pending.TargetName} [{pending.EntityId}]");
    }

    private void ResolvePendingMissedTarget() {
        var index = _pendingTargetOutcomes.FindIndex(
            item => item.HitObservedAt <= 0f && item.MissObservedAt <= 0f);
        if (index < 0) {
            MelonLogger.Msg("[FCS] Target tracking: miss observed without a tracked shot");
            return;
        }
        var pending = _pendingTargetOutcomes[index];
        pending.CounterObserved = true;
        if (pending.IsHidden) {
            // 隐藏目标的 MissionStats miss 可能是伪结果；留出时间等待实体生命值/状态更新。
            pending.MissObservedAt = Time.realtimeSinceStartup;
            MelonLogger.Msg(
                $"[FCS] Target tracking: hidden-target miss deferred for " +
                $"{pending.TargetName} [{pending.EntityId}]");
            return;
        }
        _pendingTargetOutcomes.RemoveAt(index);
        ReleasePendingTarget(pending, "missed");
    }

    private void ReleasePendingTarget(PendingTargetOutcome pending, string reason) {
        _autoTargetIds.Remove(pending.EntityId);
        _autoTargetRetryAfter[pending.EntityId] =
            Time.realtimeSinceStartup + AutoTargetRetryDelay;
        ReleaseCollateralTargets(pending.CollateralTargetIds);
        MelonLogger.Msg(
            $"[FCS] Target tracking: released {pending.TargetName} [{pending.EntityId}], " +
            $"reason={reason}");
    }

    public void EnqueueTask(ArtilleryTask task) {
        if (!task.isAutoTarget && string.IsNullOrEmpty(task.sourceEntityId)) {
            if (MapTable.TryAttachSourceEntity(task, task.bulletType)) {
                MelonLogger.Msg(
                    $"[FCS] Manual target: matched T{task.targetId} to " +
                    $"{task.targetName} [{task.sourceEntityId}]");
            }
            else {
                MelonLogger.Warning(
                    $"[FCS] Manual target: T{task.targetId} has no matching FireMission entity; " +
                    "in-flight duplicate suppression is unavailable for this shot");
            }
        }

        if (!string.IsNullOrEmpty(task.sourceEntityId)) {
            if (_destroyedTargetIds.Contains(task.sourceEntityId)) {
                MelonLogger.Msg(
                    $"[FCS] Target tracking: ignored already destroyed target " +
                    $"{task.targetName} [{task.sourceEntityId}]");
                return;
            }

            // 自动任务在选择时已经占用 ID；手动任务在这里加入同一个集合。
            if (!task.isAutoTarget && !_autoTargetIds.Add(task.sourceEntityId)) {
                MelonLogger.Msg(
                    $"[FCS] Target tracking: ignored duplicate manual target " +
                    $"{task.targetName} [{task.sourceEntityId}]");
                return;
            }
        }

        task.progress = Progress.Pending;
        _taskQueue.Enqueue(task);
        TryDispatch();
    }

    /// <summary>把队首任务派给空闲炮管，直到没有空闲炮管或队列空。</summary>
    private void TryDispatch() {
        while (_taskQueue.Count > 0) {
            LeftRight slot;
            if (LeftTask == null) slot = LeftRight.Left;
            else if (RightTask == null) slot = LeftRight.Right;
            else break; // 两管炮都忙

            var task = _taskQueue.Dequeue();
            task.scheduleOrder = ++_nextTaskScheduleOrder;
            if (slot == LeftRight.Left) LeftTask = task;
            else RightTask = task;
            StartTaskRoutine(slot, task);
        }
    }

    /// <summary>
    /// 启动一个火控任务协程。用 MelonCoroutines 跑协程实现延时——
    /// 协程由 Unity 在主线程分帧驱动，yield 期间不阻塞、恢复后仍在主线程，
    /// 因此可安全访问 IL2CPP 对象。绝不能用 async/Task.Delay：其 continuation
    /// 会在线程池线程恢复，跨线程访问 IL2CPP 运行时会导致进程崩溃且无日志。
    /// </summary>
    private void StartTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        _coroutines.Start(RunTaskRoutine(leftRight, task));
    }

    /// <summary>
    /// 解算完成后尽早把共享炮塔转向目标。这里只持锁到旋转完成，不等待装填或仰角，
    /// 所以另一管炮仍可在本任务准备期间使用方向控制。最终击发前还会再次校验方向。
    /// </summary>
    private void StartPreRotation(LeftRight leftRight, ArtilleryTask task) {
        _coroutines.Start(PreRotateTurret(leftRight, task));
    }

    private IEnumerator PreRotateTurret(LeftRight leftRight, ArtilleryTask task) {
        var deferredLogged = false;
        // 只有尚未发射任务中顺序最早的一项可以预转向。后续新目标仍可并行装填和升仰角，
        // 但不会把炮塔从即将开火的旧目标方向拉走。
        while (HasOlderActiveTask(task) || _fireReadyTurretWaiters > 0) {
            // 本任务可能已由最终击发流程抢先完成；不能在发射后再转回旧目标。
            if (task.progress >= Progress.BackToIdle) yield break;
            if (!deferredLogged && HasOlderActiveTask(task)) {
                MelonLogger.Msg(
                    $"[FCS] {leftRight}: pre-rotation deferred for {task.targetName}; " +
                    "an older firing task owns look-ahead rotation");
                deferredLogged = true;
            }
            yield return null;
        }

        yield return _turretLock.Acquire();
        try {
            // 等锁期间可能刚好有更早任务完成仰角；此时立即让路。
            if (task.progress >= Progress.BackToIdle ||
                HasOlderActiveTask(task) ||
                _fireReadyTurretWaiters > 0) yield break;

            MelonLogger.Msg(
                $"[FCS] {leftRight}: pre-rotating shared turret for {task.targetName} " +
                $"({task.angel:F1}deg) while loading/elevating");
            yield return Turret.SetRotation(task.angel);
        }
        finally {
            _turretLock.Release();
        }
    }

    private bool HasOlderActiveTask(ArtilleryTask task) {
        return IsOlderActiveTask(LeftTask, task) || IsOlderActiveTask(RightTask, task);
    }

    private static bool IsOlderActiveTask(ArtilleryTask? candidate, ArtilleryTask task) {
        return candidate != null &&
               !ReferenceEquals(candidate, task) &&
               candidate.scheduleOrder > 0 &&
               candidate.scheduleOrder < task.scheduleOrder;
    }

    /// <summary>仰角就绪任务使用的高优先级炮塔申请。</summary>
    private IEnumerator AcquireTurretForFire() {
        ++_fireReadyTurretWaiters;
        try {
            yield return _turretLock.Acquire();
        }
        finally {
            --_fireReadyTurretWaiters;
        }
    }

    private sealed class ParallelRoutineState {
        public bool Finished;
    }

    private IEnumerator RunAndSignal(IEnumerator routine, ParallelRoutineState state) {
        try {
            yield return routine;
        }
        finally {
            state.Finished = true;
        }
    }

    /// <summary>
    /// 启动本管炮的仰角修正并返回完成状态，让共享炮塔可在同一时间旋转。
    /// 子协程同样登记，确保热重载时会被停止。
    /// </summary>
    private ParallelRoutineState StartParallelElevation(GunSystem gunSys, float elevation) {
        var state = new ParallelRoutineState();
        _coroutines.Start(RunAndSignal(gunSys.SetElevation(elevation), state));
        return state;
    }

    /// <summary>炮管打完一发后释放槽位并尝试拉取队列里的下一个任务。</summary>
    private void ReleaseSlot(LeftRight leftRight) {
        if (leftRight == LeftRight.Left) LeftTask = null;
        else RightTask = null;
        TryDispatch();
    }

    private void CompleteTask(LeftRight leftRight, ArtilleryTask task) {
        ReleaseTargetReservation(task, AutoTargetRetryDelay, "task canceled");
        ReleaseSlot(leftRight);
    }

    private void TrackFiredTarget(ArtilleryTask task) {
        if (!string.IsNullOrEmpty(task.sourceEntityId)) {
            _pendingTargetOutcomes.Add(new PendingTargetOutcome {
                EntityId = task.sourceEntityId,
                TargetName = task.targetName,
                FiredAt = Time.realtimeSinceStartup,
                IsHidden = task.isHidden,
                HealthBeforeShot = task.sourceHealth,
                MaxHealth = task.sourceMaxHealth,
                AreaTargetCount = task.areaTargetCount,
                CollateralTargetIds = new List<string>(task.areaCoveredTargetIds)
            });
            MelonLogger.Msg(
                $"[FCS] Target tracking: shot in flight to {task.targetName} " +
                $"[{task.sourceEntityId}], expectedAreaTargets={task.areaTargetCount}");
        }
    }

    private void ReleaseTargetReservation(ArtilleryTask task, float retryDelay, string reason) {
        if (!string.IsNullOrEmpty(task.sourceEntityId)) {
            _autoTargetIds.Remove(task.sourceEntityId);
            _autoTargetRetryAfter[task.sourceEntityId] = Time.realtimeSinceStartup + retryDelay;
            MelonLogger.Msg(
                $"[FCS] Target tracking: released {task.targetName} [{task.sourceEntityId}], " +
                $"reason={reason}");
        }
        // 尚未发射的范围任务取消时，附带目标应立即回到候选列表。
        foreach (var entityId in task.areaCoveredTargetIds) {
            _autoTargetRetryAfter.Remove(entityId);
        }
        task.areaCoveredTargetIds.Clear();
        task.areaTargetCount = 1;
        task.impactRadiusKm = 0f;
        task.usesAreaAimPoint = false;
        task.areaAimOffsetFromPrimary = Vector3.zero;
    }

    private void ReleaseCollateralTargets(IEnumerable<string> entityIds) {
        var retryAt = Time.realtimeSinceStartup + AutoTargetRetryDelay;
        foreach (var entityId in entityIds) {
            if (string.IsNullOrWhiteSpace(entityId)) continue;
            if (MapTable.TryGetEntityStatus(
                    entityId, out var isAlive, out var health, out _, out var state)
                && (!isAlive || health <= 0
                    || (state & MapEntityStates.Destroyed) != 0)) {
                _autoTargetRetryAfter.Remove(entityId);
                _destroyedTargetIds.Add(entityId);
                continue;
            }
            _autoTargetRetryAfter[entityId] = retryAt;
        }
    }

    /// <summary>
    /// 当前弹种/装药无法继续攻击原目标时，先在所有未占用目标中按既有战术优先级
    /// 选择可达目标；没有目标则生成地图内安全空放点，保证已装填炮管最终能够释放。
    /// </summary>
    private bool TryRetargetOrPrepareSafeDischarge(
        LeftRight leftRight, ArtilleryTask task, GunSystem gunSys, out string reason) {
        reason = "";
        var loadedShellId = gunSys.BulletInChamber();
        if (!BulletTypeNames.TryParse(loadedShellId, out var loadedBullet)) {
            reason = $"unknown chambered shell '{loadedShellId ?? "empty"}'";
            return false;
        }

        var reachable = new List<ArtilleryTask>();
        foreach (var candidate in MapTable.GetAutoTargets(
                     loadedBullet,
                     desktopVisibleOnly: _sceneInteractor.DesktopOnly)) {
            if (candidate.bulletType != loadedBullet) continue;
            if (candidate.sourceEntityId == task.sourceEntityId) continue;
            if (_destroyedTargetIds.Contains(candidate.sourceEntityId)) continue;
            if (_autoTargetIds.Contains(candidate.sourceEntityId)) continue;
            if (_autoTargetRetryAfter.TryGetValue(candidate.sourceEntityId, out var retryAfter)
                && Time.realtimeSinceStartup < retryAfter) continue;
            if (!gunSys.TrySolveElevation(candidate.distance, out _, out _)) continue;
            reachable.Add(candidate);
        }

        var oldTargetName = task.targetName;
        var oldTargetId = task.sourceEntityId;
        if (reachable.Count > 0) {
            var replacement = SelectNextAutoTarget(reachable);
            if (!_autoTargetIds.Add(replacement.sourceEntityId)) {
                reason = "replacement target was reserved concurrently";
                return false;
            }
            ReleaseTargetReservation(task, AutoTargetRetryDelay, "loaded round retargeted");
            MapTable.RetargetTask(task, replacement);
            task.bulletType = loadedBullet;
            task.usesMovingPlatformSolution |= MapTable.IsFiringPlatformMoving;
            MelonLogger.Warning(
                $"[FCS] {leftRight}: loaded {loadedBullet} round retargeted from " +
                $"{oldTargetName} [{oldTargetId}] to {task.targetName} [{task.sourceEntityId}] " +
                $"({task.angel:F1}deg/{task.distance:F2}km)");
            return true;
        }

        if (!gunSys.TryGetLoadedRange(out var minRange, out var maxRange, out reason)
            || !MapTable.TryCreateSafeDischargeTask(
                minRange, maxRange, loadedBullet, out var discharge, out reason,
                range => gunSys.TrySolveElevation(range, out _, out _))) {
            return false;
        }

        ReleaseTargetReservation(task, AutoTargetRetryDelay, "loaded round redirected to safe discharge");
        MapTable.RetargetTask(task, discharge);
        task.bulletType = loadedBullet;
        task.usesMovingPlatformSolution |= MapTable.IsFiringPlatformMoving;
        MelonLogger.Warning(
            $"[FCS] {leftRight}: no reachable target for loaded {loadedBullet} round; " +
            $"safe discharge will release the gun at {task.angel:F1}deg/{task.distance:F2}km");
        return true;
    }

    /// <summary>
    /// 热重载可能发生在原逻辑已经把炮弹推进炮膛、但尚未击发的时刻。新 FSC 实例并不知道
    /// 这发炮弹属于哪个旧任务，若直接装入新任务弹种，原生推杆会因为炮膛非空而永远不可用。
    /// 不强制退弹：为膛内现有弹药寻找经过真实弹道验证的地图内空放点，正常击发并等待机械复位，
    /// 随后由调用方继续原任务。此临时空放不修改原任务，也不释放其目标预约。
    /// </summary>
    private IEnumerator DischargeInheritedRound(
        LeftRight leftRight, GunSystem gunSys, int fallbackPowderCount) {
        var loadedShellId = gunSys.BulletInChamber();
        if (string.IsNullOrWhiteSpace(loadedShellId)) yield break;

        MelonLogger.Warning(
            $"[FCS] {leftRight}: inherited chambered round detected after task hand-off; " +
            $"it will be safely discharged before the scheduled task continues " +
            $"({gunSys.LoadedRoundDescription})");

        if (!BulletTypeNames.TryParse(loadedShellId, out var loadedBullet)) {
            var nextUnknownLogAt = Time.realtimeSinceStartup;
            while (!gunSys.IsChamberEmpty()) {
                if (Time.realtimeSinceStartup >= nextUnknownLogAt) {
                    MelonLogger.Warning(
                        $"[FCS] {leftRight}: inherited shell ID '{loadedShellId}' is unknown; " +
                        "round retained without forced ejection, waiting for native/manual discharge");
                    nextUnknownLogAt = Time.realtimeSinceStartup + 15f;
                }
                yield return new WaitForSeconds(0.5f);
            }
            yield break;
        }

        // 极少数热重载会正好发生在“弹已入膛、装药尚未推进”之间。既然禁止退弹，
        // 就为遗留弹补入本任务已经准备好的装药，使它能够通过正常机械流程释放。
        if (gunSys.LoadedPowderCharges <= 0) {
            var inheritedCharge = Mathf.Clamp(fallbackPowderCount, 1, 6);
            MelonLogger.Warning(
                $"[FCS] {leftRight}: inherited {loadedBullet} has no powder; " +
                $"loading {inheritedCharge} charge(s) for safe discharge");
            yield return gunSys.LoadPowder(inheritedCharge);
        }

        var nextCanFireLogAt = Time.realtimeSinceStartup;
        while (!gunSys.CanFire()) {
            if (gunSys.IsChamberEmpty()) {
                MelonLogger.Msg(
                    $"[FCS] {leftRight}: inherited round was cleared by the native game state");
                yield break;
            }
            if (Time.realtimeSinceStartup >= nextCanFireLogAt) {
                MelonLogger.Warning(
                    $"[FCS] {leftRight}: waiting for inherited round to become fireable; " +
                    $"{gunSys.LoadedRoundDescription}");
                nextCanFireLogAt = Time.realtimeSinceStartup + 10f;
            }
            yield return new WaitForSeconds(0.5f);
        }

        ArtilleryTask? discharge = null;
        float elevation = 0f;
        var nextSolutionLogAt = Time.realtimeSinceStartup;
        while (discharge == null) {
            if (gunSys.IsChamberEmpty()) yield break;

            var reason = "loaded range is not ready";
            if (gunSys.TryGetLoadedRange(out var minRange, out var maxRange, out reason)
                && MapTable.TryCreateSafeDischargeTask(
                    minRange, maxRange, loadedBullet, out var candidate, out reason,
                    range => gunSys.TrySolveElevation(range, out _, out _))
                && MapTable.TryRefreshTargetSolution(
                    candidate, 0f, 0f, false, out reason)
                && gunSys.TrySolveElevation(candidate.distance, out elevation, out reason)) {
                discharge = candidate;
                break;
            }

            if (Time.realtimeSinceStartup >= nextSolutionLogAt) {
                MelonLogger.Warning(
                    $"[FCS] {leftRight}: inherited round retained while searching for a " +
                    $"ballistically reachable safe-discharge point; reason={reason}");
                nextSolutionLogAt = Time.realtimeSinceStartup + 10f;
            }
            yield return new WaitForSeconds(1f);
        }

        yield return gunSys.SetElevation(elevation);
        yield return AcquireTurretForFire();
        try {
            var stableUpdates = 0;
            var nextTrackingLogAt = Time.realtimeSinceStartup + 10f;
            while (true) {
                if (gunSys.IsChamberEmpty()) {
                    MelonLogger.Msg(
                        $"[FCS] {leftRight}: inherited round was discharged externally");
                    yield break;
                }

                var reason = "waiting for safe-discharge solution";
                var solved = MapTable.TryRefreshTargetSolution(
                                 discharge, 0f,
                                 MapTable.IsFiringPlatformMoving
                                     ? gunSys.DirectFireLeadSeconds
                                     : 0f,
                                 false, out reason)
                             && gunSys.TrySolveElevation(
                                 discharge.distance, out elevation, out reason);
                if (!solved) {
                    // 火炮移动后原空放点可能脱离当前装药射程；立即依据实时炮位重选，
                    // 不等待停车，也不沿用失效角度。
                    if (gunSys.TryGetLoadedRange(
                            out var minRange, out var maxRange, out var rangeReason)
                        && MapTable.TryCreateSafeDischargeTask(
                            minRange, maxRange, loadedBullet,
                            out var replacement, out rangeReason,
                            range => gunSys.TrySolveElevation(range, out _, out _))) {
                        discharge = replacement;
                        reason = "safe-discharge point refreshed for current firing position";
                    }
                    else if (!string.IsNullOrWhiteSpace(rangeReason)) {
                        reason = rangeReason;
                    }
                    stableUpdates = 0;
                }
                else {
                    Turret.CommandRotation(discharge.angel);
                    gunSys.CommandElevation(elevation);
                    var rotationTolerance = MapTable.IsFiringPlatformMoving ? 0.6f : 0.25f;
                    var elevationTolerance = MapTable.IsFiringPlatformMoving ? 0.35f : 0.08f;
                    if (Turret.IsRotationReady(discharge.angel, rotationTolerance)
                        && gunSys.IsElevationReady(elevation, elevationTolerance)) {
                        ++stableUpdates;
                    }
                    else {
                        stableUpdates = 0;
                    }
                    var requiredStableUpdates = MapTable.IsFiringPlatformMoving
                        ? MovingSolutionStableUpdates
                        : 1;
                    if (stableUpdates >= requiredStableUpdates) break;
                }

                if (Time.realtimeSinceStartup >= nextTrackingLogAt) {
                    MelonLogger.Warning(
                        $"[FCS] {leftRight}: inherited round safe-discharge tracking continues; " +
                        $"reason={reason}");
                    nextTrackingLogAt = Time.realtimeSinceStartup + 10f;
                }
                yield return new WaitForSeconds(MovingSolutionUpdateInterval);
            }

            MelonLogger.Warning(
                $"[FCS] {leftRight}: firing inherited {loadedBullet} at verified safe-discharge " +
                $"point {discharge.angel:F1}deg/{discharge.distance:F2}km/{elevation:F2}deg; " +
                "scheduled target remains reserved");
            if (_sceneInteractor.AutoFire) {
                gunSys.RequestFireDirect();
                yield return gunSys.WaitFire();
            }
            else {
                // 手动击发模式继续尊重原有计算卡/确认台流程。
                yield return _deskLock.Acquire();
                try {
                    yield return BallisticCalculator.SetDistance(discharge.distance);
                    yield return BallisticCalculator.SetDirection(discharge.angel);
                    yield return BallisticCalculator.SetCharge(
                        Mathf.Clamp(gunSys.LoadedPowderCharges, 1, 6));
                    yield return BallisticCalculator.SetShellType(loadedBullet);
                    yield return BallisticCalculator.Calculate();
                    yield return TriggerConsole.ConfirmTask();
                    yield return TriggerConsole.ConfirmBullet();
                    yield return TriggerConsole.ConfirmRotation();
                    yield return TriggerConsole.ConfirmElevation();
                    yield return TriggerConsole.ReadyToFire();
                    yield return TriggerConsole.Arm(leftRight);
                    yield return gunSys.WaitFire();
                }
                finally {
                    _deskLock.Release();
                }
            }
        }
        finally {
            _turretLock.Release();
        }

        yield return gunSys.WaitBackToIdle();
        var nextEmptyLogAt = Time.realtimeSinceStartup + 10f;
        while (!gunSys.IsChamberEmpty()) {
            if (Time.realtimeSinceStartup >= nextEmptyLogAt) {
                MelonLogger.Warning(
                    $"[FCS] {leftRight}: inherited round fired; waiting for native chamber reset " +
                    $"without forced ejection ({gunSys.LoadedRoundDescription})");
                nextEmptyLogAt = Time.realtimeSinceStartup + 10f;
            }
            yield return new WaitForSeconds(0.5f);
        }
        MelonLogger.Msg(
            $"[FCS] {leftRight}: inherited round cleared; resuming scheduled task");
    }

    private IEnumerator RunTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;

        // 候选扫描与真正接手任务之间，目标或我方炮位都可能移动。这里只刷新到当前炮位，
        // 不再用固定 45 秒提前量猜测未来位置；击发前由连续跟踪负责实时收敛。
        var dynamicAtStart = task.isMoving || MapTable.IsFiringPlatformMoving;
        if (MapTable.IsFiringPlatformMoving) task.usesMovingPlatformSolution = true;
        if (dynamicAtStart) {
            var solutionReady = false;
            var solutionReason = "waiting for motion samples";
            var attempts = task.isMoving ? 8 : 1;
            for (var attempt = 0; attempt < attempts; ++attempt) {
                if (MapTable.TryRefreshTargetSolution(
                        task,
                        targetLeadSeconds: 0f,
                        firingPlatformLeadSeconds: 0f,
                        requireStableMotion: task.isMoving,
                        out solutionReason)) {
                    solutionReady = true;
                    break;
                }
                yield return new WaitForSeconds(0.5f);
            }
            if (!solutionReady) {
                MelonLogger.Warning(
                    $"[FCS] {leftRight}: dynamic solution canceled before loading: " +
                    $"{task.targetName} [{task.sourceEntityId}], reason={solutionReason}");
                task.progress = Progress.Failed;
                CompleteTask(leftRight, task);
                yield break;
            }
            if (task.usesMovingPlatformSolution) {
                MelonLogger.Msg(
                    $"[FCS] {leftRight}: fire-on-move current-position solution for {task.targetName} " +
                    $"({task.angel:F1}deg/{task.distance:F2}km, " +
                    $"platformSpeed={MapTable.FiringPlatformSpeedKmPerSecond:F3}km/s)");
            }
        }

        var powderCount = _sceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(task.distance);

        // ===== 临界区 1：采购 =====
        // 自动模式不再操作实体弹道计算器；这里只串行使用两管炮共用的采购台。
        float elevation = 0f;
        bool viable = true;
        yield return _deskLock.Acquire();
        try {
            // 装药不足则补购。单次采购未必补满（且偶发点击早于卡牌入槽而失败），
            // 故循环购买直到够本次发射所需，避免“装药不足但非 0”时直接推进、卡住后续装填。
            // 加购买次数上限兜底：采购始终无效时不至于无限循环（每次约 2.5s）。
            var plannedPurchaseCost = 0;
            if (gunSys.RemainingCharges() < powderCount) {
                plannedPurchaseCost += Math.Max(0, _purchaseDeck.GetPowderCost());
            }
            if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                plannedPurchaseCost += Math.Max(0, _purchaseDeck.GetShellCost(task.bulletType));
            }
            if (task.isAutoTarget && !CanSpendForAutoTask(plannedPurchaseCost)) {
                MelonLogger.Warning(
                    $"[FCS] {leftRight}: auto task canceled before purchasing " +
                    $"(total={RequisitionPointsMonitor.CurrentTotal}, plannedCost={plannedPurchaseCost}, " +
                    $"absoluteReserve={AbsoluteRequisitionReserve})");
                task.progress = Progress.Failed;
                viable = false;
            }

            var powderPurchaseAttempts = 0;
            while (viable && gunSys.RemainingCharges() < powderCount) {
                var powderCost = _purchaseDeck.GetPowderCost();
                if (task.isAutoTarget && !CanSpendForAutoTask(powderCost)) {
                    MelonLogger.Warning(
                        $"[FCS] {leftRight}: auto task canceled by RP reserve before powder purchase " +
                        $"(total={RequisitionPointsMonitor.CurrentTotal}, cost={powderCost}, " +
                        $"absoluteReserve={AbsoluteRequisitionReserve})");
                    task.progress = Progress.Failed;
                    viable = false;
                    break;
                }
                yield return _purchaseDeck.BuyPowders();
                if (++powderPurchaseAttempts >= 10) {
                    MelonLogger.Error(
                        $"[FCS] {leftRight} 炮管：购买装药 {powderPurchaseAttempts} 次后仍不足 " +
                        $"{powderCount}（当前 {gunSys.RemainingCharges()}），停止补购。");
                    break;
                }
            }

            if (gunSys.RemainingCharges() < powderCount) {
                task.progress = Progress.Failed;
                viable = false;
            }

            if (viable) task.progress = Progress.SelectingBullet;
            // 弹仓里没有目标弹种则采购（采购台也是共享硬件，放在锁内）。
            // 每次点击后读取真实弹仓结果；卡牌入槽或按钮偶发未响应时最多重试三次，
            // 所有弹种走同一条验证路径，避免某个新弹种静默进入后续装填并卡住炮管。
            var shellPurchaseAttempts = 0;
            while (viable && !gunSys.HaveBulletInCylinder(task.bulletType)) {
                if (!_purchaseDeck.HasShellCard(task.bulletType)) {
                    MelonLogger.Error(
                        $"[FCS] {leftRight}: no purchase card is bound for {task.bulletType}");
                    task.progress = Progress.Failed;
                    viable = false;
                }
                else if (!gunSys.HaveEmptyShellInCylinder()) {
                    task.progress = Progress.Failed;
                    viable = false;
                }
                else {
                    var shellCost = Math.Max(0, _purchaseDeck.GetShellCost(task.bulletType));
                    if (task.isAutoTarget && !CanSpendForAutoTask(shellCost)) {
                        MelonLogger.Warning(
                            $"[FCS] {leftRight}: auto task canceled by RP reserve before " +
                            $"{task.bulletType} purchase " +
                            $"(total={RequisitionPointsMonitor.CurrentTotal}, cost={shellCost}, " +
                            $"absoluteReserve={AbsoluteRequisitionReserve})");
                        task.progress = Progress.Failed;
                        viable = false;
                    }
                    else {
                        yield return _purchaseDeck.BuyShell(task.bulletType, leftRight);
                        ++shellPurchaseAttempts;
                        if (!gunSys.HaveBulletInCylinder(task.bulletType)
                            && shellPurchaseAttempts >= 3) {
                            MelonLogger.Error(
                                $"[FCS] {leftRight}: {task.bulletType} purchase did not place a " +
                                $"shell in the cylinder after {shellPurchaseAttempts} attempts");
                            task.progress = Progress.Failed;
                            viable = false;
                        }
                    }
                }
            }
        }
        finally {
            _deskLock.Release();
        }

        if (!viable) {
            // 此时尚未取得共享炮塔，只需释放炮管槽位。
            CompleteTask(leftRight, task);
            yield break;
        }

        // 解算一完成就开始低优先级预转向。该协程与本任务接下来的等待、装填和升仰角
        // 同时推进；旋转完成即释放共享炮塔，不会像旧流程那样持有到本管击发。
        // 热重载时，新 FSC 可能接手一发已在膛内、但不属于当前任务的旧炮弹。
        // 这时先不要把炮塔预转向当前目标；否则刚转过去又会被安全空放改向。
        var inheritedRoundPresent = !gunSys.IsChamberEmpty();
        var preRotationStarted = false;
        if (!inheritedRoundPresent) {
            StartPreRotation(leftRight, task);
            preRotationStarted = true;
        }

        // 允许下一任务在上一发的炮管复位期间提前完成解算与购弹，
        // 但同一炮管仍必须等到真正就绪后才能开始装填。
        yield return gunSys.WaitReadyForNextLoad();

        if (!gunSys.IsChamberEmpty()) {
            task.progress = Progress.WaitLoading;
            yield return DischargeInheritedRound(leftRight, gunSys, powderCount);
        }
        if (!preRotationStarted) {
            // 空放改变了共享炮塔方向，或上一发在等待期内自行完成了原生复位；
            // 此时恢复当前任务的提前转向，同时继续本炮装填。
            StartPreRotation(leftRight, task);
        }

        // ===== 锁外：装填（每管炮独立，最耗时段，可与另一管炮全程并行）=====
        task.progress = Progress.LoadingBullet;
        yield return gunSys.LoadBullet(task.bulletType);
        
        
        task.progress = Progress.LoadingPowder;
        yield return gunSys.LoadPowder(powderCount);
        task.progress = Progress.WaitLoading;
        var movementBlockedFireLogged = false;
        var canFireWaitStartedAt = Time.realtimeSinceStartup;
        var stationaryCanFireWarningAt = canFireWaitStartedAt + 10f;
        while (!gunSys.CanFire()) {
            if (MapTable.IsFiringPlatformMoving && !movementBlockedFireLogged) {
                movementBlockedFireLogged = true;
                MelonLogger.Warning(
                    $"[FCS] {leftRight}: gun CanFire=false while platform is moving; " +
                    "tracking continues and firing will occur as soon as the game permits");
            }
            // 即使游戏暂时禁止开火，也持续检查当前装药是否还能覆盖目标；首次确认
            // 脱离射程就立即改选，不等 CanFire 恢复或移动结束。
            if (task.isAutoTarget && !gunSys.IsChamberEmpty()) {
                var reachabilityReason = "";
                var platformLead = MapTable.IsFiringPlatformMoving
                    ? gunSys.DirectFireLeadSeconds
                    : 0f;
                var targetLead = task.isMoving
                    ? gunSys.PredictedImpactSeconds(task.distance) + platformLead
                    : 0f;
                var refreshed = MapTable.TryRefreshTargetSolution(
                    task, targetLead, platformLead,
                    requireStableMotion: task.isMoving,
                    out reachabilityReason);
                var reachable = refreshed
                                && gunSys.TrySolveElevation(
                                    task.distance, out _, out reachabilityReason);
                var targetInvalid = reachabilityReason.Contains("destroyed")
                                    || reachabilityReason.Contains("outside range")
                                    || reachabilityReason.Contains("no longer available")
                                    || reachabilityReason.Contains("cannot reach");
                if (!reachable && (refreshed || targetInvalid)) {
                    var recoveryReason = "recovery was not attempted";
                    if (TryRetargetOrPrepareSafeDischarge(
                            leftRight, task, gunSys, out recoveryReason)) {
                        canFireWaitStartedAt = Time.realtimeSinceStartup;
                    }
                    else {
                        // 已装弹药绝不强制退出。当前帧没有可达目标/空放点时保留炮弹，
                        // 下一周期继续根据实时炮位重选。
                        MelonLogger.Warning(
                            $"[FCS] {leftRight}: loaded round retained while waiting for " +
                            $"retarget/discharge opportunity; reason={recoveryReason}");
                    }
                }
            }
            if (!MapTable.IsFiringPlatformMoving
                && Time.realtimeSinceStartup >= stationaryCanFireWarningAt) {
                MelonLogger.Warning(
                    $"[FCS] {leftRight}: still waiting for CanFire while stationary " +
                    $"({gunSys.LoadedRoundDescription})");
                stationaryCanFireWarningAt = Time.realtimeSinceStartup + 10f;
            }
            if (!MapTable.IsFiringPlatformMoving
                && Time.realtimeSinceStartup - canFireWaitStartedAt >= 30f) {
                MelonLogger.Warning(
                    $"[FCS] {leftRight}: CanFire remained false for 30s while stationary; " +
                    $"round retained and waiting for native mechanism, " +
                    $"state={gunSys.LoadedRoundDescription}");
                canFireWaitStartedAt = Time.realtimeSinceStartup;
            }
            yield return new WaitForSeconds(1f);
        }
        if (movementBlockedFireLogged) {
            MelonLogger.Msg(
                $"[FCS] {leftRight}: gun CanFire became true; " +
                $"platformMoving={MapTable.IsFiringPlatformMoving}");
        }
        else if (MapTable.IsFiringPlatformMoving) {
            MelonLogger.Msg(
                $"[FCS] {leftRight}: gun CanFire=true while platform is moving");
        }

        // 炮弹与装药入膛后，直接反求炮管内部弹道模型。这里不碰实体计算器，
        // 因而没有 2.5 秒拨盘/计算等待，也不会生成左下角计算卡片。
        var initialDirectReason = "";
        var initialDirectReady = false;
        while (!initialDirectReady) {
            for (var attempt = 0; attempt < (task.isMoving ? 20 : 1); ++attempt) {
                var initialFlightSeconds = gunSys.PredictedImpactSeconds(task.distance);
                var initialTargetLead = task.isMoving ? initialFlightSeconds : 0f;
                if (MapTable.TryRefreshTargetSolution(
                        task,
                        initialTargetLead,
                        firingPlatformLeadSeconds: 0f,
                        requireStableMotion: task.isMoving,
                        out initialDirectReason)
                    && gunSys.TrySolveElevation(task.distance, out elevation, out initialDirectReason)) {
                    initialDirectReady = true;
                    break;
                }
                yield return new WaitForSeconds(0.5f);
            }
            if (initialDirectReady) break;

            var recoveryReason = "recovery was not attempted";
            if (TryRetargetOrPrepareSafeDischarge(
                    leftRight, task, gunSys, out recoveryReason)) {
                initialDirectReason = "retargeted loaded round";
                continue;
            }

            MelonLogger.Warning(
                $"[FCS] {leftRight}: loaded round retained; waiting to retarget or safe-discharge: " +
                $"{task.targetName} [{task.sourceEntityId}], solveReason={initialDirectReason}, " +
                $"recoveryReason={recoveryReason}");
            yield return new WaitForSeconds(1f);
        }

        // ===== 锁外：初始升仰角（每管炮独立，可与另一管方向/装填并行）=====
        task.progress = Progress.Aiming;
        yield return gunSys.SetElevation(elevation);

        // ===== 临界区 2：共享炮塔方向 + 击发 =====
        // 两管炮先分别完成最耗时的仰角调整；谁先就绪，谁先取得共享炮塔并发射。
        task.progress = Progress.WaitingForFire;
        MelonLogger.Msg(
            $"[FCS] {leftRight}: elevation ready for {task.targetName}, waiting for shared turret");
        yield return AcquireTurretForFire();
        try {
            // 持续读取真实炮位并直接更新方向和仰角。目标值连续三个周期都在机构误差内
            // 才允许击发；固定 45/8 秒提前量以及“算完后再等十几秒”的误差均被移除。
            var trackingStartedAt = Time.realtimeSinceStartup;
            var stableUpdates = 0;
            var directSolutionReady = false;
            var directSolutionReason = "waiting for direct solution";
            var solutionUpdates = 0;
            var unreachableUpdates = 0;
            var flightSeconds = gunSys.PredictedImpactSeconds(task.distance);
            while (!directSolutionReady) {
                var platformMoving = MapTable.IsFiringPlatformMoving;
                var fireCommandLead = platformMoving ? gunSys.DirectFireLeadSeconds : 0f;
                flightSeconds = gunSys.PredictedImpactSeconds(task.distance);
                var targetLeadSeconds = task.isMoving
                    ? flightSeconds + fireCommandLead
                    : 0f;

                var refreshed = MapTable.TryRefreshTargetSolution(
                    task,
                    targetLeadSeconds,
                    fireCommandLead,
                    requireStableMotion: task.isMoving,
                    out directSolutionReason);
                var solved = refreshed
                             && gunSys.TrySolveElevation(
                                 task.distance, out elevation, out directSolutionReason);
                if (solved) {
                    task.progress = Progress.Aiming;
                    Turret.CommandRotation(task.angel);
                    gunSys.CommandElevation(elevation);
                    ++solutionUpdates;

                    var rotationTolerance = platformMoving ? 0.6f : 0.25f;
                    var elevationTolerance = platformMoving ? 0.35f : 0.08f;
                    if (Turret.IsRotationReady(task.angel, rotationTolerance)
                        && gunSys.IsElevationReady(elevation, elevationTolerance)) {
                        ++stableUpdates;
                    }
                    else {
                        stableUpdates = 0;
                    }

                    var requiredStableUpdates = platformMoving || task.isMoving
                        ? MovingSolutionStableUpdates
                        : 1;
                    directSolutionReady = stableUpdates >= requiredStableUpdates;
                    unreachableUpdates = 0;
                }
                else {
                    stableUpdates = 0;
                    var targetInvalid = directSolutionReason.Contains("destroyed")
                                        || directSolutionReason.Contains("outside range")
                                        || directSolutionReason.Contains("no longer available")
                                        || directSolutionReason.Contains("cannot reach");
                    unreachableUpdates = refreshed || targetInvalid
                        ? unreachableUpdates + 1
                        : 0;
                }

                if (directSolutionReady) break;

                // 正向弹道模型已确认当前装药不可达时无需等待更多周期，立即改选目标/空放。
                if (task.isAutoTarget && unreachableUpdates >= 1) {
                    var recoveryReason = "recovery was not attempted";
                    if (TryRetargetOrPrepareSafeDischarge(
                            leftRight, task, gunSys, out recoveryReason)) {
                        unreachableUpdates = 0;
                        stableUpdates = 0;
                        trackingStartedAt = Time.realtimeSinceStartup;
                        continue;
                    }

                    MelonLogger.Warning(
                        $"[FCS] {leftRight}: loaded round retained; real-time retarget/discharge " +
                        $"will retry, reason={recoveryReason}");
                    unreachableUpdates = 0;
                    trackingStartedAt = Time.realtimeSinceStartup;
                }

                if (platformMoving
                    && Time.realtimeSinceStartup - trackingStartedAt >= MovingSolutionTimeout) {
                    MelonLogger.Warning(
                        $"[FCS] {leftRight}: real-time solution is still tracking after " +
                        $"{MovingSolutionTimeout:F0}s; correction continues without waiting for a stop, " +
                        $"reason={directSolutionReason}");
                    trackingStartedAt = Time.realtimeSinceStartup;
                    stableUpdates = 0;
                }
                else if (!platformMoving
                         && Time.realtimeSinceStartup - trackingStartedAt >= MovingSolutionTimeout) {
                    var recoveryReason = "recovery was not attempted";
                    if (TryRetargetOrPrepareSafeDischarge(
                            leftRight, task, gunSys, out recoveryReason)) {
                        trackingStartedAt = Time.realtimeSinceStartup;
                        stableUpdates = 0;
                        unreachableUpdates = 0;
                        continue;
                    }
                    MelonLogger.Warning(
                        $"[FCS] {leftRight}: direct solution timed out; loaded round retained " +
                        $"and recovery will retry: reason={directSolutionReason}; " +
                        $"recoveryReason={recoveryReason}");
                    trackingStartedAt = Time.realtimeSinceStartup;
                    stableUpdates = 0;
                    unreachableUpdates = 0;
                }
                yield return new WaitForSeconds(MovingSolutionUpdateInterval);
            }

            MelonLogger.Msg(
                $"[FCS] {leftRight}: real-time direct solution " +
                $"{task.targetName} [{task.sourceEntityId}], updates={solutionUpdates}, " +
                $"targetLead={task.predictedLeadSeconds:F2}s (flight={flightSeconds:F1}s), " +
                $"targetSpeed={MapTable.GetSpeedKmPerSecond(task.sourceVelocity):F3}km/s, " +
                $"platformLead={task.predictedPlatformLeadSeconds:F2}s, " +
                $"platformSpeed={MapTable.FiringPlatformSpeedKmPerSecond:F3}km/s, " +
                $"solution={task.angel:F1}deg/{task.distance:F2}km/{elevation:F2}deg");

            task.progress = Progress.WaitingForFire;
            MelonLogger.Msg(
                $"[FCS] {leftRight}: shared turret ready for {task.targetName} " +
                $"({task.angel:F1}deg/{task.distance:F2}km)");

            if (_sceneInteractor.AutoFire) {
                if (MapTable.IsFiringPlatformMoving) {
                    MelonLogger.Msg(
                        $"[FCS] {leftRight}: FIRE-ON-MOVE direct request for {task.targetName} " +
                        $"({task.angel:F1}deg/{task.distance:F2}km/{elevation:F2}deg)");
                }
                gunSys.RequestFireDirect();
                yield return gunSys.WaitFire();
            }
            else {
                // 手动击发模式保留原计算卡和确认台，避免改变玩家已有操作习惯。
                // 自动击发开启时不会进入此分支。
                yield return _deskLock.Acquire();
                try {
                    yield return BallisticCalculator.SetDistance(task.distance);
                    yield return BallisticCalculator.SetDirection(task.angel);
                    yield return BallisticCalculator.SetCharge(powderCount);
                    yield return BallisticCalculator.SetShellType(task.bulletType);
                    yield return BallisticCalculator.Calculate();
                    yield return TriggerConsole.ConfirmTask();
                    yield return TriggerConsole.ConfirmBullet();
                    yield return TriggerConsole.ConfirmRotation();
                    yield return TriggerConsole.ConfirmElevation();
                    yield return TriggerConsole.ReadyToFire();
                    yield return TriggerConsole.Arm(leftRight);
                    yield return gunSys.WaitFire();
                }
                finally {
                    _deskLock.Release();
                }
            }
        }
        finally {
            _turretLock.Release();
        }

        // 击发确认后立即释放任务槽，让下一任务开始解算和购弹。
        // 当前任务继续独立等待复位，结束时不再清空炮管槽，以免误删新任务。
        TrackFiredTarget(task);
        ReleaseSlot(leftRight);

        // ===== 锁外：回位（仰角回 0，每管炮独立，最耗时段之一）=====
        task.progress = Progress.BackToIdle;
        yield return gunSys.WaitBackToIdle();
        task.progress = Progress.Finished;
        _sceneInteractor.TaskFinished(task);
    }

    private sealed class PendingTargetOutcome {
        public string EntityId = "";
        public string TargetName = "";
        public float FiredAt;
        public float HitObservedAt;
        public float MissObservedAt;
        public bool IsHidden;
        public bool CounterObserved;
        public int HealthBeforeShot;
        public int MaxHealth;
        public int AreaTargetCount = 1;
        public List<string> CollateralTargetIds = new();
    }

    private sealed class AreaFirePlan {
        public ArtilleryTask Task = null!;
        public ShellBlastProfile Profile = null!;
        public List<string> CoveredIds = new();
        public Vector3 AimPoint;
        public float Clearance;
    }

}
