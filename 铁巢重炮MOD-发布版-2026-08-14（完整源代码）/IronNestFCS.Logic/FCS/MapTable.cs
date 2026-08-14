using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class MapTable {
    private const float MapDistanceScale = 3.8164f;
    private const float AutoTargetMaxRangeKm = 30f;
    private const float MaxMovingTargetSpeedKmPerSecond = 0.05f;
    private const float TargetMovingSpeedThresholdKmPerSecond = 0.002f;
    private const float MaxMovingLeadDistanceKm = 2f;
    private const float MaxMovingPlatformLeadDistanceKm = 5f;
    private const float FiringPlatformMovingSpeedThresholdKmPerSecond = 0.002f;
    private const string FiringPlatformMotionId = "__firing_platform__";
    private const float TurretMarkerSyncInterval = 0.2f;
    private Transform? turret;
    private TurretController? firingTurret;
    private Dictionary<int, Transform> artilleries = new();
    private readonly Dictionary<string, MapEntity> knownEntities = new();
    private readonly Dictionary<string, EntityLocation> knownLocations = new();
    private readonly Dictionary<string, MotionTrack> motionTracks = new();
    private Transform? fireMissionRoot;
    private FireMission? fireMission;
    private float nextTurretMarkerSyncAt;
    private bool turretMarkerSyncLogged;
    private Vector3 turretMarkerOriginalLocalScale = Vector3.one;
    private float turretMarkerRootLocalZ;
    
    public bool TryBind() {
        artilleries = new Dictionary<int, Transform>();
        knownEntities.Clear();
        knownLocations.Clear();
        motionTracks.Clear();
        var turretObject = GameObject.Find("Player Turret Piece");
        if (turretObject == null) {
            MelonLogger.Warning(
                "[FCS] 未找到 Player Turret Piece；桌面炮位标志无法同步，" +
                "但真实炮位解算仍可继续");
        }

        var mapObject = GameObject.Find("Draggable Surface");
        if (mapObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Draggable Surface，当前场景尚未就绪");
            return false;
        }

        turret = turretObject?.transform;
        var turretSystem = GameObject.Find("TurretSystem");
        firingTurret = turretSystem?.GetComponent<TurretController>();
        if (firingTurret?.turretBase == null) {
            MelonLogger.Warning("[FCS] 未找到 TurretController.turretBase，无法读取火炮真实位置");
            return false;
        }
        var map = mapObject.transform;
        for (var i = 0; i < map.childCount; ++i) {
            var t = map.GetChild(i);
            if (t.name != "MapToken_Artillery") continue;
            var tmp = t.GetComponentInChildren<Il2CppTMPro.TextMeshPro>();
            if (tmp == null) continue;
            if (!int.TryParse(tmp.text, out var id)) continue;
            artilleries.Add(id, t);
        }
        MelonLogger.Msg(
            $"[FCS] Player Turret Piece: {(turret == null ? "not found" : turret.ToString())}, " +
            $"Artilleries: {artilleries.Count}");
        var fireMissionObject = GameObject.Find("Fire Mission Root");
        if (fireMissionObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Fire Mission Root，当前场景尚未就绪");
            return false;
        }

        fireMissionRoot = fireMissionObject.transform;
        fireMission = fireMissionRoot.GetComponent<FireMission>();
        if (fireMission == null) return false;

        if (turret != null && fireMission.coordinateRoot != null) {
            // 必须在第一次同步前保存。PositionInRootSpace 会改变图标层级/缩放，
            // 这里只记录场景原本给 Player Turret Piece 设置的尺寸和地图深度。
            turretMarkerOriginalLocalScale = turret.localScale;
            turretMarkerRootLocalZ = fireMission.coordinateRoot
                .InverseTransformPoint(turret.position).z;
        }
        nextTurretMarkerSyncAt = 0f;
        turretMarkerSyncLogged = false;
        UpdateTurretMarker(true);
        return true;
    }

    /// <summary>
    /// 将测绘台上的 Player Turret Piece 同步到火炮真实位置。射击解算始终直接读取
    /// TurretController.turretBase；此方法只维护桌面显示及原版交互兼容性。
    /// </summary>
    public void UpdateTurretMarker(bool force = false) {
        if (turret == null || fireMission?.coordinateRoot == null
                           || firingTurret?.turretBase == null) return;

        var now = Time.realtimeSinceStartup;
        if (!force && now < nextTurretMarkerSyncAt) return;
        nextTurretMarkerSyncAt = now + TurretMarkerSyncInterval;

        try {
            var actualPosition = fireMission.ToLocalSpace(firingTurret.turretBase.position);
            // PositionInRootSpace 适合新生成的任务标记，但会重设现有炮位模型的
            // 层级/缩放。这里只换算并写入世界位置，保留标志原有尺寸和朝向。
            var rootLocalPosition = new Vector3(
                actualPosition.x, actualPosition.y, turretMarkerRootLocalZ);
            turret.position = fireMission.coordinateRoot.TransformPoint(rootLocalPosition);
            turret.localScale = turretMarkerOriginalLocalScale;
            if (!turretMarkerSyncLogged) {
                turretMarkerSyncLogged = true;
                MelonLogger.Msg(
                    $"[FCS] Player Turret Piece auto-sync enabled: " +
                    $"mission=({actualPosition.x:F3},{actualPosition.y:F3}), " +
                    $"interval={TurretMarkerSyncInterval:F1}s");
            }
        }
        catch (Exception ex) {
            // 标志仅用于显示；同步异常不得中断自动索敌或真实炮位解算。
            nextTurretMarkerSyncAt = now + 2f;
            MelonLogger.Warning($"[FCS] Player Turret Piece auto-sync failed: {ex.Message}");
        }
    }

    public ArtilleryTask? GetMarkTarget(int index) {
        if (index > artilleries.Count) {
            MelonLogger.Error($"[FCS] GetMarkTarget: index {index} out of range, artillery count: {artilleries.Count}");
            return null;
        }

        if (fireMission == null) return null;
        var marker = artilleries[index];
        var markerPoint = GetMarkerPointerWorldPosition(marker, out var pointerSource);
        var missionPosition = fireMission.ToLocalSpace(markerPoint);
        MelonLogger.Msg(
            $"[FCS] Manual target T{index}: pointer={pointerSource}, " +
            $"mission=({missionPosition.x:F3},{missionPosition.y:F3})");
        return CreateTask(new Vector3(missionPosition.x, missionPosition.y, 0f));
    }

    private static Vector3 GetMarkerPointerWorldPosition(
        Transform marker, out string source) {
        source = "transform pivot";
        try {
            var rect = marker.GetComponent<RectTransform>();
            if (rect == null) return marker.position;

            // 地图指针若以底部为 pivot，Transform.position 已经是尖角，不能再减一次高度。
            if (rect.pivot.y <= 0.15f) {
                source = $"bottom pivot ({rect.pivot.x:F2},{rect.pivot.y:F2})";
                return marker.position;
            }

            var localTip = new Vector3(rect.rect.center.x, rect.rect.yMin, 0f);
            var worldTip = rect.TransformPoint(localTip);
            source = $"rect bottom ({rect.pivot.x:F2},{rect.pivot.y:F2})";
            return worldTip;
        }
        catch (Exception ex) {
            source = $"transform pivot fallback ({ex.Message})";
            return marker.position;
        }
    }

    /// <summary>
    /// 将手动点击的编号目标与 FireMission 中最近的真实实体关联起来。
    /// 手动任务原本只有坐标，没有实体 ID，自动索敌因此不知道已有炮弹正在飞向该目标。
    /// </summary>
    public bool TryAttachSourceEntity(ArtilleryTask task, BulletType bulletType) {
        var candidates = GetAutoTargets(bulletType);
        ArtilleryTask? best = null;
        var bestScore = float.MaxValue;

        foreach (var candidate in candidates) {
            var angleDelta = Mathf.Abs(Mathf.DeltaAngle(task.angel, candidate.angel));
            var distanceDelta = Mathf.Abs(task.distance - candidate.distance);
            if (angleDelta > 1f || distanceDelta > 0.4f) continue;

            var score = distanceDelta + angleDelta * 0.1f;
            if (score >= bestScore) continue;
            best = candidate;
            bestScore = score;
        }

        if (best == null) return false;
        CopySourceMetadata(task, best);
        return true;
    }

    private static void CopySourceMetadata(ArtilleryTask target, ArtilleryTask source) {
        target.targetName = source.targetName;
        target.sourceEntityId = source.sourceEntityId;
        target.isHidden = source.isHidden;
        target.isMoving = source.isMoving;
        target.isUnderground = source.isUnderground;
        target.requiresAp = source.requiresAp;
        target.isCommander = source.isCommander;
        target.isArtillery = source.isArtillery;
        target.isAntiAir = source.isAntiAir;
        target.isSupply = source.isSupply;
        target.isMechanized = source.isMechanized;
        target.isRecon = source.isRecon;
        target.isInfantry = source.isInfantry;
        target.isShip = source.isShip;
        target.areaTargetCount = source.areaTargetCount;
        target.impactRadiusKm = source.impactRadiusKm;
        target.areaCoveredTargetIds = new List<string>(source.areaCoveredTargetIds);
        target.usesAreaAimPoint = source.usesAreaAimPoint;
        target.areaAimOffsetFromPrimary = source.areaAimOffsetFromPrimary;
        target.isSafeDischarge = source.isSafeDischarge;
        target.usesMovingPlatformSolution = source.usesMovingPlatformSolution;
        target.predictedPlatformLeadSeconds = source.predictedPlatformLeadSeconds;
        target.predictedFiringOrigin = source.predictedFiringOrigin;
        target.sourceIcon = source.sourceIcon;
        target.sourceIconSprite = source.sourceIconSprite;
        target.sourceStatusSprites = source.sourceStatusSprites;
        target.sourceImmuneShells = source.sourceImmuneShells;
        target.sourceRewardPoints = source.sourceRewardPoints;
        target.sourceRewardSource = source.sourceRewardSource;
        target.sourceHealth = source.sourceHealth;
        target.sourceMaxHealth = source.sourceMaxHealth;
        target.sourceArmour = source.sourceArmour;
        target.sourceStars = source.sourceStars;
        target.sourceRole = source.sourceRole;
        target.sourceState = source.sourceState;
        target.sourceVelocity = source.sourceVelocity;
        target.motionSamples = source.motionSamples;
        target.predictedLeadSeconds = source.predictedLeadSeconds;
    }

    public static void RetargetTask(ArtilleryTask target, ArtilleryTask source) {
        CopySourceMetadata(target, source);
        target.angel = source.angel;
        target.distance = source.distance;
        target.position = source.position;
        target.bulletType = source.bulletType;
        target.isAutoTarget = source.isAutoTarget;
    }

    /// <summary>
    /// 返回当前已经被任务系统发现且仍存活的敌军，并按距离由近到远生成火控任务。
    /// Hidden 目标仍遵守游戏任务系统的发现规则；移动炮兵在取得稳定速度样本后也可进入任务队列。
    /// </summary>
    public List<ArtilleryTask> GetAutoTargets(
        BulletType bulletType, float firingPlatformLeadSeconds = 0f,
        bool desktopVisibleOnly = false) {
        var tasks = new List<ArtilleryTask>();
        if (fireMission == null || firingTurret?.turretBase == null) return tasks;
        // 行进间射击至少需要两个一致的真实炮位速度样本。宁可晚几秒接单，也不根据
        // 未确认的 MovementSpeed/坐标系猜测炮位并把弹道算到地图外。
        if (IsFiringPlatformMoving) {
            var platformMotion = SampleFiringPlatformMotion();
            if (platformMotion.StableSamples < 2) return tasks;
        }

        foreach (var location in GetAllFireMissionEntities()) {
            try {
                if (!location.gameObject.activeInHierarchy) continue;

                var entity = location.Entity;
                if (entity == null || !entity.IsAlive) continue;
                if (!string.IsNullOrWhiteSpace(entity.ID)) {
                    knownEntities[entity.ID] = entity;
                    knownLocations[entity.ID] = location;
                }
                // IsAlive/Destroyed 在部分任务脚本中会延迟更新；Health 是受击时直接修改的可靠兜底。
                if (entity.MaxHealth > 0 && entity.Health <= 0) continue;
                // EntityRoles is a flags enum.  During mission transitions an entity can briefly
                // retain Enemy while already receiving Ally; fail closed instead of treating that
                // mixed state as a valid automatic target.
                if ((entity.Role & EntityRoles.Enemy) == 0
                    || (entity.Role & EntityRoles.Ally) != 0) continue;
                if ((entity.State & MapEntityStates.Destroyed) != 0) continue;
                if (desktopVisibleOnly && !IsDesktopTargetVisible(location)) continue;

                var entityName = GetEntityName(entity);
                var mapPosition = ConvertEntityToMapPosition(location, entity);
                var task = CreateTask(mapPosition, firingPlatformLeadSeconds);
                if (float.IsNaN(task.distance) || float.IsInfinity(task.distance)
                                                   || task.distance <= 0f
                                                   || task.distance > AutoTargetMaxRangeKm) {
                    MelonLogger.Warning(
                        $"[FCS] AutoTarget: ignore invalid position for {entityName}: " +
                        $"{task.angel:F1} deg, {task.distance:F2} km");
                    continue;
                }
                task.sourceEntityId = string.IsNullOrWhiteSpace(entity.ID)
                    ? location.GetInstanceID().ToString()
                    : entity.ID;
                var motion = UpdateMotion(task.sourceEntityId, mapPosition);
                task.sourceVelocity = motion.Velocity;
                task.motionSamples = motion.StableSamples;
                task.targetName = string.IsNullOrWhiteSpace(entityName)
                    ? task.sourceEntityId
                    : entityName;
                task.sourceIcon = entity.Icon ?? "";
                task.sourceIconSprite = GetIconSpriteName(location, entity);
                task.sourceStatusSprites = GetStatusSpriteNames(location);
                task.sourceImmuneShells = GetImmuneShells(entity);
                task.sourceRewardPoints = GetTargetRewardPoints(location, out var rewardSource);
                task.sourceRewardSource = rewardSource;
                task.isHidden = (entity.State & MapEntityStates.Hidden) != 0;
                task.isMoving = (entity.State & MapEntityStates.Moving) != 0
                                || GetSpeedKmPerSecond(motion.Velocity)
                                > TargetMovingSpeedThresholdKmPerSecond;
                task.isShip = IsShip(location, entity, entityName);
                task.isUnderground = IsUnderground(location, entity, entityName, task.isShip);
                // Armour 在地下工事上负责生成绿色“地下/AP”标识，但舰船也会使用
                // Armour 表示舰体防护。二者都需要 AP，只有前者才应标记为地下。
                task.requiresAp = task.isUnderground || entity.Armour > 0
                                  || RequiresAp(entity, bulletType);
                task.isCommander = IsCommander(location, entity, entityName);
                task.isAntiAir = IsAntiAir(location, entity, entityName, task.isCommander);
                task.isArtillery = IsArtillery(
                    location, entity, entityName, task.isCommander, task.isAntiAir);
                task.isSupply = IsSupply(
                    location, entity, entityName,
                    task.isCommander, task.isArtillery, task.isAntiAir);
                task.isMechanized = IsMechanized(
                    location, entity, entityName,
                    task.isCommander, task.isArtillery, task.isAntiAir, task.isSupply);
                task.isRecon = IsRecon(
                    location, entity, entityName, task.isCommander, task.isArtillery,
                    task.isAntiAir, task.isSupply, task.isMechanized);
                task.isInfantry = IsInfantry(
                    location, entity, entityName, task.isCommander, task.isArtillery,
                    task.isAntiAir, task.isSupply, task.isMechanized, task.isRecon);
                // 所有移动敌军都允许按连续位置样本预测，包括被 PHGN 等效果驱散的单位。
                // 至少需要两个方向/速度一致的增量样本，不能用一次位置变化盲目外推。
                if (task.isMoving && task.motionSamples < 2) continue;
                task.bulletType = task.requiresAp ? BulletType.AP : bulletType;
                task.isAutoTarget = true;
                task.sourceHealth = entity.Health;
                task.sourceMaxHealth = entity.MaxHealth;
                task.sourceArmour = entity.Armour;
                task.sourceStars = entity.Stars;
                task.sourceRole = entity.Role;
                task.sourceState = entity.State;
                tasks.Add(task);
            }
            catch (Exception ex) {
                // FireMission 可能恰好在扫描期间移除被摧毁实体；跳过该帧即可。
                MelonLogger.Warning($"[FCS] AutoTarget: skip stale entity: {ex.Message}");
            }
        }

        // 类型优先级和炮兵/FDC 交替由 FSC 的有状态调度器处理；这里保持同类目标由近到远。
        tasks.Sort((a, b) => a.distance.CompareTo(b.distance));
        return tasks;
    }

    /// <summary>
    /// 刷新指定任务的内部逻辑坐标，并在目标仍移动时按当前稳定速度预测落点。
    /// 返回 false 表示实体已失效、运动不稳定或预测点超出火控范围。
    /// </summary>
    public bool TryRefreshTargetSolution(
        ArtilleryTask task,
        float targetLeadSeconds,
        float firingPlatformLeadSeconds,
        bool requireStableMotion,
        out string reason) {
        reason = "";
        if (IsFiringPlatformMoving && firingPlatformLeadSeconds > 0f) {
            var platformMotion = SampleFiringPlatformMotion();
            if (platformMotion.StableSamples < 2) {
                reason = $"firing platform motion is not stable " +
                         $"({platformMotion.StableSamples}/2 samples)";
                return false;
            }
        }
        if (!task.isAutoTarget || string.IsNullOrWhiteSpace(task.sourceEntityId)) {
            // 手动地图任务可能没有匹配到 FireMission 实体，但其任务坐标仍然有效；
            // 即使为防重复攻击关联到了实体 ID，也必须保留玩家所点图标尖角的固定坐标。
            // 移动炮位时只按实时/预测炮位重新计算相对方向和距离，不吸附到实体中心。
            try {
                var fixedSolution = CreateTask(task.position, firingPlatformLeadSeconds);
                if (float.IsNaN(fixedSolution.distance) || float.IsInfinity(fixedSolution.distance)
                                                        || fixedSolution.distance <= 0f
                                                        || fixedSolution.distance > AutoTargetMaxRangeKm) {
                    reason = $"fixed target is outside range ({fixedSolution.distance:F2} km)";
                    return false;
                }

                task.angel = fixedSolution.angel;
                task.distance = fixedSolution.distance;
                task.usesMovingPlatformSolution |= fixedSolution.usesMovingPlatformSolution;
                task.predictedPlatformLeadSeconds = fixedSolution.predictedPlatformLeadSeconds;
                task.predictedFiringOrigin = fixedSolution.predictedFiringOrigin;
                return true;
            }
            catch (Exception ex) {
                reason = ex.Message;
                return false;
            }
        }
        if (!TryGetKnownEntity(task.sourceEntityId, out var entity, out var location)) {
            reason = "entity is no longer available";
            return false;
        }

        try {
            if (!entity.IsAlive || entity.Health <= 0
                                || (entity.State & MapEntityStates.Destroyed) != 0) {
                reason = "entity is destroyed";
                return false;
            }
            if ((entity.Role & EntityRoles.Ally) != 0) {
                reason = "entity became allied";
                return false;
            }
            if ((entity.Role & EntityRoles.Enemy) == 0) {
                reason = "entity is no longer hostile";
                return false;
            }
            var current = ConvertEntityToMapPosition(location, entity);
            var motion = UpdateMotion(task.sourceEntityId, current);
            var speedKmPerSecond = GetSpeedKmPerSecond(motion.Velocity);
            var moving = (entity.State & MapEntityStates.Moving) != 0
                         || speedKmPerSecond > TargetMovingSpeedThresholdKmPerSecond;
            if (moving && speedKmPerSecond > MaxMovingTargetSpeedKmPerSecond) {
                reason = $"implausible motion speed ({speedKmPerSecond:F3} km/s)";
                return false;
            }

            var predicted = PredictTargetPosition(
                current, motion, moving, targetLeadSeconds, out var clampedLead);
            var stableMotion = !moving || motion.StableSamples >= 2;
            if (task.usesAreaAimPoint) {
                if (!TryRefreshAreaImpactPoint(
                        task,
                        predicted,
                        targetLeadSeconds,
                        ref moving,
                        ref stableMotion,
                        out predicted,
                        out reason)) {
                    task.isMoving = moving;
                    return false;
                }
                clampedLead = moving ? Mathf.Clamp(targetLeadSeconds, 0f, 120f) : 0f;
            }
            if (moving && requireStableMotion && !stableMotion) {
                reason = "group motion is not stable";
                task.isMoving = true;
                return false;
            }

            var solution = CreateTask(predicted, firingPlatformLeadSeconds);
            if (float.IsNaN(solution.distance) || float.IsInfinity(solution.distance)
                                               || solution.distance <= 0f
                                               || solution.distance > AutoTargetMaxRangeKm) {
                reason = $"predicted point is outside range ({solution.distance:F2} km)";
                return false;
            }

            task.angel = solution.angel;
            task.distance = solution.distance;
            task.position = solution.position;
            task.sourceVelocity = motion.Velocity;
            task.motionSamples = motion.StableSamples;
            task.predictedLeadSeconds = clampedLead;
            task.usesMovingPlatformSolution |= solution.usesMovingPlatformSolution;
            task.predictedPlatformLeadSeconds = solution.predictedPlatformLeadSeconds;
            task.predictedFiringOrigin = solution.predictedFiringOrigin;
            task.isMoving = moving;
            task.sourceHealth = entity.Health;
            task.sourceMaxHealth = entity.MaxHealth;
            task.sourceState = entity.State;
            return true;
        }
        catch (Exception ex) {
            reason = ex.Message;
            return false;
        }
    }

    private Vector3 PredictTargetPosition(
        Vector3 current,
        MotionTrack motion,
        bool moving,
        float requestedLeadSeconds,
        out float usedLeadSeconds) {
        usedLeadSeconds = moving ? Mathf.Clamp(requestedLeadSeconds, 0f, 120f) : 0f;
        var leadOffset = motion.Velocity * usedLeadSeconds;
        var maxLeadLocal = MaxMovingLeadDistanceKm / DistanceScale;
        if (leadOffset.magnitude > maxLeadLocal) {
            leadOffset = leadOffset.normalized * maxLeadLocal;
            usedLeadSeconds = motion.Velocity.magnitude > 0.000001f
                ? leadOffset.magnitude / motion.Velocity.magnitude
                : 0f;
        }
        var predicted = current + leadOffset;
        predicted.z = current.z;
        return predicted;
    }

    private bool TryRefreshAreaImpactPoint(
        ArtilleryTask task,
        Vector3 predictedPrimary,
        float requestedLeadSeconds,
        ref bool groupMoving,
        ref bool stableMotion,
        out Vector3 impactPoint,
        out string reason) {
        impactPoint = predictedPrimary;
        reason = "";
        var predictedTargets = new List<Vector3> { predictedPrimary };

        foreach (var entityId in task.areaCoveredTargetIds) {
            if (!TryGetKnownEntity(entityId, out var entity, out var location)) continue;
            try {
                if (!entity.IsAlive || entity.Health <= 0
                                    || (entity.State & MapEntityStates.Destroyed) != 0) continue;
                if ((entity.Role & EntityRoles.Enemy) == 0
                    || (entity.Role & EntityRoles.Ally) != 0) continue;
                var current = ConvertEntityToMapPosition(location, entity);
                var motion = UpdateMotion(entityId, current);
                var speed = GetSpeedKmPerSecond(motion.Velocity);
                var moving = (entity.State & MapEntityStates.Moving) != 0
                             || speed > TargetMovingSpeedThresholdKmPerSecond;
                if (moving && speed > MaxMovingTargetSpeedKmPerSecond) {
                    stableMotion = false;
                    continue;
                }
                groupMoving |= moving;
                stableMotion &= !moving || motion.StableSamples >= 2;
                predictedTargets.Add(PredictTargetPosition(
                    current, motion, moving, requestedLeadSeconds, out _));
            }
            catch {
                // 群组实体可能刚好被移除；本轮不把它计入覆盖即可。
            }
        }

        var radiusMission = task.impactRadiusKm / DistanceScale;
        if (radiusMission <= 0.0001f || predictedTargets.Count == 1) {
            task.areaTargetCount = 1;
            task.areaAimOffsetFromPrimary = Vector3.zero;
            return true;
        }

        var bestCount = 0;
        var bestClearance = float.NegativeInfinity;
        var bestPoint = predictedPrimary;
        // 本轮所有候选落点共用同一份友军快照，避免每个点都重新遍历全图实体。
        var allyPositions = GetLiveAllyPositions();
        foreach (var candidate in BuildLiveAreaAimCandidates(
                     predictedTargets,
                     predictedPrimary + task.areaAimOffsetFromPrimary,
                     radiusMission)) {
            if (fireMission?.coordinateRoot == null
                || !fireMission.coordinateRoot.rect.Contains(
                    new Vector2(candidate.x, candidate.y))) continue;
            // 主目标必须始终在本发杀伤范围内，不能为了附带目标而丢掉主目标。
            if (Vector3.Distance(candidate, predictedPrimary) > radiusMission + 0.001f) continue;
            if (HasPositionWithin(allyPositions, candidate, radiusMission)) continue;

            var count = 0;
            var maxDistance = 0f;
            foreach (var target in predictedTargets) {
                var distance = Vector3.Distance(candidate, target);
                if (distance > radiusMission + 0.001f) continue;
                count++;
                maxDistance = Mathf.Max(maxDistance, distance);
            }
            var clearance = radiusMission - maxDistance;
            if (count < bestCount || count == bestCount && clearance <= bestClearance) continue;
            bestCount = count;
            bestClearance = clearance;
            bestPoint = candidate;
        }

        if (bestCount == 0) {
            reason = "no safe live area impact point";
            return false;
        }
        if (bestCount != task.areaTargetCount) {
            MelonLogger.Msg(
                $"[FCS] Moving area plan: {task.targetName} live coverage " +
                $"{task.areaTargetCount}->{bestCount}");
        }
        task.areaTargetCount = bestCount;
        task.areaAimOffsetFromPrimary = bestPoint - predictedPrimary;
        impactPoint = bestPoint;
        return true;
    }

    private static IEnumerable<Vector3> BuildLiveAreaAimCandidates(
        List<Vector3> targets,
        Vector3 translatedPreviousAim,
        float radius) {
        yield return translatedPreviousAim;
        foreach (var target in targets) yield return target;

        var centroid = Vector3.zero;
        foreach (var target in targets) centroid += target;
        yield return centroid / targets.Count;

        for (var i = 0; i < targets.Count; ++i) {
            for (var j = i + 1; j < targets.Count; ++j) {
                var delta = targets[j] - targets[i];
                delta.z = 0f;
                var distance = delta.magnitude;
                if (distance > radius * 2f + 0.001f) continue;
                var midpoint = (targets[i] + targets[j]) * 0.5f;
                yield return midpoint;
                if (distance <= 0.0001f) continue;
                var half = distance * 0.5f;
                var heightSquared = radius * radius - half * half;
                if (heightSquared <= 0f) continue;
                var perpendicular = new Vector3(-delta.y, delta.x, 0f) / distance;
                var height = Mathf.Sqrt(heightSquared);
                yield return midpoint + perpendicular * height;
                yield return midpoint - perpendicular * height;
            }
        }
    }

    private bool TryGetKnownEntity(
        string entityId, out MapEntity entity, out EntityLocation location) {
        // EntityLocation objects can be reused by mission scripts.  Always resolve the current
        // entity from the cached location so a stale MapEntity cannot keep an old target alive.
        if (knownLocations.TryGetValue(entityId, out location!)) {
            try {
                var current = location.Entity;
                if (current != null
                    && string.Equals(current.ID, entityId, StringComparison.Ordinal)) {
                    entity = current;
                    knownEntities[entityId] = current;
                    return true;
                }
            }
            catch {
                // Fall through to a fresh FireMission scan.
            }
            knownEntities.Remove(entityId);
            knownLocations.Remove(entityId);
        }

        foreach (var candidate in GetAllFireMissionEntities()) {
            try {
                var found = candidate.Entity;
                if (found == null || !string.Equals(found.ID, entityId, StringComparison.Ordinal)) continue;
                entity = found;
                location = candidate;
                knownEntities[entityId] = found;
                knownLocations[entityId] = candidate;
                return true;
            }
            catch {
                // 实体列表可能正处于更新中。
            }
        }

        entity = null!;
        location = null!;
        return false;
    }

    private MotionTrack UpdateMotion(string entityId, Vector3 current) {
        var now = Time.realtimeSinceStartup;
        if (!motionTracks.TryGetValue(entityId, out var track)) {
            track = new MotionTrack { Position = current, SampleTime = now };
            motionTracks[entityId] = track;
            return track;
        }

        var elapsed = now - track.SampleTime;
        if (elapsed < 0.15f) return track;
        var measured = (current - track.Position) / elapsed;
        measured.z = 0f;
        var speed = measured.magnitude;
        if (speed < 0.0005f) {
            track.Velocity = Vector3.zero;
            track.StableSamples = 0;
            track.StationarySamples++;
        }
        else if (track.Velocity.sqrMagnitude < 0.000001f) {
            track.Velocity = measured;
            track.StableSamples = 1;
            track.StationarySamples = 0;
            track.HasObservedMotion = true;
        }
        else {
            track.StationarySamples = 0;
            track.HasObservedMotion = true;
            var directionAgreement = Vector3.Dot(track.Velocity.normalized, measured.normalized);
            var speedRatio = speed / Mathf.Max(0.0001f, track.Velocity.magnitude);
            if (directionAgreement >= 0.85f && speedRatio >= 0.5f && speedRatio <= 2f) {
                track.Velocity = Vector3.Lerp(track.Velocity, measured, 0.5f);
                track.StableSamples++;
            }
            else {
                track.Velocity = measured;
                track.StableSamples = 1;
            }
        }
        track.Position = current;
        track.SampleTime = now;
        return track;
    }

    /// <summary>
    /// 按实体 ID 读取当前状态。隐藏目标虽然不绘制 VisualRoot，EntityLocation 和 MapEntity
    /// 仍由 FireMission 保留，因此可以用生命值/Destroyed 状态确认真实命中。
    /// </summary>
    public bool TryGetEntityStatus(
        string entityId, out bool isAlive, out int health,
        out int maxHealth, out MapEntityStates state) {
        isAlive = false;
        health = 0;
        maxHealth = 0;
        state = MapEntityStates.None;
        if (string.IsNullOrWhiteSpace(entityId)) return false;

        if (knownEntities.TryGetValue(entityId, out var known)) {
            try {
                isAlive = known.IsAlive;
                health = known.Health;
                maxHealth = known.MaxHealth;
                state = known.State;
                return true;
            }
            catch {
                knownEntities.Remove(entityId);
            }
        }

        foreach (var location in GetAllFireMissionEntities()) {
            try {
                var entity = location.Entity;
                if (entity == null || !string.Equals(entity.ID, entityId, StringComparison.Ordinal)) continue;
                isAlive = entity.IsAlive;
                health = entity.Health;
                maxHealth = entity.MaxHealth;
                state = entity.State;
                knownEntities[entityId] = entity;
                return true;
            }
            catch {
                // FireMission 可能正在销毁实体；下一次轮询再读取。
            }
        }
        return false;
    }

    /// <summary>
    /// 读取目标上由游戏配置的固定征用积分。返回 -1 表示没有公开固定奖励，
    /// 此类目标仍可能由任务图在摧毁后追加积分。
    /// </summary>
    private static int GetTargetRewardPoints(EntityLocation location, out string source) {
        source = "";
        try {
            var rewards = location.GetComponentsInChildren<TargetRewardRequisitionPoints>(true);
            foreach (var reward in rewards) {
                if (reward == null) continue;
                source = string.IsNullOrWhiteSpace(reward.defaultSourceLabel)
                    ? "TargetReward"
                    : reward.defaultSourceLabel;
                return reward.points;
            }

            // 部分关卡把奖励组件放在别处，再通过 EntityLocation.OnDestroyed 的
            // 持久监听器调用它；沿事件引用查找能覆盖这种配置方式。
            var destroyedEvent = location.OnDestroyed;
            if (destroyedEvent != null) {
                for (var i = 0; i < destroyedEvent.GetPersistentEventCount(); i++) {
                    var target = destroyedEvent.GetPersistentTarget(i);
                    var reward = target?.TryCast<TargetRewardRequisitionPoints>();
                    if (reward == null) continue;

                    source = string.IsNullOrWhiteSpace(reward.defaultSourceLabel)
                        ? destroyedEvent.GetPersistentMethodName(i)
                        : reward.defaultSourceLabel;
                    return reward.points;
                }
            }

            var parent = location.transform.parent;
            for (var depth = 1; parent != null && depth <= 2; depth++, parent = parent.parent) {
                var reward = parent.GetComponent<TargetRewardRequisitionPoints>();
                if (reward == null) continue;
                source = string.IsNullOrWhiteSpace(reward.defaultSourceLabel)
                    ? $"Parent{depth}"
                    : reward.defaultSourceLabel;
                return reward.points;
            }
        }
        catch {
            // 目标 UI 在销毁过程中可能暂时失效；奖励未知即可，不影响索敌。
        }
        return -1;
    }

    /// <summary>
    /// Demo 的 MapEntity.Name 是 string；正式版改成了 Localisation.TextIdentifier。
    /// 通过运行时属性读取可避免把逻辑 DLL 固定绑定到任一版本的 get_Name 签名。
    /// </summary>
    private static string GetEntityName(MapEntity entity) {
        try {
            var value = entity.GetType().GetProperty("Name")?.GetValue(entity);
            if (value is string directName) return directName;
            if (value == null) return "";

            var valueType = value.GetType();
            var getMethod = valueType.GetMethod("Get", Type.EmptyTypes);
            if (getMethod?.Invoke(value, null) is string localizedName
                && !string.IsNullOrWhiteSpace(localizedName)) {
                return localizedName;
            }

            foreach (var propertyName in new[] { "Raw", "Key" }) {
                if (valueType.GetProperty(propertyName)?.GetValue(value) is string fallbackName
                    && !string.IsNullOrWhiteSpace(fallbackName)) {
                    return fallbackName;
                }
            }
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS] AutoTarget: unable to read target name: {ex.Message}");
        }
        return "";
    }

    /// <summary>
    /// 优先使用游戏的 Artillery 角色位；部分关卡只提供名称或图标，因此增加常见炮兵标记兜底。
    /// FDC 单独归入指挥官类别，避免同一个目标同时占用两个交替类别。
    /// </summary>
    private static bool IsArtillery(
        EntityLocation location, MapEntity entity, string entityName,
        bool isCommander, bool isAntiAir) {
        if (isCommander || isAntiAir) return false;
        if ((entity.Role & EntityRoles.Artillery) != 0) return true;

        return ContainsArtilleryMarker(entity.ID)
               || ContainsArtilleryMarker(entityName)
               || ContainsArtilleryMarker(entity.Icon)
               || ContainsArtilleryMarker(GetIconSpriteName(location, entity));
    }

    /// <summary>
    /// 防空炮在游戏中通常带 AABattery 角色、aa/enemyaa ID 或 Enemy AA 图标。
    /// 单独分类后可把它放在指挥官之后，而不会因 Artillery 角色误进最高炮兵层级。
    /// </summary>
    private static bool IsAntiAir(
        EntityLocation location, MapEntity entity, string entityName, bool isCommander) {
        if (isCommander) return false;
        return ContainsAntiAirMarker(entity.Role.ToString())
               || ContainsAntiAirMarker(entity.ID)
               || ContainsAntiAirMarker(entityName)
               || ContainsAntiAirMarker(entity.Icon)
               || ContainsAntiAirMarker(GetIconSpriteName(location, entity));
    }

    private static bool ContainsAntiAirMarker(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.IndexOf("AABattery", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Anti-Air", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Anti Air", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("AntiAircraft", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Anti Aircraft", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Enemy AA", StringComparison.OrdinalIgnoreCase) >= 0
               || value.StartsWith("aa#", StringComparison.OrdinalIgnoreCase)
               || value.IndexOf("enemyaa", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Flak", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("防空炮", StringComparison.Ordinal) >= 0
               || value.IndexOf("高射炮", StringComparison.Ordinal) >= 0;
    }

    private static bool ContainsArtilleryMarker(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return false;
        // “Field Artillery Observer”等侦察目标名称也含 Artillery，不能因此抬到炮兵优先级。
        if (ContainsReconMarker(value)) return false;
        return value.IndexOf("Artillery", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Howitzer", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Mortar", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Field Gun", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("AntiTank", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Anti Tank", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("enemyfield", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("炮兵", StringComparison.Ordinal) >= 0
               || value.IndexOf("火炮", StringComparison.Ordinal) >= 0
               || value.IndexOf("野战炮", StringComparison.Ordinal) >= 0
               || value.IndexOf("反坦克炮", StringComparison.Ordinal) >= 0;
    }

    private static bool IsSupply(EntityLocation location, MapEntity entity, string entityName,
        bool isCommander, bool isArtillery, bool isAntiAir) {
        if (isCommander || isArtillery || isAntiAir) return false;
        return ContainsSupplyMarker(entity.ID)
               || ContainsSupplyMarker(entityName)
               || ContainsSupplyMarker(entity.Icon)
               || ContainsSupplyMarker(GetIconSpriteName(location, entity));
    }

    private static bool ContainsSupplyMarker(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.IndexOf("SupplyCache", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Supply Cache", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("AmmoCache", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Ammo Cache", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Ammunition Cache", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Logistics", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("补给", StringComparison.Ordinal) >= 0
               || value.IndexOf("弹药库", StringComparison.Ordinal) >= 0;
    }

    private static bool IsMechanized(EntityLocation location, MapEntity entity, string entityName,
        bool isCommander, bool isArtillery, bool isAntiAir, bool isSupply) {
        if (isCommander || isArtillery || isAntiAir || isSupply) return false;
        if ((entity.Role & EntityRoles.Tank) != 0) return true;

        return ContainsMechanizedMarker(entity.ID)
               || ContainsMechanizedMarker(entityName)
               || ContainsMechanizedMarker(entity.Icon)
               || ContainsMechanizedMarker(GetIconSpriteName(location, entity));
    }

    private static bool ContainsMechanizedMarker(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.IndexOf("Mechanized", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Mechanised", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Tank", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("APC", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("IFV", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Armored", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Armoured", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("机械化", StringComparison.Ordinal) >= 0
               || value.IndexOf("坦克", StringComparison.Ordinal) >= 0
               || value.IndexOf("装甲", StringComparison.Ordinal) >= 0;
    }

    private static bool IsRecon(EntityLocation location, MapEntity entity, string entityName,
        bool isCommander, bool isArtillery,
        bool isAntiAir, bool isSupply, bool isMechanized) {
        if (isCommander || isArtillery || isAntiAir || isSupply || isMechanized) return false;
        if ((entity.Role & EntityRoles.ListeningPost) != 0) return true;

        return ContainsReconMarker(entity.ID)
               || ContainsReconMarker(entityName)
               || ContainsReconMarker(entity.Icon)
               || ContainsReconMarker(GetIconSpriteName(location, entity));
    }

    private static bool ContainsReconMarker(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.IndexOf("Recon", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Scout", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Observer", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Listening Post", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("ListeningPost", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Spotter", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("侦察", StringComparison.Ordinal) >= 0
               || value.IndexOf("观察哨", StringComparison.Ordinal) >= 0
               || value.IndexOf("监听哨", StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// 优先读取游戏公开的 Infantry 角色位，并用名称/图标兜底。
    /// 已识别为炮兵或指挥官的目标不会再归入步兵类别。
    /// </summary>
    private static bool IsInfantry(EntityLocation location, MapEntity entity, string entityName,
        bool isCommander, bool isArtillery,
        bool isAntiAir, bool isSupply, bool isMechanized, bool isRecon) {
        if (isCommander || isArtillery || isAntiAir
            || isSupply || isMechanized || isRecon) return false;
        if ((entity.Role & EntityRoles.Infantry) != 0) return true;

        return ContainsInfantryMarker(entity.ID)
               || ContainsInfantryMarker(entityName)
               || ContainsInfantryMarker(entity.Icon)
               || ContainsInfantryMarker(GetIconSpriteName(location, entity));
    }

    private static bool ContainsInfantryMarker(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.IndexOf("Infantry", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Rifleman", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Soldier", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Trooper", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("步兵", StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// 正式版海战任务会使用 Enemy Ship/Ship Stripe 等图标；纸面航迹目标的
    /// ID 也可能直接使用 ship/vessel/cruiser。单独归类后可允许其在航行中接受提前量计算。
    /// </summary>
    private static bool IsShip(EntityLocation location, MapEntity entity, string entityName) {
        return ContainsShipMarker(entity.ID)
               || ContainsShipMarker(entityName)
               || ContainsShipMarker(entity.Icon)
               || ContainsShipMarker(GetIconSpriteName(location, entity));
    }

    private static bool ContainsShipMarker(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.IndexOf("Ship", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Vessel", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("Cruiser", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("舰", StringComparison.Ordinal) >= 0
               || value.IndexOf("船", StringComparison.Ordinal) >= 0
               || value.IndexOf("邮轮", StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// 炮兵指挥官在地图上使用 FDC（Fire Direction Center）标记。
    /// 同时检查任务数据中的名称/图标和运行时精灵名，以兼容不同关卡的数据写法。
    /// </summary>
    private static bool IsCommander(EntityLocation location, MapEntity entity, string entityName) {
        if (ContainsCommanderMarker(entityName) || ContainsCommanderMarker(entity.Icon)) return true;

        try {
            var sprite = location.Image_Icon?.sprite;
            return sprite != null && ContainsCommanderMarker(sprite.name);
        }
        catch {
            return false;
        }
    }

    private static bool ContainsCommanderMarker(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (value.IndexOf("Fire Direction Center", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("Fire_Direction_Center", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("FireDirectionCenter", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("Commander", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("炮兵指挥官", StringComparison.Ordinal) >= 0
            || value.IndexOf("射击指挥官", StringComparison.Ordinal) >= 0
            || value.IndexOf("射擊指揮官", StringComparison.Ordinal) >= 0) {
            return true;
        }

        // FDC 必须是独立缩写，避免误判名称中偶然出现的三个字母。
        for (var i = 0; i <= value.Length - 3; i++) {
            if (!value.AsSpan(i, 3).Equals("FDC".AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;

            var leftBoundary = i == 0 || !char.IsLetterOrDigit(value[i - 1]);
            var rightIndex = i + 3;
            var rightBoundary = rightIndex == value.Length || !char.IsLetterOrDigit(value[rightIndex]);
            if (leftBoundary && rightBoundary) return true;
        }

        return false;
    }

    /// <summary>
    /// 地下目标可能使用 Enemy_Underground Fort 状态图标，也可能只在主图标中使用 Bunker 名称。
    /// 名称、数据图标、原始精灵、运行时精灵和对象名都检查，兼容 FDC 与补给掩体等不同关卡写法。
    /// </summary>
    private static bool IsUnderground(
        EntityLocation location, MapEntity entity, string entityName, bool isShip) {
        // 绿色“地下 / 需要 AP”标识由 Armour 显示层生成，主 FDC 图标和 Role 都可能仍是普通值。
        // 舰船的 Armour 是舰体防护，不是地下标识。
        if (!isShip && entity.Armour > 0) return true;

        if (ContainsUnderground(entity.ID)
            || ContainsUnderground(entityName)
            || ContainsUnderground(entity.Icon)
            || ContainsUnderground(GetIconSpriteName(location, entity))
            || ContainsUnderground(location.gameObject.name)) {
            return true;
        }

        if (HasUndergroundStatusIcon(location)) return true;

        try {
            var sprite = location.Image_Icon?.sprite;
            return sprite != null && ContainsUnderground(sprite.name);
        }
        catch {
            // 某些地图实体在销毁过程中会先释放 UI 引用；此时按普通目标处理。
            return false;
        }
    }

    /// <summary>检查目标下面实际可见的状态图标，包括主图标之外单独叠加的绿色地下标识。</summary>
    private static bool HasUndergroundStatusIcon(EntityLocation location) {
        try {
            var images = location.GetComponentsInChildren<UnityEngine.UI.Image>(true);
            foreach (var image in images) {
                if (image == null || !image.enabled || !image.gameObject.activeInHierarchy) continue;
                if (ContainsUnderground(image.gameObject.name)
                    || ContainsUnderground(image.sprite?.name)) {
                    return true;
                }
            }
        }
        catch {
            // UI 正在销毁或重建时由其他数据字段继续判定。
        }
        return false;
    }

    private static bool ContainsUnderground(string? value) {
        return !string.IsNullOrEmpty(value)
               && (value.IndexOf("Underground", StringComparison.OrdinalIgnoreCase) >= 0
                   || value.IndexOf("Bunker", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// 游戏命中判定会读取 MapEntity.ImmuneShells。即使关卡没有在主图标名里写 Underground，
    /// 只要当前弹种在免疫列表里而 AP 不在，就按游戏自身规则切换为 AP。
    /// </summary>
    private static bool RequiresAp(MapEntity entity, BulletType selectedBulletType) {
        if (selectedBulletType == BulletType.AP) return false;
        return IsImmuneTo(entity, selectedBulletType) && !IsImmuneTo(entity, BulletType.AP);
    }

    private static bool IsImmuneTo(MapEntity entity, BulletType bulletType) {
        var immuneShells = entity.ImmuneShells;
        if (immuneShells == null) return false;

        var shellId = bulletType.ToString();
        foreach (var immuneShell in immuneShells) {
            if (string.Equals(immuneShell, shellId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string GetImmuneShells(MapEntity entity) {
        var immuneShells = entity.ImmuneShells;
        if (immuneShells == null) return "";

        var values = new List<string>();
        foreach (var immuneShell in immuneShells) {
            if (!string.IsNullOrWhiteSpace(immuneShell)) values.Add(immuneShell);
        }
        return string.Join(",", values);
    }

    private static string GetIconSpriteName(EntityLocation location, MapEntity entity) {
        try {
            if (entity.IconRaw != null && !string.IsNullOrWhiteSpace(entity.IconRaw.name)) {
                return entity.IconRaw.name;
            }
            return location.Image_Icon?.sprite?.name ?? "";
        }
        catch {
            return "";
        }
    }

    private static string GetStatusSpriteNames(EntityLocation location) {
        try {
            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var images = location.GetComponentsInChildren<UnityEngine.UI.Image>(true);
            foreach (var image in images) {
                if (image == null || !image.enabled || !image.gameObject.activeInHierarchy) continue;
                var spriteName = image.sprite?.name;
                if (!string.IsNullOrWhiteSpace(spriteName)) values.Add(spriteName);
            }
            return string.Join(",", values);
        }
        catch {
            return "";
        }
    }

    /// <summary>
    /// EntityLocation.LocalPosition 与 TurretController.turretBase 都由 FireMission 放在同一任务
    /// 坐标系中。直接使用该坐标可避开桌上 Player Turret Piece 在阵地移动后仍停留于旧位置的问题。
    /// </summary>
    private Vector3 ConvertEntityToMapPosition(EntityLocation location, MapEntity entity) {
        var local = location.LocalPosition;
        return new Vector3(local.x, local.y, 0f);
    }

    public bool IsFiringPlatformMoving {
        get {
            try {
                if (firingTurret == null) return false;
                if (firingTurret.IsMoving || firingTurret.CR_Movement != null) return true;
                var motion = SampleFiringPlatformMotion();
                var measuredMoving = GetSpeedKmPerSecond(motion.Velocity)
                                     > FiringPlatformMovingSpeedThresholdKmPerSecond;
                // 游戏 IsMoving 会比实际炮位更早复位；真实位置至少连续三次静止采样后
                // 才结束移动状态，避免余速阶段被当作停车射击。
                return measuredMoving
                       || (motion.HasObservedMotion && motion.StationarySamples < 3);
            }
            catch {
                return firingTurret?.CR_Movement != null;
            }
        }
    }

    public float FiringPlatformSpeedKmPerSecond {
        get {
            try {
                return GetSpeedKmPerSecond(SampleFiringPlatformMotion().Velocity);
            }
            catch {
                return 0f;
            }
        }
    }

    private ArtilleryTask CreateTask(
        Vector3 targetMissionPosition, float firingPlatformLeadSeconds = 0f) {
        if (fireMission == null || firingTurret?.turretBase == null) {
            throw new InvalidOperationException("FireMission or firing turret is not bound.");
        }

        var origin2 = fireMission.ToLocalSpace(firingTurret.turretBase.position);
        var currentOrigin = new Vector3(origin2.x, origin2.y, 0f);
        var origin = PredictFiringOriginMission(
            currentOrigin, firingPlatformLeadSeconds, out var usedMovingPrediction);
        var target = targetMissionPosition - origin;
        target.z = 0f;
        var dist = target.magnitude * DistanceScale;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        return new ArtilleryTask {
            angel = angle,
            distance = dist,
            position = targetMissionPosition,
            usesMovingPlatformSolution = usedMovingPrediction,
            predictedPlatformLeadSeconds = usedMovingPrediction
                ? Mathf.Clamp(firingPlatformLeadSeconds, 0f, 120f)
                : 0f,
            predictedFiringOrigin = origin
        };
    }

    /// <summary>
    /// 在当前装药射程内搜索地图边界以内、且离所有任务实体最远的空放落点。
    /// 空放只用于释放无法重新分配的已装填炮弹，不会伪装成敌军命中任务。
    /// </summary>
    public bool TryCreateSafeDischargeTask(
        float minRangeKm, float maxRangeKm, BulletType bulletType,
        out ArtilleryTask task, out string reason,
        Func<float, bool>? rangeValidator = null,
        float minimumClearanceMission = 0f) {
        task = new ArtilleryTask();
        reason = "";
        if (fireMission?.coordinateRoot == null || firingTurret?.turretBase == null) {
            reason = "fire mission is not bound";
            return false;
        }
        if (!float.IsFinite(minRangeKm) || !float.IsFinite(maxRangeKm)
                                             || maxRangeKm <= minRangeKm) {
            reason = $"invalid discharge range {minRangeKm:F2}-{maxRangeKm:F2} km";
            return false;
        }

        try {
            var origin2 = fireMission.ToLocalSpace(firingTurret.turretBase.position);
            var origin = new Vector3(origin2.x, origin2.y, 0f);
            var rect = fireMission.coordinateRoot.rect;
            var entityPositions = new List<Vector3>();
            foreach (var location in GetAllFireMissionEntities()) {
                try {
                    if (!location.gameObject.activeInHierarchy) continue;
                    var local = location.LocalPosition;
                    entityPositions.Add(new Vector3(local.x, local.y, 0f));
                }
                catch {
                    // 场景可能正在移除实体；忽略该对象即可。
                }
            }

            var low = Mathf.Max(0.1f, minRangeKm + 0.05f);
            var high = Mathf.Min(AutoTargetMaxRangeKm, maxRangeKm - 0.05f);
            if (high <= low) {
                reason = $"loaded range has no safe interior ({minRangeKm:F2}-{maxRangeKm:F2} km)";
                return false;
            }
            // GetRangeForCharge 给出的是原生声明射程，但个别弹种/装药的正向弹道曲线
            // 并不能覆盖整个区间。空放点必须先经过当前炮管的真实弹道反解验证，
            // 防止选中一个“在声明射程内、实际却无法到达”的点后反复重选。
            var ranges = Enumerable.Range(1, 9)
                .Select(index => Mathf.Lerp(low, high, index / 10f))
                .Where(range => rangeValidator == null || rangeValidator(range))
                .ToArray();
            if (ranges.Length == 0) {
                reason = "no ballistically reachable safe-discharge range";
                return false;
            }
            var bestClearanceKm = float.NegativeInfinity;
            var minimumClearanceKm = Mathf.Max(0f, minimumClearanceMission) * DistanceScale;
            Vector3 bestPoint = default;
            var found = false;
            foreach (var rangeKm in ranges) {
                var localDistance = rangeKm / DistanceScale;
                for (var angle = 0; angle < 360; angle += 5) {
                    var radians = angle * Mathf.Deg2Rad;
                    var point = origin + new Vector3(
                        Mathf.Sin(radians) * localDistance,
                        Mathf.Cos(radians) * localDistance,
                        0f);
                    if (!rect.Contains(new Vector2(point.x, point.y))) continue;

                    var clearanceKm = Mathf.Min(
                        Mathf.Min(point.x - rect.xMin, rect.xMax - point.x),
                        Mathf.Min(point.y - rect.yMin, rect.yMax - point.y)) * DistanceScale;
                    foreach (var entityPosition in entityPositions) {
                        clearanceKm = Mathf.Min(
                            clearanceKm,
                            Vector3.Distance(point, entityPosition) * DistanceScale);
                    }
                    if (clearanceKm + 0.001f < minimumClearanceKm) continue;
                    if (clearanceKm <= bestClearanceKm) continue;
                    bestClearanceKm = clearanceKm;
                    bestPoint = point;
                    found = true;
                }
            }

            if (!found) {
                reason = minimumClearanceKm > 0f
                    ? $"no in-map discharge point has {minimumClearanceKm:F2} km clearance"
                    : "no in-map point is reachable by the loaded round";
                return false;
            }

            task = CreateTask(bestPoint);
            task.targetName = "安全空放区";
            task.bulletType = bulletType;
            task.isAutoTarget = true;
            task.isSafeDischarge = true;
            MelonLogger.Warning(
                $"[FCS] Safe discharge point selected: {task.angel:F1}deg/{task.distance:F2}km, " +
                $"clearance={bestClearanceKm:F2}km");
            return true;
        }
        catch (Exception ex) {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 只使用 turretBase 的连续真实位置样本推算未来炮位。MovementTargetLoc 是游戏提供的
    /// 可空二维任务坐标，仅用于防止预测越过终点；不把它与世界三维坐标直接混算。
    /// </summary>
    private Vector3 PredictFiringOriginMission(
        Vector3 currentOrigin, float leadSeconds, out bool usedPrediction) {
        usedPrediction = false;
        if (!IsFiringPlatformMoving || firingTurret == null || leadSeconds <= 0f) {
            return currentOrigin;
        }

        try {
            var motion = SampleFiringPlatformMotion();
            if (motion.StableSamples < 1 || motion.Velocity.sqrMagnitude < 0.000001f) {
                return currentOrigin;
            }

            var leadOffset = motion.Velocity * Mathf.Clamp(leadSeconds, 0f, 120f);
            var maxLeadLocal = MaxMovingPlatformLeadDistanceKm / DistanceScale;
            if (leadOffset.magnitude > maxLeadLocal) {
                leadOffset = leadOffset.normalized * maxLeadLocal;
            }

            var predicted = currentOrigin + leadOffset;
            var movementTarget = firingTurret.MovementTargetLoc;
            if (movementTarget.HasValue) {
                var target2 = movementTarget.Value;
                var endpoint = new Vector3(target2.x, target2.y, 0f);
                var remaining = endpoint - currentOrigin;
                // 只有终点看起来确实位于当前运动方向前方时才用于封顶；坐标异常则忽略。
                if (IsFinite(endpoint) && remaining.sqrMagnitude > 0.000001f
                                       && Vector3.Dot(leadOffset, remaining) > 0f
                                       && leadOffset.magnitude > remaining.magnitude) {
                    predicted = endpoint;
                }
            }

            usedPrediction = (predicted - currentOrigin).sqrMagnitude > 0.000001f;
            return predicted;
        }
        catch {
            return currentOrigin;
        }
    }

    private MotionTrack SampleFiringPlatformMotion() {
        if (fireMission == null || firingTurret?.turretBase == null) {
            return new MotionTrack();
        }

        var local2 = fireMission.ToLocalSpace(firingTurret.turretBase.position);
        return UpdateMotion(
            FiringPlatformMotionId,
            new Vector3(local2.x, local2.y, 0f));
    }

    private static bool IsFinite(Vector3 value) {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
               && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
               && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    private float DistanceScale {
        get {
            var scale = fireMission?.distanceToKmScale ?? 0f;
            return scale > 0.0001f ? scale : MapDistanceScale;
        }
    }

    public float MissionDistanceToKm(float missionDistance) {
        return missionDistance * DistanceScale;
    }

    public bool TrySetAreaImpactPoint(
        ArtilleryTask task, Vector3 impactPoint, out string reason) {
        reason = "";
        try {
            if (fireMission?.coordinateRoot == null
                || !fireMission.coordinateRoot.rect.Contains(
                    new Vector2(impactPoint.x, impactPoint.y))) {
                reason = "optimized impact point is outside the map";
                return false;
            }

            var solution = CreateTask(impactPoint);
            if (!float.IsFinite(solution.distance) || solution.distance <= 0f
                                                   || solution.distance > AutoTargetMaxRangeKm) {
                reason = $"optimized impact point is outside range ({solution.distance:F2} km)";
                return false;
            }
            task.areaAimOffsetFromPrimary = impactPoint - task.position;
            task.position = impactPoint;
            task.angel = solution.angel;
            task.distance = solution.distance;
            task.usesAreaAimPoint = true;
            task.usesMovingPlatformSolution = solution.usesMovingPlatformSolution;
            task.predictedPlatformLeadSeconds = solution.predictedPlatformLeadSeconds;
            task.predictedFiringOrigin = solution.predictedFiringOrigin;
            return true;
        }
        catch (Exception ex) {
            reason = ex.Message;
            return false;
        }
    }

    public bool HasLiveAllyWithin(Vector3 center, float radiusMission) {
        return HasPositionWithin(GetLiveAllyPositions(), center, radiusMission);
    }

    /// <summary>
    /// Final fail-closed guard for automatic fire.  Manual map tasks deliberately bypass it.
    /// The caller supplies the live shell blast radius plus its desired safety margin.
    /// </summary>
    public bool TryValidateAutomaticImpact(
        ArtilleryTask task, float safetyRadiusMission, out string reason) {
        reason = "";
        if (!task.isAutoTarget) return true;

        if (!task.isSafeDischarge) {
            if (string.IsNullOrWhiteSpace(task.sourceEntityId)
                || !TryGetKnownEntity(task.sourceEntityId, out var entity, out _)) {
                reason = "automatic target entity is no longer available";
                return false;
            }
            if (!entity.IsAlive || entity.Health <= 0
                                || (entity.State & MapEntityStates.Destroyed) != 0) {
                reason = "automatic target entity is destroyed";
                return false;
            }
            if ((entity.Role & EntityRoles.Ally) != 0) {
                reason = "automatic target entity is allied";
                return false;
            }
            if ((entity.Role & EntityRoles.Enemy) == 0) {
                reason = "automatic target entity is no longer hostile";
                return false;
            }
        }

        var radius = Mathf.Max(0f, safetyRadiusMission);
        if (radius > 0f && HasLiveAllyWithin(task.position, radius)) {
            reason = $"live ally is within the {radius:F3} mission-unit safety radius";
            return false;
        }
        return true;
    }

    public List<Vector3> GetLiveAllyPositions() {
        var positions = new List<Vector3>();
        foreach (var location in GetAllFireMissionEntities()) {
            try {
                if (!location.gameObject.activeInHierarchy) continue;
                var entity = location.Entity;
                if (entity == null || !entity.IsAlive) continue;
                if (entity.MaxHealth > 0 && entity.Health <= 0) continue;
                if ((entity.State & MapEntityStates.Destroyed) != 0) continue;
                if ((entity.Role & EntityRoles.Ally) == 0) continue;
                var ally = location.LocalPosition;
                positions.Add(new Vector3(ally.x, ally.y, 0f));
            }
            catch {
                // 场景实体正在增删时由下一轮扫描重试。
            }
        }
        return positions;
    }

    public static bool HasPositionWithin(
        IReadOnlyList<Vector3> positions, Vector3 center, float radiusMission) {
        if (radiusMission <= 0f) return false;
        var radiusSquared = radiusMission * radiusMission;
        foreach (var position in positions) {
            if ((center - position).sqrMagnitude <= radiusSquared) return true;
        }
        return false;
    }

    public static bool IsShellEffectiveAgainst(ArtilleryTask task, BulletType bulletType) {
        // 地下/AP 标识是强制规则，范围规划不得为了多目标覆盖改用其它弹种。
        if (task.requiresAp && bulletType != BulletType.AP) return false;
        if (string.IsNullOrWhiteSpace(task.sourceImmuneShells)) return true;
        foreach (var value in task.sourceImmuneShells.Split(',')) {
            if (string.Equals(value.Trim(), bulletType.ToString(),
                    StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public float GetSpeedKmPerSecond(Vector3 missionVelocity) {
        return missionVelocity.magnitude * DistanceScale;
    }

    /// <summary>
    /// 按游戏实际绘制状态判断目标是否已暴露在测绘台，而不是读取任务注册表或 Hidden 状态。
    /// Demo 只有 Image_Icon/VisibilityGroup；正式版还可能把整组图形放在 VisualRoot 中。
    /// </summary>
    private static bool IsDesktopTargetVisible(EntityLocation location) {
        try {
            if (location == null || location.gameObject == null
                                 || !location.gameObject.activeInHierarchy) return false;

            var visibility = location.VisibilityGroup;
            if (visibility != null) {
                if (!visibility.gameObject.activeInHierarchy || visibility.alpha <= 0.01f) return false;
            }

            var icon = location.Image_Icon;
            if (icon == null || icon.gameObject == null
                             || !icon.gameObject.activeInHierarchy
                             || !icon.enabled || icon.color.a <= 0.01f) return false;

            // VisualRoot 是正式版新增字段。用反射读取可保持同一逻辑 DLL 兼容 Demo。
            var visualRootProperty = location.GetType().GetProperty("VisualRoot");
            if (visualRootProperty?.GetValue(location) is GameObject visualRoot
                && !visualRoot.activeInHierarchy) return false;

            return true;
        }
        catch {
            // 只读桌面模式宁可漏掉一帧目标，也不能越过玩家要求锁定未暴露实体。
            return false;
        }
    }

    public List<EntityLocation> GetAllFireMissionEntities() {
        List<EntityLocation> res = new();
        if (fireMissionRoot == null) {
            return res;
        }

        var seen = new HashSet<int>();
        void Add(EntityLocation? location) {
            if (location == null) return;
            var instanceId = location.GetInstanceID();
            if (seen.Add(instanceId)) res.Add(location);
        }

        // 普通目标通常是第一层子物体；正式版任务也会把航迹目标嵌套到专用容器中。
        foreach (var location in fireMissionRoot.GetComponentsInChildren<EntityLocation>(true)) {
            Add(location);
        }

        // 再读取 FireMission 的注册表，覆盖位置对象不在 Fire Mission Root 层级下的任务实体。
        try {
            if (fireMission?.Entities != null) {
                foreach (var pair in fireMission.Entities) Add(pair.Value?.Location);
            }
        }
        catch {
            // 任务图可能恰在增删实体；下一次两秒扫描会重新取得完整列表。
        }
        return res;
    }

    private sealed class MotionTrack {
        public Vector3 Position;
        public Vector3 Velocity;
        public float SampleTime;
        public int StableSamples;
        public int StationarySamples;
        public bool HasObservedMotion;
    }
    
}
