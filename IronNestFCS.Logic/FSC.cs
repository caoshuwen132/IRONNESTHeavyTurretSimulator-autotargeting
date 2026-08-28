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
    private const int HcheMinimumAreaTargets = 3;
    // Added to the shell's native impact radius.  This also gives direct-impact shells a
    // small exclusion zone instead of allowing an ally to overlap the aim point.
    private const float FriendlyFireSafetyMarginMission = 0.02f;
    private const float ManualFriendlyWarningInterval = 0.25f;
    private const int DesiredRequisitionFloor = 50;
    private const int AbsoluteRequisitionReserve = 1;

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
    // 单发预计无法消灭、或已经确认受伤存活的目标必须优先补射；普通战术优先级
    // 不得在目标死亡前把后续任务切走。
    private readonly HashSet<string> _forcedFollowUpTargetIds = new();
    private readonly List<PendingTargetOutcome> _pendingTargetOutcomes = new();
    private string _dualGunFocusTargetId = "";
    private ArtilleryTask? _dualGunFocusTemplate;
    private DualFireBarrier? _dualFireBarrier;
    private bool _targetStatsInitialized;
    private int _lastTargetsDestroyed;
    private int _lastHitsOnTargets;
    private int _lastMissedShots;
    private int _untrackedShotsInFlight;
    private int _entityOutcomeCountersToIgnore;
    private int _entityDestroyCountersToIgnore;
    // 炮兵与 FDC 同时可用时，首次从炮兵开始，此后在两个类别之间交替。
    private bool _preferArtilleryNext = true;
    private bool _budgetPauseLogged;
    private bool _powderBudgetPauseLogged;
    private int _lastLoggedValveCount = -1;
    private long _nextTaskScheduleOrder;
    private float _nextManualFriendlyWarningAt;

    /// <summary>当前各炮管正在执行的任务；null 表示该炮管空闲。供 UI 显示与调度判断。</summary>
    public ArtilleryTask? LeftTask { get; private set; }
    public ArtilleryTask? RightTask { get; private set; }

    /// <summary>等待派发的任务数（已入队但还没分到炮管）。供 UI 显示。</summary>
    public int PendingCount => _taskQueue.Count;
    public Queue<ArtilleryTask> QueueCan => new Queue<ArtilleryTask>(_taskQueue);
    public bool AutoTargetEnabled => _sceneInteractor.AutoTarget;
    public bool DesktopOnlyEnabled => _sceneInteractor.DesktopOnly;
    public bool ImpactPreviewEnabled => _sceneInteractor.ImpactPreview;
    public bool DualGunFocusEnabled => _sceneInteractor.DualGunFocus;
    public bool FreeCameraActive => _freeCamera.IsActive;
    public bool ColliderOverlayActive => _freeCamera.ColliderOverlayActive;
    public int DetectedEnemyCount { get; private set; }
    public bool AutoTargetBudgetPaused { get; private set; }
    public int RequisitionTarget => DesiredRequisitionFloor;
    public int DetectedValveCount { get; private set; }
    public int LooseValveCount { get; private set; }
    public int RepairedValveCount { get; private set; }
    public string ManualFriendlyFireWarning { get; private set; } = "";

    public float GetImpactPreviewRadiusMission(ArtilleryTask task) {
        return _purchaseDeck.TryGetShellProfile(task.bulletType, out var profile)
            ? Mathf.Max(0f, profile.ImpactRadiusMission)
            : 0f;
    }

    public float GetImpactPreviewSafetyRadiusMission(ArtilleryTask task) {
        return GetImpactPreviewRadiusMission(task) + FriendlyFireSafetyMarginMission;
    }

    public bool TryGetCurrentGunImpact(LeftRight side, out ArtilleryTask task) {
        task = null!;
        var gun = side == LeftRight.Left ? LeftGun : RightGun;
        if (!gun.TryGetCurrentBallisticState(
                out var bulletType, out var distanceKm, out _)
            || !Turret.TryGetCurrentDirection(out var directionDeg)) return false;

        return MapTable.TryCreateCurrentImpactTask(
            directionDeg,
            distanceKm,
            bulletType,
            side == LeftRight.Left ? "左炮当前落点" : "右炮当前落点",
            out task,
            out _);
    }

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
        if (IsBound) {
            MapTable.UpdateTurretMarker();
            UpdateManualFriendlyFireWarning();
        }
        _freeCamera.Update();
        // 自由视角只屏蔽测绘台点击，桌面落点和任务显示仍需持续刷新；否则 F10
        // 开启期间预览会冻结在旧位置，看起来像火控与桌面失去同步。
        _sceneInteractor.Update(allowClicks: !_freeCamera.IsActive);
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
        _forcedFollowUpTargetIds.Clear();
        _pendingTargetOutcomes.Clear();
        _dualGunFocusTargetId = "";
        _dualGunFocusTemplate = null;
        _dualFireBarrier = null;
        _targetStatsInitialized = false;
        _lastTargetsDestroyed = 0;
        _lastHitsOnTargets = 0;
        _lastMissedShots = 0;
        _untrackedShotsInFlight = 0;
        _entityOutcomeCountersToIgnore = 0;
        _entityDestroyCountersToIgnore = 0;
        _preferArtilleryNext = true;
        _budgetPauseLogged = false;
        _powderBudgetPauseLogged = false;
        _lastLoggedValveCount = -1;
        _nextTaskScheduleOrder = 0;
        _nextManualFriendlyWarningAt = 0f;
        ManualFriendlyFireWarning = "";
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
                _dualGunFocusTargetId = "";
                _dualGunFocusTemplate = null;
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

            var eligible = new List<ArtilleryTask>();
            foreach (var task in candidates) {
                if (_destroyedTargetIds.Contains(task.sourceEntityId)) continue;
                if (_autoTargetRetryAfter.TryGetValue(task.sourceEntityId, out var retryAfter)) {
                    if (Time.time < retryAfter) continue;
                    _autoTargetRetryAfter.Remove(task.sourceEntityId);
                }
                if (_autoTargetIds.Contains(task.sourceEntityId)) continue;
                eligible.Add(task);
            }

            PlanAreaShells(eligible);
            PlanEconomySingleTargetShells(eligible);

            // Shell selection can change the lethal radius after the entity scan.  Apply the
            // actual selected shell radius now so direct HE/DRIL shots receive the same ally
            // exclusion rule as explicit multi-target plans.
            for (var index = eligible.Count - 1; index >= 0; --index) {
                var candidate = eligible[index];
                if (TryValidateAutomaticFireSafety(candidate, out var safetyReason)) continue;
                MelonLogger.Warning(
                    $"[FCS] AutoTarget: deferred unsafe target {candidate.targetName} " +
                    $"[{candidate.sourceEntityId}], reason={safetyReason}");
                eligible.RemoveAt(index);
            }

            if (!_sceneInteractor.DualGunFocus) {
                _dualGunFocusTargetId = "";
                _dualGunFocusTemplate = null;
            }
            else if (!string.IsNullOrWhiteSpace(_dualGunFocusTargetId)) {
                var liveFocus = candidates.FirstOrDefault(
                    candidate => candidate.sourceEntityId == _dualGunFocusTargetId);
                if (liveFocus != null) {
                    PlanEconomySingleTargetShells(new List<ArtilleryTask> { liveFocus });
                    _dualGunFocusTemplate = CloneAutoTargetTask(liveFocus);
                }

                var focusTask = _dualGunFocusTemplate;
                while (available > 0
                       && focusTask != null
                       && GetCommittedExpectedDamage(_dualGunFocusTargetId)
                       < Math.Max(1, focusTask.sourceHealth)) {
                    var duplicate = CloneAutoTargetTask(focusTask);
                    if (!TryValidateAutomaticFireSafety(duplicate, out _)) break;
                    var committedPurchaseCost = CalculateCommittedAutoPurchaseCost();
                    var purchaseCost = EstimateNextAutoTaskPurchaseCost(duplicate);
                    if (!CanFundAutoTask(committedPurchaseCost, purchaseCost)) break;

                    _autoTargetIds.Add(duplicate.sourceEntityId);
                    MarkFollowUpIfNeeded(duplicate);
                    MelonLogger.Msg(
                        $"[FCS] Dual-gun focus: continued {duplicate.targetName} " +
                        $"[{duplicate.sourceEntityId}], HP={duplicate.sourceHealth}, " +
                        $"committedDamage={GetCommittedExpectedDamage(duplicate.sourceEntityId)}, " +
                        $"shell={duplicate.bulletType}");
                    EnqueueTask(duplicate);
                    available--;
                }

                if (focusTask == null
                    || GetCommittedExpectedDamage(_dualGunFocusTargetId)
                    < Math.Max(1, focusTask.sourceHealth)) {
                    // 预计总伤害仍不足、但暂时无法再派发（炮管占用、积分或安全限制）时，
                    // 保持主目标，不让普通优先级把任务切走。
                    continue;
                }

                if (CountUnfiredTargetCommitments(_dualGunFocusTargetId) > 0) {
                    // 预计伤害已经足够，但同目标仍有炮弹正在装填、瞄准或等待击发。
                    // 双炮合一模式必须等这些任务全部离膛后再成对切换，不能让先空闲的
                    // 炮管提前拿到另一个目标。
                    continue;
                }

                MelonLogger.Msg(
                    $"[FCS] Dual-gun focus: projected lethal damage committed to " +
                    $"{focusTask.targetName} [{_dualGunFocusTargetId}] " +
                    $"(HP={focusTask.sourceHealth}, " +
                    $"committedDamage={GetCommittedExpectedDamage(_dualGunFocusTargetId)}); " +
                    "searching the next target without waiting for impact");
                _dualGunFocusTargetId = "";
                _dualGunFocusTemplate = null;
            }

            // 新一轮“双炮合一”必须有两个空闲任务槽才开始。否则先派出单管任务会
            // 使另一门炮在下一轮拿到不同目标，无法进入同步开火屏障。
            if (_sceneInteractor.DualGunFocus && available < AutoTargetCapacity) continue;

            while (available > 0 && eligible.Count > 0) {
                var committedPurchaseCost = CalculateCommittedAutoPurchaseCost();
                var task = SelectNextAutoTarget(eligible);
                var purchaseCost = EstimateNextAutoTaskPurchaseCost(task);
                ArtilleryTask? pairedTask = null;
                if (_sceneInteractor.DualGunFocus
                    && GetExpectedDamage(task) < Math.Max(1, task.sourceHealth)) {
                    pairedTask = CloneAutoTargetTask(task);
                    // 单发不足以致命时，首轮合击固定为左右炮各一发。在任一任务
                    // 入队前一次性确认两门炮的购弹预算，避免只派出第一门炮后才
                    // 发现第二门无法购弹。
                    purchaseCost = EstimateTaskPurchaseCostForGun(task, LeftGun)
                                   + EstimateTaskPurchaseCostForGun(pairedTask, RightGun);
                }
                if (!CanFundAutoTask(committedPurchaseCost, purchaseCost)) {
                    AutoTargetBudgetPaused = true;
                    if (!_budgetPauseLogged) {
                        var total = RequisitionPointsMonitor.HasCurrentTotal
                            ? RequisitionPointsMonitor.CurrentTotal.ToString()
                            : "unknown";
                        MelonLogger.Warning(
                            $"[FCS] AutoTarget: insufficient RP to preserve the absolute reserve " +
                            $"(total={total}, target={task.targetName}, " +
                            $"committedCost={committedPurchaseCost}, purchaseCost={purchaseCost})");
                        _budgetPauseLogged = true;
                    }
                    break;
                }
                _budgetPauseLogged = false;

                eligible.Remove(task);
                if (!_autoTargetIds.Add(task.sourceEntityId)) continue;
                ReserveAreaCoverage(task, eligible);
                MarkFollowUpIfNeeded(task);
                if (_sceneInteractor.DualGunFocus) {
                    _dualGunFocusTargetId = task.sourceEntityId;
                    _dualGunFocusTemplate = CloneAutoTargetTask(task);
                }

                MelonLogger.Msg(
                    $"[FCS] AutoTarget: acquired {task.targetName} [{task.sourceEntityId}] " +
                    $"({task.angel:F1} deg, {task.distance:F2} km, " +
                    $"HP {task.sourceHealth}/{task.sourceMaxHealth}, armour {task.sourceArmour}, " +
                    $"stars {task.sourceStars}, role {task.sourceRole}, " +
                    $"state {task.sourceState}, " +
                    $"icon={task.sourceIcon}, sprite={task.sourceIconSprite}, " +
                    $"statusSprites=[{task.sourceStatusSprites}], " +
                    $"immune=[{task.sourceImmuneShells}], reward={RewardLogValue(task)}, " +
                    $"rewardSource={task.sourceRewardSource}, locomotive={task.isLocomotive}, " +
                    $"artillery={task.isArtillery}, " +
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

                if (_sceneInteractor.DualGunFocus) {
                    if (pairedTask != null) {
                        MelonLogger.Msg(
                            $"[FCS] Dual-gun focus: paired {pairedTask.targetName} " +
                            $"[{pairedTask.sourceEntityId}], shell={pairedTask.bulletType}");
                        EnqueueTask(pairedTask);
                        available--;
                    }
                    else {
                        MelonLogger.Msg(
                            $"[FCS] Dual-gun focus: single-shot lethal for {task.targetName} " +
                            $"[{task.sourceEntityId}]; holding the other gun to avoid overkill");
                    }
                    // 合击模式一轮只允许一个主目标。单发可致命时另一门炮保持空闲；
                    // 单发不足时两管锁定同一目标并进入同步开火屏障。
                    break;
                }
            }
        }
    }

    private void MarkFollowUpIfNeeded(ArtilleryTask task) {
        if (string.IsNullOrWhiteSpace(task.sourceEntityId)
            || task.sourceHealth <= 0
            || !_purchaseDeck.TryGetShellProfile(task.bulletType, out var profile)
            || !profile.CanKillTargets
            || !MapTable.IsShellEffectiveAgainst(task, task.bulletType)
            || GetExpectedDamage(task, profile) >= task.sourceHealth) return;

        if (_forcedFollowUpTargetIds.Add(task.sourceEntityId)) {
            MelonLogger.Msg(
                $"[FCS] Target tracking: {task.targetName} [{task.sourceEntityId}] " +
                $"needs follow-up fire (HP={task.sourceHealth}, " +
                $"expectedDamage={GetExpectedDamage(task, profile)}, " +
                $"armour={task.sourceArmour}, shell={task.bulletType})");
        }
    }

    private static int GetExpectedDamage(
        ArtilleryTask task, ShellBlastProfile profile) {
        // 本局幽灵炮台实测为 HP4/Armour1，AP 的原始 Damage2 每发只扣 1 点。
        // 使用游戏暴露的 Armour 值计算有效伤害，避免把两发原始伤害误判为足够致死。
        return Math.Max(0, profile.Damage - Math.Max(0, task.sourceArmour));
    }

    private int GetExpectedDamage(ArtilleryTask task) {
        return _purchaseDeck.TryGetShellProfile(task.bulletType, out var profile)
               && profile.CanKillTargets
               && MapTable.IsShellEffectiveAgainst(task, task.bulletType)
            ? GetExpectedDamage(task, profile)
            : 0;
    }

    private int GetCommittedExpectedDamage(string entityId) {
        var damage = 0;
        if (LeftTask?.sourceEntityId == entityId) damage += GetExpectedDamage(LeftTask);
        if (RightTask?.sourceEntityId == entityId) damage += GetExpectedDamage(RightTask);
        damage += _taskQueue
            .Where(task => task.sourceEntityId == entityId)
            .Sum(GetExpectedDamage);
        damage += _pendingTargetOutcomes
            .Where(pending => pending.EntityId == entityId)
            .Sum(pending => pending.ExpectedDamage);
        return damage;
    }

    private ArtilleryTask CloneAutoTargetTask(ArtilleryTask source) {
        var clone = new ArtilleryTask();
        MapTable.RetargetTask(clone, source);
        clone.targetId = source.targetId;
        clone.isAutoTarget = true;
        clone.progress = Progress.Pending;
        return clone;
    }

    private int CountTargetCommitments(
        string entityId, ArtilleryTask? excludedTask = null) {
        if (string.IsNullOrWhiteSpace(entityId)) return 0;
        var count = 0;
        if (!ReferenceEquals(LeftTask, excludedTask)
            && LeftTask?.sourceEntityId == entityId) count++;
        if (!ReferenceEquals(RightTask, excludedTask)
            && RightTask?.sourceEntityId == entityId) count++;
        count += _taskQueue.Count(task => !ReferenceEquals(task, excludedTask)
                                          && task.sourceEntityId == entityId);
        count += _pendingTargetOutcomes.Count(pending => pending.EntityId == entityId);
        return count;
    }

    private int CountUnfiredTargetCommitments(string entityId) {
        if (string.IsNullOrWhiteSpace(entityId)) return 0;
        var count = 0;
        if (LeftTask?.sourceEntityId == entityId) count++;
        if (RightTask?.sourceEntityId == entityId) count++;
        count += _taskQueue.Count(task => task.sourceEntityId == entityId);
        return count;
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

                // HCHE/HE 的覆盖数量门槛本身就是用户指定的采用条件：只要能够直接
                // 消灭相应数量的目标，就优先节省射击周期，不再与 DRIL 等逐发成本比较。
                // 其他范围弹仍需通过成本过滤，避免自动购买 ATMC 等极昂贵特种弹。
                if (profile.Type != BulletType.HCHE && profile.Type != BulletType.HE) {
                    var separateCost = 0;
                    foreach (var coveredTask in coveredTargets) {
                        separateCost += GetCheapestDirectKillCost(coveredTask);
                    }
                    if (profile.Cost > separateCost) continue;
                }

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

    /// <summary>
    /// 刷新正在执行的范围任务，并同步释放实时落点已经无法覆盖的附带目标。
    /// 若 HCHE 在购弹前降到三个目标以下，则撤销范围方案并重新选择单目标经济弹。
    /// </summary>
    private bool TryRefreshActiveTaskSolution(
        ArtilleryTask task,
        float targetLeadSeconds,
        float firingPlatformLeadSeconds,
        bool requireStableMotion,
        out string reason) {
        var previousCoveredIds = new HashSet<string>(task.areaCoveredTargetIds);
        var previouslyAreaPlanned = task.usesAreaAimPoint || task.areaTargetCount > 1;
        if (!MapTable.TryRefreshTargetSolution(
                task, targetLeadSeconds, firingPlatformLeadSeconds,
                requireStableMotion, out reason)) return false;

        var retained = new HashSet<string>(task.areaCoveredTargetIds);
        foreach (var entityId in previousCoveredIds) {
            if (retained.Contains(entityId)) continue;
            _autoTargetRetryAfter.Remove(entityId);
            MelonLogger.Msg(
                $"[FCS] Area fire plan: released {entityId}; " +
                "the live impact circle no longer covers it");
        }

        var mayReplanShell = task.progress <= Progress.SelectingBullet;
        var hcheBelowMinimum = task.bulletType == BulletType.HCHE
                               && task.areaTargetCount < HcheMinimumAreaTargets;
        var areaCollapsed = previouslyAreaPlanned && task.areaTargetCount <= 1;
        if (mayReplanShell && (hcheBelowMinimum || areaCollapsed)) {
            foreach (var entityId in task.areaCoveredTargetIds) {
                _autoTargetRetryAfter.Remove(entityId);
            }
            task.areaCoveredTargetIds.Clear();
            task.areaTargetCount = 1;
            task.impactRadiusKm = 0f;
            task.usesAreaAimPoint = false;
            task.areaAimOffsetFromPrimary = Vector3.zero;
            var previousType = task.bulletType;
            PlanEconomySingleTargetShells(new List<ArtilleryTask> { task });
            MelonLogger.Msg(
                $"[FCS] Area fire plan: live coverage fell below the required minimum; " +
                $"replanned {previousType}->{task.bulletType} for {task.targetName}");
        }
        return true;
    }

    private static bool CanDestroyWithProfile(
        ArtilleryTask task, ShellBlastProfile profile) {
        if (!profile.CanKillTargets
            || task.sourceHealth > 0
            && GetExpectedDamage(task, profile) < task.sourceHealth) return false;

        // 实测 FLCH 覆盖装甲/车辆群时只有其中的纯步兵产生奖励；PRPG 同样是
        // 针对人员的宣传/杀伤效果。它们的通用 Damage 字段不能证明可消灭车辆或设施。
        if (profile.Type == BulletType.FLCH || profile.Type == BulletType.PRPG) {
            return task.isInfantry;
        }
        return true;
    }

    private float GetShellSafetyRadiusMission(BulletType type) {
        var blastRadius = _purchaseDeck.TryGetShellProfile(type, out var profile)
            ? Mathf.Max(0f, profile.ImpactRadiusMission)
            : 0f;
        return blastRadius + FriendlyFireSafetyMarginMission;
    }

    private bool TryValidateAutomaticFireSafety(
        ArtilleryTask task, out string reason) {
        return MapTable.TryValidateAutomaticImpact(
            task, GetShellSafetyRadiusMission(task.bulletType), out reason);
    }

    private void UpdateManualFriendlyFireWarning() {
        var now = Time.realtimeSinceStartup;
        if (now < _nextManualFriendlyWarningAt) return;
        _nextManualFriendlyWarningAt = now + ManualFriendlyWarningInterval;
        ManualFriendlyFireWarning = "";

        IEnumerable<ArtilleryTask> ManualTasks() {
            if (LeftTask is { isAutoTarget: false }) yield return LeftTask;
            if (RightTask is { isAutoTarget: false }) yield return RightTask;
            foreach (var queued in _taskQueue) {
                if (!queued.isAutoTarget) yield return queued;
            }
        }

        var allyPositions = MapTable.GetLiveAllyPositions();
        foreach (var task in ManualTasks()) {
            var radius = GetShellSafetyRadiusMission(task.bulletType);
            if (!MapTable.HasPositionWithin(allyPositions, task.position, radius)) continue;
            var target = task.targetId > 0 ? $"T{task.targetId}" : task.targetName;
            ManualFriendlyFireWarning =
                $"\u8b66\u544a\uff1a{target}/{task.bulletType} " +
                "\u843d\u70b9\u9644\u8fd1\u6709\u53cb\u519b\uff01";
            return;
        }
    }

    private static bool IsBetterAreaProfile(
        ShellBlastProfile candidate, ShellBlastProfile current, BulletType originalType) {
        if (candidate.Cost != current.Cost) return candidate.Cost < current.Cost;
        var candidateKeepsType = candidate.Type == originalType;
        var currentKeepsType = current.Type == originalType;
        if (candidateKeepsType != currentKeepsType) return candidateKeepsType;
        if (candidate.ImpactRadiusMission != current.ImpactRadiusMission) {
            return candidate.ImpactRadiusMission < current.ImpactRadiusMission;
        }
        return candidate.Damage > current.Damage;
    }

    private int GetCheapestDirectKillCost(ArtilleryTask task) {
        var best = int.MaxValue;
        foreach (var profile in _purchaseDeck.BlastProfiles) {
            // HCHE 自身不能作为“逐个射击”的比较基准，否则会绕过三目标门槛。
            if (profile.Type == BulletType.HCHE) continue;
            if (!MapTable.IsShellEffectiveAgainst(task, profile.Type)) continue;
            if (!CanDestroyWithProfile(task, profile)) continue;
            best = Math.Min(best, Math.Max(0, profile.Cost));
        }
        return best != int.MaxValue
            ? best
            : Math.Max(0, _purchaseDeck.GetShellCost(task.bulletType));
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

    private bool CanFundAutoTask(int committedPurchaseCost, int nextPurchaseCost) {
        if (!RequisitionPointsMonitor.HasCurrentTotal) return false;
        var projected = RequisitionPointsMonitor.CurrentTotal
                        - committedPurchaseCost
                        - nextPurchaseCost;
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
        return EstimatedScoringReward(task) > 0;
    }

    private static int EstimatedScoringReward(ArtilleryTask task) {
        // 若游戏实体直接暴露了奖励值，优先采用游戏数据；-1 表示当前版本无法读取。
        if (task.sourceRewardPoints >= 0) return task.sourceRewardPoints;

        // 正式版 1577 实测日志：幽灵炮台 125、补给类 75、观测员 50、
        // 炮兵与指挥官 5。不能再使用 Demo 阶段的“星级×10”估算，也不能把
        // 炮兵/指挥官视为零分目标。尚无正式版证据的类别继续按 0 处理。
        if (task.sourceEntityId.StartsWith(
                "enemyironnest", StringComparison.OrdinalIgnoreCase)
            || task.sourceIconSprite.Contains(
                "Heavy_Gun_Turret", StringComparison.OrdinalIgnoreCase)) return 125;
        if (task.isSupply) return 75;
        if (task.isRecon) return 50;
        if (task.isArtillery || task.isCommander) return 5;
        return 0;
    }

    private int EstimateNextAutoTaskPurchaseCost(ArtilleryTask task) {
        var gunSys = LeftTask == null ? LeftGun : RightGun;
        return EstimateTaskPurchaseCostForGun(task, gunSys);
    }

    private int CalculateCommittedAutoPurchaseCost() {
        var cost = 0;
        if (NeedsAutoShellBudget(LeftTask)) {
            cost += EstimateTaskPurchaseCostForGun(LeftTask!, LeftGun);
        }
        if (NeedsAutoShellBudget(RightTask)) {
            cost += EstimateTaskPurchaseCostForGun(RightTask!, RightGun);
        }
        foreach (var task in _taskQueue) {
            if (!NeedsAutoShellBudget(task)) continue;
            // 队列任务尚未分配炮管，按两管中较高的采购需求保守预留。
            cost += Math.Max(
                EstimateTaskPurchaseCostForGun(task, LeftGun),
                EstimateTaskPurchaseCostForGun(task, RightGun));
        }
        return cost;
    }

    private int EstimateTaskPurchaseCostForGun(ArtilleryTask task, GunSystem gunSys) {
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

    private static bool NeedsAutoShellBudget(ArtilleryTask? task) {
        if (task == null || !task.isAutoTarget) return false;
        return task.progress == Progress.Pending
               || task.progress == Progress.Calculating
               || task.progress == Progress.SelectingBullet;
    }

    /// <summary>
    /// 战术优先级：列车目标高于所有新目标；炮兵与指挥官同时存在时从炮兵开始交替攻击。
    /// 防空炮紧随指挥官，排在其余支援与普通目标之前。
    /// 某一高优先类别暂时为空时立即选择另一类，不让空闲炮管等待。
    /// </summary>
    private ArtilleryTask SelectNextAutoTarget(
        List<ArtilleryTask> candidates,
        bool countSingleHighPrioritySelection = false) {
        // 已知需要补射的目标先于积分保护、炮兵/指挥官交替等所有常规规则。
        // 这只影响已经承诺攻击但未被一发摧毁的实体，不改变新目标之间的原优先级。
        var followUp = FindBestCandidate(
            candidates, task => _forcedFollowUpTargetIds.Contains(task.sourceEntityId));
        if (followUp != null) return followUp;

        var locomotive = FindBestCandidate(candidates, task => task.isLocomotive);
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
            task => !task.isLocomotive && !task.isArtillery
                    && !task.isCommander && !task.isAntiAir
                    && !task.isSupply && !task.isMechanized && !task.isRecon
                    && !task.isInfantry);
        var infantry = FindBestCandidate(candidates, task => task.isInfantry);
        var prioritizeScoring = scoring != null
                                && RequisitionPointsMonitor.HasCurrentTotal
                                && RequisitionPointsMonitor.CurrentTotal
                                <= DesiredRequisitionFloor
                                   + CalculateCommittedAutoPurchaseCost()
                                   + EstimateNextAutoTaskPurchaseCost(scoring);

        ArtilleryTask selected;
        var selectedFromAlternatingPair = false;
        if (locomotive != null) {
            selected = locomotive;
        }
        else if (prioritizeScoring) {
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
        else if (countSingleHighPrioritySelection
                 && (selected.isArtillery || selected.isCommander)) {
            // 遗留弹/射程重选只看得到当前弹种可达的局部候选。即使局部只有其中
            // 一类，这次真实攻击也应计入交替序列，不能把全局状态重置回炮兵。
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
                _forcedFollowUpTargetIds.Clear();
                _dualGunFocusTargetId = "";
                _dualGunFocusTemplate = null;
                _lastTargetsDestroyed = destroyed;
                _lastHitsOnTargets = hits;
                _lastMissedShots = misses;
                _untrackedShotsInFlight = 0;
                _entityOutcomeCountersToIgnore = 0;
                _entityDestroyCountersToIgnore = 0;
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

            ConsumeDeferredEntityCounters(ref hitDelta, ref missDelta, ref destroyedDelta);
            ResolveEntityStateOutcomes(ref hitDelta, ref missDelta, ref destroyedDelta);

            ResolvePendingDestroyedTargets(destroyedDelta);

            // 摧毁通常也计为命中；剩余命中暂缓数秒，等待可能延迟的摧毁计数。
            var survivingHitDelta = Math.Max(0, hitDelta - destroyedDelta);
            for (var i = 0; i < survivingHitDelta; i++) MarkPendingTargetHit();
            for (var i = 0; i < missDelta; i++) ResolvePendingMissedTarget();

            // 炮弹飞行、伤害结算和目标重试都属于游戏模拟时间；必须与
            // Time.timeScale 同步，否则倍速下炮弹已经落地仍会长期占用目标。
            var now = Time.time;
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
    private void ConsumeDeferredEntityCounters(
        ref int hitDelta, ref int missDelta, ref int destroyedDelta) {
        while (_entityOutcomeCountersToIgnore > 0 && (hitDelta > 0 || missDelta > 0)) {
            if (missDelta > 0) missDelta--;
            else hitDelta--;
            _entityOutcomeCountersToIgnore--;
            MelonLogger.Msg(
                $"[FCS] Target tracking: consumed deferred entity outcome counter, " +
                $"remaining={_entityOutcomeCountersToIgnore}");
        }
        if (_entityDestroyCountersToIgnore > 0 && destroyedDelta > 0) {
            var consumed = Math.Min(_entityDestroyCountersToIgnore, destroyedDelta);
            _entityDestroyCountersToIgnore -= consumed;
            destroyedDelta -= consumed;
            MelonLogger.Msg(
                $"[FCS] Target tracking: consumed {consumed} deferred destroy counter(s), " +
                $"remaining={_entityDestroyCountersToIgnore}");
        }
    }

    /// <summary>
    /// 双炮炮弹可能按与发射顺序不同的次序落地。优先读取每个在途目标自己的
    /// Health/State，把结果绑定到真实实体；只有实体状态尚未更新时才回退到全局计数。
    /// 隐藏实体的错误 miss 计数也在这里统一消费。
    /// </summary>
    private void ResolveEntityStateOutcomes(
        ref int hitDelta, ref int missDelta, ref int destroyedDelta) {
        for (var i = _pendingTargetOutcomes.Count - 1; i >= 0; i--) {
            var pending = _pendingTargetOutcomes[i];
            // 合击时同一实体会有两条在途记录。只让最早预计落地的一发结算本次
            // Health 变化，随后把另一发的生命基线更新到当前值，避免一次伤害被算两遍。
            var earlierPending = _pendingTargetOutcomes
                .Where(other => !ReferenceEquals(other, pending)
                                && other.EntityId == pending.EntityId)
                .OrderBy(other => other.ExpectedImpactAt)
                .FirstOrDefault();
            if (earlierPending != null
                && earlierPending.ExpectedImpactAt < pending.ExpectedImpactAt) continue;
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
                // 隐藏任务实测会把真实命中写成 miss；普通目标优先消费 hit。
                if (pending.IsHidden && missDelta > 0) {
                    missDelta--;
                    consumed = true;
                }
                else if (hitDelta > 0) {
                    hitDelta--;
                    consumed = true;
                }
                else if (missDelta > 0) {
                    missDelta--;
                    consumed = true;
                }
                if (!consumed) _entityOutcomeCountersToIgnore++;
            }
            if (destroyed) {
                var expectedDestroys = Math.Max(1, pending.AreaTargetCount);
                var consumedDestroys = Math.Min(destroyedDelta, expectedDestroys);
                destroyedDelta -= consumedDestroys;
                _entityDestroyCountersToIgnore += expectedDestroys - consumedDestroys;
            }

            if (destroyed) {
                MarkPendingTargetDestroyed(pending, "entity state confirmed");
                var redundantOutcomes = _pendingTargetOutcomes.RemoveAll(
                    other => other.EntityId == pending.EntityId);
                var redundantCountersNow = Math.Min(redundantOutcomes, hitDelta + missDelta);
                var consumedRedundantCounters = redundantCountersNow;
                while (redundantCountersNow > 0 && hitDelta > 0) {
                    hitDelta--;
                    redundantCountersNow--;
                }
                while (redundantCountersNow > 0 && missDelta > 0) {
                    missDelta--;
                    redundantCountersNow--;
                }
                _entityOutcomeCountersToIgnore += redundantOutcomes
                                                  - consumedRedundantCounters;
                i = Math.Min(i, _pendingTargetOutcomes.Count);
            }
            else {
                foreach (var other in _pendingTargetOutcomes) {
                    if (other.EntityId == pending.EntityId) {
                        other.HealthBeforeShot = health;
                    }
                }
                ReleasePendingTarget(pending, "entity damaged but survived");
            }
        }
    }

    private int FindBestPendingOutcomeIndex(bool preferObservedHit = false) {
        var now = Time.time;
        var bestIndex = -1;
        var bestScore = float.MaxValue;
        for (var i = 0; i < _pendingTargetOutcomes.Count; ++i) {
            var pending = _pendingTargetOutcomes[i];
            if (pending.HitObservedAt > 0f || pending.MissObservedAt > 0f) {
                if (!preferObservedHit || pending.HitObservedAt <= 0f) continue;
            }
            var score = Mathf.Abs(now - pending.ExpectedImpactAt);
            if (preferObservedHit && pending.HitObservedAt > 0f) score -= 1000f;
            if (score >= bestScore) continue;
            bestScore = score;
            bestIndex = i;
        }
        return bestIndex;
    }

    private void ResolvePendingDestroyedTargets(int destroyedCount) {
        var remaining = destroyedCount;
        while (remaining > 0) {
            if (_pendingTargetOutcomes.Count == 0) {
                MelonLogger.Msg(
                    $"[FCS] Target tracking: {remaining} destroy(s) observed without a tracked shot");
                return;
            }

            var index = FindBestPendingOutcomeIndex(preferObservedHit: true);
            if (index < 0) index = FindBestPendingOutcomeIndex();
            if (index < 0) return;
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
        _forcedFollowUpTargetIds.Remove(pending.EntityId);
        if (_dualGunFocusTargetId == pending.EntityId) {
            _dualGunFocusTargetId = "";
            _dualGunFocusTemplate = null;
        }
        ReleaseCollateralTargets(pending.CollateralTargetIds);
        MelonLogger.Msg(
            $"[FCS] Target tracking: destroyed {pending.TargetName} [{pending.EntityId}], " +
            $"reason={reason}");
    }

    private void MarkPendingTargetHit() {
        var index = FindBestPendingOutcomeIndex();
        if (index < 0) {
            MelonLogger.Msg("[FCS] Target tracking: hit observed without a tracked shot");
            return;
        }
        var pending = _pendingTargetOutcomes[index];
        pending.CounterObserved = true;
        pending.HitObservedAt = Time.time;
        MelonLogger.Msg(
            $"[FCS] Target tracking: hit observed for {pending.TargetName} [{pending.EntityId}]");
    }

    private void ResolvePendingMissedTarget() {
        var index = FindBestPendingOutcomeIndex();
        if (index < 0) {
            MelonLogger.Msg("[FCS] Target tracking: miss observed without a tracked shot");
            return;
        }
        var pending = _pendingTargetOutcomes[index];
        pending.CounterObserved = true;
        if (pending.IsHidden) {
            // 隐藏目标的 MissionStats miss 可能是伪结果；留出时间等待实体生命值/状态更新。
            pending.MissObservedAt = Time.time;
            MelonLogger.Msg(
                $"[FCS] Target tracking: hidden-target miss deferred for " +
                $"{pending.TargetName} [{pending.EntityId}]");
            return;
        }
        _pendingTargetOutcomes.RemoveAt(index);
        ReleasePendingTarget(pending, "missed");
    }

    private void ReleasePendingTarget(PendingTargetOutcome pending, string reason) {
        var requiresFollowUp = _forcedFollowUpTargetIds.Contains(pending.EntityId);
        var hasOtherCommitment = CountTargetCommitments(pending.EntityId) > 0;
        if (!hasOtherCommitment) _autoTargetIds.Remove(pending.EntityId);
        if (requiresFollowUp) {
            // 已确认单发不足时立即回到强制补射队列；如果另一发已经在装填或飞行，
            // 继续保留目标占用，等它先结算，避免无意义地追加过量炮弹。
            _autoTargetRetryAfter.Remove(pending.EntityId);
        }
        else {
            _autoTargetRetryAfter[pending.EntityId] =
                Time.time + AutoTargetRetryDelay;
        }
        ReleaseCollateralTargets(pending.CollateralTargetIds);
        MelonLogger.Msg(
            $"[FCS] Target tracking: released {pending.TargetName} [{pending.EntityId}], " +
            $"reason={reason}, followUp={requiresFollowUp}, " +
            $"otherCommitment={hasOtherCommitment}");
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
        if (task.preRotationRequested || task.progress >= Progress.BackToIdle) return;
        // 必须在启动协程前登记。否则同一帧派发的后续任务可能看不到旧任务已经申请
        // 预转向，两个协程会同时认为自己拥有前瞻方向。
        task.preRotationRequested = true;
        _coroutines.Start(PreRotateTurret(leftRight, task));
    }

    private IEnumerator PreRotateTurret(LeftRight leftRight, ArtilleryTask task) {
        var deferredLogged = false;
        while (task.progress < Progress.BackToIdle) {
            // 只有已经实际申请预转向的旧任务才可保留前瞻方向。仍在处理遗留弹、装药或
            // 机构恢复且尚未申请旋转的任务会让出炮塔，避免两管炮一起只升仰角不转向。
            while (HasOlderActiveTask(task) || _fireReadyTurretWaiters > 0) {
                // 本任务可能已由最终击发流程抢先完成；不能在发射后再转回旧目标。
                if (task.progress >= Progress.BackToIdle) yield break;
                if (!deferredLogged && HasOlderActiveTask(task)) {
                    MelonLogger.Msg(
                        $"[FCS] {leftRight}: pre-rotation deferred for {task.targetName}; " +
                        "an older pre-rotation request owns look-ahead rotation");
                    deferredLogged = true;
                }
                yield return null;
            }

            yield return _turretLock.Acquire();
            var retryAfterYieldingLock = false;
            try {
                // 等锁期间可能刚好有更早任务完成仰角。先让出共享炮塔，再回到循环继续
                // 等待；不能直接结束，否则会留下“已申请但从未旋转”的虚假所有权。
                if (task.progress >= Progress.BackToIdle) yield break;
                if (HasOlderActiveTask(task) || _fireReadyTurretWaiters > 0) {
                    retryAfterYieldingLock = true;
                }
                else {
                    MelonLogger.Msg(
                        $"[FCS] {leftRight}: pre-rotating shared turret for {task.targetName} " +
                        $"({task.angel:F1}deg) while loading/elevating");
                    yield return Turret.SetRotation(task.angel);
                    yield break;
                }
            }
            finally {
                _turretLock.Release();
            }

            if (retryAfterYieldingLock) yield return null;
        }
    }

    private bool HasOlderActiveTask(ArtilleryTask task) {
        return IsOlderActiveTask(LeftTask, task) || IsOlderActiveTask(RightTask, task);
    }

    private static bool IsOlderActiveTask(ArtilleryTask? candidate, ArtilleryTask task) {
        return candidate != null &&
               !ReferenceEquals(candidate, task) &&
               candidate.preRotationRequested &&
               candidate.progress < Progress.BackToIdle &&
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

    private bool CanUseSynchronizedDualFire(ArtilleryTask task) {
        if (!_sceneInteractor.DualGunFocus
            || !_sceneInteractor.AutoFire
            || !task.isAutoTarget
            || task.isSafeDischarge
            || string.IsNullOrWhiteSpace(task.sourceEntityId)
            || LeftTask == null
            || RightTask == null) return false;
        return LeftTask.isAutoTarget
               && RightTask.isAutoTarget
               && !LeftTask.isSafeDischarge
               && !RightTask.isSafeDischarge
               && LeftTask.sourceEntityId == task.sourceEntityId
               && RightTask.sourceEntityId == task.sourceEntityId;
    }

    private IEnumerator TrySynchronizedDualFire(
        LeftRight side, ArtilleryTask task, DualFireParticipant participant) {
        if (!CanUseSynchronizedDualFire(task)) yield break;

        var leftTask = LeftTask!;
        var rightTask = RightTask!;
        var barrier = _dualFireBarrier;
        if (barrier == null
            || !ReferenceEquals(barrier.LeftTask, leftTask)
            || !ReferenceEquals(barrier.RightTask, rightTask)) {
            barrier = new DualFireBarrier {
                LeftTask = leftTask,
                RightTask = rightTask
            };
            _dualFireBarrier = barrier;
        }

        participant.Handled = true;
        if (side == LeftRight.Left) barrier.LeftReady = true;
        else barrier.RightReady = true;
        task.progress = Progress.WaitingForFire;
        MelonLogger.Msg(
            $"[FCS] {side}: dual-fire ready for {task.targetName}; " +
            "waiting for the other gun");

        while (!barrier.Completed && !barrier.Failed) {
            if (!barrier.FireRequested
                && (!ReferenceEquals(LeftTask, barrier.LeftTask)
                || !ReferenceEquals(RightTask, barrier.RightTask)
                || barrier.LeftTask.progress is Progress.Failed or Progress.Finished
                || barrier.RightTask.progress is Progress.Failed or Progress.Finished)) {
                barrier.Failed = true;
                break;
            }

            if (barrier.LeftReady && barrier.RightReady && !barrier.LeaderStarted) {
                barrier.LeaderStarted = true;
                yield return ExecuteSynchronizedDualFire(barrier);
                continue;
            }
            yield return null;
        }

        if (barrier.Failed && !barrier.FireRequested) {
            participant.Handled = false;
            MelonLogger.Warning(
                $"[FCS] {side}: synchronized fire was canceled; falling back to normal fire");
            yield break;
        }

        participant.FlightSeconds = side == LeftRight.Left
            ? barrier.LeftFlightSeconds
            : barrier.RightFlightSeconds;
    }

    private IEnumerator ExecuteSynchronizedDualFire(DualFireBarrier barrier) {
        yield return AcquireTurretForFire();
        try {
            var stableUpdates = 0;
            while (!barrier.Failed) {
                if (!ReferenceEquals(LeftTask, barrier.LeftTask)
                    || !ReferenceEquals(RightTask, barrier.RightTask)
                    || barrier.LeftTask.sourceEntityId != barrier.RightTask.sourceEntityId) {
                    barrier.Failed = true;
                    break;
                }

                var platformMoving = MapTable.IsFiringPlatformMoving;
                var fireCommandLead = platformMoving
                    ? Math.Max(LeftGun.DirectFireLeadSeconds, RightGun.DirectFireLeadSeconds)
                    : 0f;
                var leftFlight = LeftGun.PredictedImpactSeconds(barrier.LeftTask.distance);
                var rightFlight = RightGun.PredictedImpactSeconds(barrier.RightTask.distance);
                var leftTargetLead = barrier.LeftTask.isMoving
                    ? leftFlight + fireCommandLead
                    : 0f;
                var rightTargetLead = barrier.RightTask.isMoving
                    ? rightFlight + fireCommandLead
                    : 0f;

                var leftElevation = 0f;
                var rightElevation = 0f;
                var leftReady = TryRefreshActiveTaskSolution(
                                    barrier.LeftTask, leftTargetLead, fireCommandLead,
                                    requireStableMotion: barrier.LeftTask.isMoving, out _)
                                && TryValidateAutomaticFireSafety(barrier.LeftTask, out _)
                                && LeftGun.TrySolveElevation(
                                    barrier.LeftTask.distance, out leftElevation, out _);
                var rightReady = TryRefreshActiveTaskSolution(
                                     barrier.RightTask, rightTargetLead, fireCommandLead,
                                     requireStableMotion: barrier.RightTask.isMoving, out _)
                                 && TryValidateAutomaticFireSafety(barrier.RightTask, out _)
                                 && RightGun.TrySolveElevation(
                                     barrier.RightTask.distance, out rightElevation, out _);
                if (!leftReady || !rightReady) {
                    stableUpdates = 0;
                    yield return new WaitForSeconds(MovingSolutionUpdateInterval);
                    continue;
                }

                // 同一目标的两条解算应具有相同方向；取环形平均值可吸收同帧采样的微小差异。
                var directionDelta = Mathf.DeltaAngle(
                    barrier.LeftTask.angel, barrier.RightTask.angel);
                var sharedDirection = barrier.LeftTask.angel + directionDelta * 0.5f;
                Turret.CommandRotation(sharedDirection);
                LeftGun.CommandElevation(leftElevation);
                RightGun.CommandElevation(rightElevation);

                var rotationTolerance = platformMoving ? 0.6f : 0.25f;
                var elevationTolerance = platformMoving ? 0.35f : 0.08f;
                var mechanismsReady = Turret.IsRotationReady(
                                          sharedDirection, rotationTolerance)
                                      && LeftGun.IsElevationReady(
                                          leftElevation, elevationTolerance)
                                      && RightGun.IsElevationReady(
                                          rightElevation, elevationTolerance);
                stableUpdates = mechanismsReady ? stableUpdates + 1 : 0;
                var requiredUpdates = platformMoving
                                      || barrier.LeftTask.isMoving
                                      || barrier.RightTask.isMoving
                    ? MovingSolutionStableUpdates
                    : 1;
                if (stableUpdates < requiredUpdates) {
                    yield return new WaitForSeconds(MovingSolutionUpdateInterval);
                    continue;
                }

                barrier.LeftFlightSeconds = leftFlight;
                barrier.RightFlightSeconds = rightFlight;
                MelonLogger.Msg(
                    $"[FCS] Dual-fire: both guns ready for {barrier.LeftTask.targetName} " +
                    $"[{barrier.LeftTask.sourceEntityId}], direction={sharedDirection:F1}deg, " +
                    $"elevation={leftElevation:F2}/{rightElevation:F2}deg");

                // 两个 RequestFire 调用之间没有 yield，Unity 会在同一帧收到左右炮击发请求。
                LeftGun.RequestFireDirect();
                RightGun.RequestFireDirect();
                barrier.FireRequested = true;
                yield return LeftGun.WaitFire();
                yield return RightGun.WaitFire();
                barrier.Completed = true;
            }
        }
        finally {
            _turretLock.Release();
        }
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

    private void TrackFiredTarget(ArtilleryTask task, float predictedFlightSeconds) {
        if (!string.IsNullOrEmpty(task.sourceEntityId)) {
            var firedAt = Time.time;
            _pendingTargetOutcomes.Add(new PendingTargetOutcome {
                EntityId = task.sourceEntityId,
                TargetName = task.targetName,
                FiredAt = firedAt,
                ExpectedImpactAt = firedAt + Mathf.Max(0.1f, predictedFlightSeconds),
                IsHidden = task.isHidden,
                HealthBeforeShot = task.sourceHealth,
                MaxHealth = task.sourceMaxHealth,
                ExpectedDamage = GetExpectedDamage(task),
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
            var hasOtherCommitment = CountTargetCommitments(
                task.sourceEntityId, excludedTask: task) > 0;
            if (!hasOtherCommitment) {
                _autoTargetIds.Remove(task.sourceEntityId);
                _autoTargetRetryAfter[task.sourceEntityId] =
                    Time.time + retryDelay;
                _forcedFollowUpTargetIds.Remove(task.sourceEntityId);
            }
            MelonLogger.Msg(
                $"[FCS] Target tracking: released {task.targetName} [{task.sourceEntityId}], " +
                $"reason={reason}, otherCommitment={hasOtherCommitment}");
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
        var retryAt = Time.time + AutoTargetRetryDelay;
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

        var canRetargetLoadedRound =
            _purchaseDeck.TryGetShellProfile(loadedBullet, out var loadedProfile)
            && loadedProfile.CanKillTargets;

        var reachable = new List<ArtilleryTask>();
        if (canRetargetLoadedRound) {
            foreach (var candidate in MapTable.GetAutoTargets(
                         loadedBullet,
                         desktopVisibleOnly: _sceneInteractor.DesktopOnly)) {
                if (candidate.bulletType != loadedBullet) continue;
                if (!MapTable.IsShellEffectiveAgainst(candidate, loadedBullet)) continue;
                if (!CanDestroyWithProfile(candidate, loadedProfile)) continue;
                if (candidate.sourceEntityId == task.sourceEntityId) continue;
                if (_destroyedTargetIds.Contains(candidate.sourceEntityId)) continue;
                if (_autoTargetIds.Contains(candidate.sourceEntityId)) continue;
                if (_autoTargetRetryAfter.TryGetValue(candidate.sourceEntityId, out var retryAfter)
                    && Time.time < retryAfter) continue;
                if (!gunSys.TrySolveElevation(candidate.distance, out _, out _)) continue;
                if (!TryValidateAutomaticFireSafety(candidate, out _)) continue;
                reachable.Add(candidate);
            }
        }

        var oldTargetName = task.targetName;
        var oldTargetId = task.sourceEntityId;
        if (reachable.Count > 0) {
            var replacement = SelectNextAutoTarget(
                reachable, countSingleHighPrioritySelection: true);
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
                range => gunSys.TrySolveElevation(range, out _, out _),
                minimumClearanceMission: GetShellSafetyRadiusMission(loadedBullet))) {
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
    /// 为热重载/任务交接遗留在炮膛内的弹药寻找真实可杀伤目标。AP 同样可以攻击
    /// 普通目标；是否可用由弹药的直接杀伤能力、目标免疫、剩余生命、射程和友军安全
    /// 共同决定，而不是仅依据“目标是否要求穿甲”。
    /// </summary>
    private bool HasTargetForInheritedCharge(
        GunSystem gunSys, BulletType loadedBullet, ArtilleryTask scheduledTask,
        int chargeCount, out string reason) {
        reason = "no directly killable target is inside this charge range";
        if (!_purchaseDeck.TryGetShellProfile(loadedBullet, out var profile)
            || !profile.CanKillTargets) {
            reason = $"loaded {loadedBullet} shell is not a direct-kill round";
            return false;
        }
        if (!gunSys.TryGetRangeForCharge(
                chargeCount, out var minRange, out var maxRange, out reason)) return false;

        bool IsCandidateInRange(ArtilleryTask candidate, bool ignoreReservation) {
            if (string.IsNullOrWhiteSpace(candidate.sourceEntityId)) return false;
            if (candidate.isSafeDischarge) return false;
            if (_destroyedTargetIds.Contains(candidate.sourceEntityId)) return false;
            if (!ignoreReservation && _autoTargetIds.Contains(candidate.sourceEntityId)) return false;
            if (!ignoreReservation
                && _autoTargetRetryAfter.TryGetValue(candidate.sourceEntityId, out var retryAfter)
                && Time.time < retryAfter) return false;
            if (!MapTable.IsShellEffectiveAgainst(candidate, loadedBullet)) return false;
            if (!CanDestroyWithProfile(candidate, profile)) return false;
            if (!MapTable.TryRefreshTargetSolution(
                    candidate, 0f, 0f, false, out _)) return false;
            if (candidate.distance < minRange - 0.05f
                || candidate.distance > maxRange + 0.05f) return false;
            return TryValidateAutomaticFireSafety(candidate, out _);
        }

        if (scheduledTask.isAutoTarget
            && IsCandidateInRange(scheduledTask, ignoreReservation: true)) return true;

        foreach (var candidate in MapTable.GetAutoTargets(
                     loadedBullet,
                     desktopVisibleOnly: _sceneInteractor.DesktopOnly)) {
            if (candidate.sourceEntityId == scheduledTask.sourceEntityId) continue;
            if (IsCandidateInRange(candidate, ignoreReservation: false)) return true;
        }

        reason = $"no target for loaded {loadedBullet} inside " +
                 $"{minRange:F2}-{maxRange:F2}km with {chargeCount} charge(s)";
        return false;
    }

    private bool TrySelectTargetForInheritedRound(
        GunSystem gunSys, BulletType loadedBullet, ArtilleryTask scheduledTask,
        out ArtilleryTask target, out bool fulfillsScheduledTask, out string reason) {
        target = null!;
        fulfillsScheduledTask = false;
        reason = "no directly killable target is available";

        if (!_purchaseDeck.TryGetShellProfile(loadedBullet, out var profile)
            || !profile.CanKillTargets) {
            reason = $"loaded {loadedBullet} shell is not a direct-kill round";
            return false;
        }

        var reachable = new List<ArtilleryTask>();

        bool TryAddCandidate(ArtilleryTask candidate, bool ignoreReservation) {
            if (string.IsNullOrWhiteSpace(candidate.sourceEntityId)) return false;
            if (candidate.isSafeDischarge) return false;
            if (_destroyedTargetIds.Contains(candidate.sourceEntityId)) return false;
            if (!ignoreReservation && _autoTargetIds.Contains(candidate.sourceEntityId)) return false;
            if (!ignoreReservation
                && _autoTargetRetryAfter.TryGetValue(candidate.sourceEntityId, out var retryAfter)
                && Time.time < retryAfter) return false;
            if (!MapTable.IsShellEffectiveAgainst(candidate, loadedBullet)) return false;
            if (!CanDestroyWithProfile(candidate, profile)) return false;

            // 范围任务可能预约了多个附带目标。不同弹种的遗留弹只可处理单目标任务，
            // 避免把一次 AP 命中错误地当作整组 HCHE 覆盖已经完成。
            if (ReferenceEquals(candidate, scheduledTask)
                && candidate.areaTargetCount > 1
                && candidate.bulletType != loadedBullet) return false;

            if (!MapTable.TryRefreshTargetSolution(
                    candidate, 0f, 0f, false, out _)) return false;
            if (!gunSys.TrySolveElevation(candidate.distance, out _, out _)) return false;
            if (!TryValidateAutomaticFireSafety(candidate, out _)) return false;
            reachable.Add(candidate);
            return true;
        }

        // 当前排定目标已经被本炮预约，因此允许绕过“已预约”过滤；若遗留弹能直接
        // 消灭它，就可以用这发完成任务，无需先空放再装一发。
        if (scheduledTask.isAutoTarget) {
            TryAddCandidate(scheduledTask, ignoreReservation: true);
        }

        foreach (var candidate in MapTable.GetAutoTargets(
                     loadedBullet,
                     desktopVisibleOnly: _sceneInteractor.DesktopOnly)) {
            if (candidate.bulletType != loadedBullet) continue;
            if (candidate.sourceEntityId == scheduledTask.sourceEntityId) continue;
            TryAddCandidate(candidate, ignoreReservation: false);
        }

        if (reachable.Count == 0) return false;

        target = SelectNextAutoTarget(
            reachable, countSingleHighPrioritySelection: true);
        fulfillsScheduledTask = ReferenceEquals(target, scheduledTask);
        reason = fulfillsScheduledTask
            ? "the inherited round can fulfill the scheduled target"
            : "a reachable unreserved target accepts the inherited round";
        return true;
    }

    /// <summary>
    /// 热重载可能发生在原逻辑已经把炮弹推进炮膛、但尚未击发的时刻。新 FSC 实例并不知道
    /// 这发炮弹属于哪个旧任务，若直接装入新任务弹种，原生推杆会因为炮膛非空而永远不可用。
    /// 不强制退弹：先让膛内弹药攻击能够直接杀伤、射程可达且通过友军安全检查的目标；确实没有
    /// 合适目标时才寻找地图内空放点。若遗留弹完成了当前任务，调用方不会再对同一目标重复发射。
    /// </summary>
    /// <summary>
    /// 手动模式下完成确认、待击发后，既接受玩家手动击发，也持续观察自动开火开关。
    /// 若炮管等待期间重新开启自动开火，立即向已经就绪的炮管发送原生击发请求。
    /// </summary>
    private IEnumerator WaitForManualFireOrAutoEnable(
        LeftRight leftRight, GunSystem gunSys, string targetName) {
        while (!gunSys.IsNativeReloadPending) {
            if (_sceneInteractor.AutoFire) {
                MelonLogger.Msg(
                    $"[FCS] {leftRight}: AutoFire re-enabled while ready; " +
                    $"requesting direct fire for {targetName}");
                gunSys.RequestFireDirect();
                break;
            }

            yield return new WaitForSeconds(0.1f);
        }

        yield return gunSys.WaitFire();
    }

    private IEnumerator DischargeInheritedRound(
        LeftRight leftRight, GunSystem gunSys, int fallbackPowderCount,
        ArtilleryTask scheduledTask, InheritedRoundResult result) {
        var loadedShellId = gunSys.BulletInChamber();
        if (string.IsNullOrWhiteSpace(loadedShellId)) yield break;

        MelonLogger.Warning(
            $"[FCS] {leftRight}: inherited chambered round detected after task hand-off; " +
            $"a reachable target will be preferred before safe discharge " +
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
        // 先复用托盘上已经选好的装药；它能覆盖任一安全可杀伤目标时直接推入。
        // 只有当前托盘装药没有可达目标时，才补到出现可达目标的最低数量。
        if (gunSys.LoadedPowderCharges <= 0) {
            var stagedCharge = gunSys.StagedPowderCharges;
            var inheritedCharge = Mathf.Clamp(fallbackPowderCount, 1, 6);
            if (stagedCharge > 0) {
                inheritedCharge = stagedCharge;
                if (!HasTargetForInheritedCharge(
                        gunSys, loadedBullet, scheduledTask,
                        stagedCharge, out var stagedReason)) {
                    for (var supplementedCharge = stagedCharge + 1;
                         supplementedCharge <= 6;
                         ++supplementedCharge) {
                        if (!HasTargetForInheritedCharge(
                                gunSys, loadedBullet, scheduledTask,
                                supplementedCharge, out _)) continue;
                        inheritedCharge = supplementedCharge;
                        break;
                    }
                    MelonLogger.Msg(
                        $"[FCS] {leftRight}: staged {loadedBullet} charge range has no target; " +
                        $"supplement plan={stagedCharge}->{inheritedCharge}, reason={stagedReason}");
                }
                else {
                    MelonLogger.Msg(
                        $"[FCS] {leftRight}: reusing all {stagedCharge} staged charge(s) " +
                        $"for inherited {loadedBullet}; a killable target is already in range");
                }
            }
            MelonLogger.Warning(
                $"[FCS] {leftRight}: inherited {loadedBullet} has no powder; " +
                $"staged={stagedCharge}, preparing {inheritedCharge} charge(s) so the round can be reused");
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

        ArtilleryTask? aimTask = null;
        var firesAtTarget = false;
        var fulfillsScheduledTask = false;
        var extraTargetReserved = false;
        float elevation = 0f;

        if (TrySelectTargetForInheritedRound(
                gunSys, loadedBullet, scheduledTask,
                out var inheritedTarget, out fulfillsScheduledTask, out var targetReason)) {
            if (fulfillsScheduledTask
                || _autoTargetIds.Add(inheritedTarget.sourceEntityId)) {
                aimTask = inheritedTarget;
                firesAtTarget = true;
                extraTargetReserved = !fulfillsScheduledTask;
                MelonLogger.Warning(
                    $"[FCS] {leftRight}: inherited {loadedBullet} assigned to " +
                    $"{inheritedTarget.targetName} [{inheritedTarget.sourceEntityId}] " +
                    $"instead of safe discharge ({targetReason})");
            }
            else {
                targetReason = "selected target was reserved concurrently";
            }
        }

        var nextSolutionLogAt = Time.realtimeSinceStartup;
        while (aimTask == null) {
            if (gunSys.IsChamberEmpty()) yield break;

            var reason = "loaded range is not ready";
            if (gunSys.TryGetLoadedRange(out var minRange, out var maxRange, out reason)
                && MapTable.TryCreateSafeDischargeTask(
                    minRange, maxRange, loadedBullet, out var candidate, out reason,
                    range => gunSys.TrySolveElevation(range, out _, out _),
                    minimumClearanceMission: GetShellSafetyRadiusMission(loadedBullet))
                && MapTable.TryRefreshTargetSolution(
                    candidate, 0f, 0f, false, out reason)
                && TryValidateAutomaticFireSafety(candidate, out reason)
                && gunSys.TrySolveElevation(candidate.distance, out elevation, out reason)) {
                aimTask = candidate;
                break;
            }

            if (Time.realtimeSinceStartup >= nextSolutionLogAt) {
                MelonLogger.Warning(
                    $"[FCS] {leftRight}: inherited round retained while searching for a " +
                    $"target or ballistically reachable safe-discharge point; " +
                    $"targetReason={targetReason}, dischargeReason={reason}");
                nextSolutionLogAt = Time.realtimeSinceStartup + 10f;
            }
            yield return new WaitForSeconds(1f);
        }

        // 目标分支先完成一次当前距离的仰角解算；仰角机构与共享炮塔旋转随后并行，
        // 避免遗留弹复用流程退化成“先抬完仰角、再开始旋转”。之后仍持续实时校正。
        if (!gunSys.TrySolveElevation(aimTask.distance, out elevation, out _)) {
            elevation = 0f;
        }
        var inheritedElevation = StartParallelElevation(gunSys, elevation);
        yield return AcquireTurretForFire();
        try {
            MelonLogger.Msg(
                $"[FCS] {leftRight}: inherited round rotating and elevating in parallel for " +
                $"{aimTask.targetName} ({aimTask.angel:F1}deg/{elevation:F2}deg)");
            Turret.CommandRotation(aimTask.angel);
            while (!inheritedElevation.Finished) {
                // 旋转控制器保持目标值；仰角子协程在同一帧序列中独立推进。
                Turret.CommandRotation(aimTask.angel);
                yield return null;
            }

            var stableUpdates = 0;
            var nextTrackingLogAt = Time.realtimeSinceStartup + 10f;
            while (true) {
                if (gunSys.IsChamberEmpty()) {
                    if (extraTargetReserved && !string.IsNullOrWhiteSpace(aimTask.sourceEntityId)) {
                        _autoTargetIds.Remove(aimTask.sourceEntityId);
                    }
                    MelonLogger.Msg(
                        $"[FCS] {leftRight}: inherited round was discharged externally");
                    yield break;
                }

                var platformLead = MapTable.IsFiringPlatformMoving
                    ? gunSys.DirectFireLeadSeconds
                    : 0f;
                var flightSeconds = gunSys.PredictedImpactSeconds(aimTask.distance);
                var targetLead = firesAtTarget && aimTask.isMoving
                    ? flightSeconds + platformLead
                    : 0f;
                var reason = firesAtTarget
                    ? "waiting for inherited-round target solution"
                    : "waiting for safe-discharge solution";
                var solved = TryRefreshActiveTaskSolution(
                                 aimTask, targetLead, platformLead,
                                 firesAtTarget && aimTask.isMoving, out reason)
                             && TryValidateAutomaticFireSafety(aimTask, out reason)
                             && gunSys.TrySolveElevation(
                                 aimTask.distance, out elevation, out reason);
                if (!solved) {
                    // 目标在等待期间消失、移出射程或变得不安全时，释放额外预约并退回
                    // 地图内安全空放；绝不沿用已经失效的目标坐标。
                    if (extraTargetReserved && !string.IsNullOrWhiteSpace(aimTask.sourceEntityId)) {
                        _autoTargetIds.Remove(aimTask.sourceEntityId);
                        _autoTargetRetryAfter[aimTask.sourceEntityId] =
                            Time.time + AutoTargetRetryDelay;
                        extraTargetReserved = false;
                    }
                    if (TrySelectTargetForInheritedRound(
                            gunSys, loadedBullet, scheduledTask,
                            out var replacementTarget, out var replacementFulfillsScheduled,
                            out var replacementReason)
                        && (replacementFulfillsScheduled
                            || _autoTargetIds.Add(replacementTarget.sourceEntityId))) {
                        aimTask = replacementTarget;
                        firesAtTarget = true;
                        fulfillsScheduledTask = replacementFulfillsScheduled;
                        extraTargetReserved = !replacementFulfillsScheduled;
                        reason = $"inherited round retargeted: {replacementReason}";
                    }
                    else if (gunSys.TryGetLoadedRange(
                            out var minRange, out var maxRange, out var rangeReason)
                        && MapTable.TryCreateSafeDischargeTask(
                            minRange, maxRange, loadedBullet,
                            out var replacement, out rangeReason,
                            range => gunSys.TrySolveElevation(range, out _, out _),
                            minimumClearanceMission: GetShellSafetyRadiusMission(loadedBullet))) {
                        aimTask = replacement;
                        firesAtTarget = false;
                        fulfillsScheduledTask = false;
                        reason = "target became invalid; safe-discharge point selected for current firing position";
                    }
                    else if (!string.IsNullOrWhiteSpace(rangeReason)) {
                        reason = rangeReason;
                    }
                    stableUpdates = 0;
                }
                else {
                    Turret.CommandRotation(aimTask.angel);
                    gunSys.CommandElevation(elevation);
                    var rotationTolerance = MapTable.IsFiringPlatformMoving ? 0.6f : 0.25f;
                    var elevationTolerance = MapTable.IsFiringPlatformMoving ? 0.35f : 0.08f;
                    if (Turret.IsRotationReady(aimTask.angel, rotationTolerance)
                        && gunSys.IsElevationReady(elevation, elevationTolerance)) {
                        ++stableUpdates;
                    }
                    else {
                        stableUpdates = 0;
                    }
                    var requiredStableUpdates = MapTable.IsFiringPlatformMoving
                                                || firesAtTarget && aimTask.isMoving
                        ? MovingSolutionStableUpdates
                        : 1;
                    if (stableUpdates >= requiredStableUpdates) break;
                }

                if (Time.realtimeSinceStartup >= nextTrackingLogAt) {
                    MelonLogger.Warning(
                        $"[FCS] {leftRight}: inherited round " +
                        $"{(firesAtTarget ? "target" : "safe-discharge")} tracking continues; " +
                        $"reason={reason}");
                    nextTrackingLogAt = Time.realtimeSinceStartup + 10f;
                }
                yield return new WaitForSeconds(MovingSolutionUpdateInterval);
            }

            if (firesAtTarget) {
                MelonLogger.Warning(
                    $"[FCS] {leftRight}: firing inherited {loadedBullet} at " +
                    $"{aimTask.targetName} [{aimTask.sourceEntityId}] " +
                    $"({aimTask.angel:F1}deg/{aimTask.distance:F2}km/{elevation:F2}deg); " +
                    $"scheduledTaskFulfilled={fulfillsScheduledTask}");
            }
            else {
                MelonLogger.Warning(
                    $"[FCS] {leftRight}: no killable target remains in loaded range; firing inherited " +
                    $"{loadedBullet} at verified safe-discharge point " +
                    $"{aimTask.angel:F1}deg/{aimTask.distance:F2}km/{elevation:F2}deg");
            }
            if (_sceneInteractor.AutoFire) {
                gunSys.RequestFireDirect();
                yield return gunSys.WaitFire();
            }
            else {
                // 手动击发模式继续尊重原有计算卡/确认台流程。
                yield return _deskLock.Acquire();
                try {
                    yield return BallisticCalculator.SetDistance(aimTask.distance);
                    yield return BallisticCalculator.SetDirection(aimTask.angel);
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
                    yield return WaitForManualFireOrAutoEnable(
                        leftRight, gunSys, aimTask.targetName);
                }
                finally {
                    _deskLock.Release();
                }
            }

            if (firesAtTarget) {
                TrackFiredTarget(
                    aimTask, gunSys.PredictedImpactSeconds(aimTask.distance));
                result.FulfilledScheduledTask = fulfillsScheduledTask;
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
        // 炮弹离膛不等于装药台已经完成原生复位。再次等待空膛后的 pendingReload
        // 清除，避免随后恢复原任务时把上一发正在消失的托盘装药当成新装药。
        yield return gunSys.WaitReadyForNextLoad();
        MelonLogger.Msg(result.FulfilledScheduledTask
            ? $"[FCS] {leftRight}: inherited round cleared; scheduled target was fulfilled"
            : $"[FCS] {leftRight}: inherited round cleared; resuming scheduled task");
    }

    private IEnumerator RunTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;

        // 候选扫描与真正接手任务之间，目标或我方炮位都可能移动。这里只刷新到当前炮位，
        // 不再用固定 45 秒提前量猜测未来位置；击发前由连续跟踪负责实时收敛。
        var dynamicAtStart = task.isMoving || MapTable.IsFiringPlatformMoving;
        if (MapTable.IsFiringPlatformMoving) task.usesMovingPlatformSolution = true;
        if (dynamicAtStart || task.isAutoTarget) {
            var solutionReady = false;
            var solutionReason = "waiting for motion samples";
            var attempts = task.isMoving ? 8 : 1;
            for (var attempt = 0; attempt < attempts; ++attempt) {
                if (TryRefreshActiveTaskSolution(
                        task,
                        targetLeadSeconds: 0f,
                        firingPlatformLeadSeconds: 0f,
                        requireStableMotion: task.isMoving,
                        out solutionReason)
                    && TryValidateAutomaticFireSafety(task, out solutionReason)) {
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
        var liveInheritedRoundAtStart = !gunSys.IsChamberEmpty()
                                        && !gunSys.IsNativeReloadPending;
        var preRotationStarted = false;

        // 初始落点一经确认便立即登记预转向，让共享炮塔在购弹、等待机构和装药期间
        // 同步旋转。只有真正未击发的遗留弹先保留当前方向，避免随即又转去处理遗留弹。
        if (!liveInheritedRoundAtStart) {
            StartPreRotation(leftRight, task);
            preRotationStarted = true;
        }

        // 热重载留下的未击发炮弹必须在采购当前任务弹药之前处理。pendingReload=true
        // 表示上一发正在原生复位，不属于可复用遗留弹；这种情况仍允许提前采购。
        if (liveInheritedRoundAtStart) {
            task.progress = Progress.WaitLoading;
            var earlyInheritedResult = new InheritedRoundResult();
            yield return DischargeInheritedRound(
                leftRight, gunSys, powderCount, task, earlyInheritedResult);
            if (earlyInheritedResult.FulfilledScheduledTask) {
                ReleaseSlot(leftRight);
                task.progress = Progress.Finished;
                _sceneInteractor.TaskFinished(task);
                yield break;
            }
            // 遗留弹发射改变了炮塔方向；处理完成后马上为原计划重新预转向，采购过程
            // 与旋转同时进行。
            StartPreRotation(leftRight, task);
            preRotationStarted = true;
        }

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

        // 预转向通常已在初始落点确认后启动。这里仅保留异常状态兜底。
        var inheritedRoundPresent = !gunSys.IsChamberEmpty();
        if (!inheritedRoundPresent && !preRotationStarted) {
            StartPreRotation(leftRight, task);
            preRotationStarted = true;
        }

        // 允许下一任务在上一发的炮管复位期间提前完成解算与购弹，
        // 但同一炮管仍必须等到真正就绪后才能开始装填。
        yield return gunSys.WaitReadyForNextLoad();

        if (!gunSys.IsChamberEmpty()) {
            task.progress = Progress.WaitLoading;
            var inheritedResult = new InheritedRoundResult();
            yield return DischargeInheritedRound(
                leftRight, gunSys, powderCount, task, inheritedResult);
            if (inheritedResult.FulfilledScheduledTask) {
                // 遗留弹已经完成当前目标；不再重复装弹、瞄准和发射同一任务。
                ReleaseSlot(leftRight);
                task.progress = Progress.Finished;
                _sceneInteractor.TaskFinished(task);
                yield break;
            }
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
        if (gunSys.LoadedPowderCharges > 0
            && gunSys.LoadedPowderCharges != powderCount) {
            MelonLogger.Msg(
                $"[FCS] {leftRight}: using actual loaded powder count " +
                $"{gunSys.LoadedPowderCharges} instead of planned {powderCount}");
            powderCount = gunSys.LoadedPowderCharges;
        }
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
                var refreshed = TryRefreshActiveTaskSolution(
                    task, targetLead, platformLead,
                    requireStableMotion: task.isMoving,
                    out reachabilityReason);
                var reachable = refreshed
                                && TryValidateAutomaticFireSafety(task, out reachabilityReason)
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
                if (TryRefreshActiveTaskSolution(
                        task,
                        initialTargetLead,
                        firingPlatformLeadSeconds: 0f,
                        requireStableMotion: task.isMoving,
                        out initialDirectReason)
                    && TryValidateAutomaticFireSafety(task, out initialDirectReason)
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

        // 双炮合一且左右任务确实指向同一实体时，先到位的炮管在这里等待另一管。
        // 两管就绪后由同一个协调者完成最终实时校正，并在同一帧发出两条击发请求。
        var synchronizedFire = new DualFireParticipant();
        yield return TrySynchronizedDualFire(
            leftRight, task, synchronizedFire);
        if (synchronizedFire.Handled) {
            TrackFiredTarget(task, synchronizedFire.FlightSeconds);
            ReleaseSlot(leftRight);
            task.progress = Progress.BackToIdle;
            yield return gunSys.WaitBackToIdle();
            task.progress = Progress.Finished;
            _sceneInteractor.TaskFinished(task);
            yield break;
        }

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

                var refreshed = TryRefreshActiveTaskSolution(
                    task,
                    targetLeadSeconds,
                    fireCommandLead,
                    requireStableMotion: task.isMoving,
                    out directSolutionReason);
                var solved = refreshed
                             && TryValidateAutomaticFireSafety(task, out directSolutionReason)
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
                    // 静止炮位的大角度旋转可能自然超过 30 秒。这里不能仅因机构尚未
                    // 收敛就改选目标，否则两个相反方向的候选会每 30 秒互相切换。
                    // 目标死亡、失效、变为友军或真实弹道不可达已由上方即时恢复分支处理。
                    MelonLogger.Warning(
                        $"[FCS] {leftRight}: stationary aiming is still converging after " +
                        $"{MovingSolutionTimeout:F0}s; current target is retained, " +
                        $"reason={directSolutionReason}");
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
                    yield return WaitForManualFireOrAutoEnable(
                        leftRight, gunSys, task.targetName);
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
        TrackFiredTarget(task, gunSys.PredictedImpactSeconds(task.distance));
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
        public float ExpectedImpactAt;
        public float HitObservedAt;
        public float MissObservedAt;
        public bool IsHidden;
        public bool CounterObserved;
        public int HealthBeforeShot;
        public int MaxHealth;
        public int ExpectedDamage;
        public int AreaTargetCount = 1;
        public List<string> CollateralTargetIds = new();
    }

    private sealed class InheritedRoundResult {
        public bool FulfilledScheduledTask;
    }

    private sealed class DualFireParticipant {
        public bool Handled;
        public float FlightSeconds;
    }

    private sealed class DualFireBarrier {
        public ArtilleryTask LeftTask = null!;
        public ArtilleryTask RightTask = null!;
        public bool LeftReady;
        public bool RightReady;
        public bool LeaderStarted;
        public bool FireRequested;
        public bool Completed;
        public bool Failed;
        public float LeftFlightSeconds;
        public float RightFlightSeconds;
    }

    private sealed class AreaFirePlan {
        public ArtilleryTask Task = null!;
        public ShellBlastProfile Profile = null!;
        public List<string> CoveredIds = new();
        public Vector3 AimPoint;
        public float Clearance;
    }

}
