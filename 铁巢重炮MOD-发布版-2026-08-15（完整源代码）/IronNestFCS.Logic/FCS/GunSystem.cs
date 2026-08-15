using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;


public enum BulletType {
    AP = 1,
    APHE = 2,
    ATMC = 3,
    CLMN = 4,
    CYAN = 5,
    DRIL = 6,
    EQKE = 7,
    FLCH = 8,
    HCHE = 9,
    HE = 10,
    INCN = 11,
    LE = 12,
    PCLM = 13,
    PHGN = 14,
    PRPG = 15,
    SMK = 16,
    STAR = 17,
    TEAR = 18,
    THRM = 19,
    WP = 20,
}

public static class BulletTypeNames {
    public static bool TryParse(string? value, out BulletType type) {
        var normalized = (value ?? "")
            .Replace("SMOKE", "SMK", StringComparison.OrdinalIgnoreCase)
            .Replace("Shell", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "")
            .Replace("_", "")
            .Replace("-", "");
        // 旧修改版曾把游戏的 PCLM 误写为 PLCM。保留读取兼容，
        // 但所有新 UI、购买卡匹配和炮膛 ID 都使用游戏的正式代号 PCLM。
        if (normalized.Equals("PLCM", StringComparison.OrdinalIgnoreCase)) {
            normalized = "PCLM";
        }
        return Enum.TryParse(normalized, true, out type);
    }

    public static string? Canonicalize(string? value) {
        if (value == null) return null;
        return TryParse(value, out var type) ? type.ToString() : value;
    }
}

public class GunSystem {
    private const float PostFireRecoverySeconds = 13f;
    private const float ElevationReadyToleranceDeg = 0.08f;
    private string _surfix = "";
    private float _nextLoadReadyAt;
    private bool _postFireRecoveryPending;

    private CylinderShellSelector? shellSelector;
    
    private List<string?> bullets = new();
    private LookAtTarget? nextBulletButton;
    private LookAtTarget? loadBulletButton;
    private List<LookAtTarget> powderButtons = new();
    private LookAtTarget? loadPowderButton;
    private PowderChargeController? powderController;
    private GunController? gunController;
    private LinearSliderInteractable? elevationLever;
    private OdometerDisplay? remainingCharges;


    public bool TryBind(string surfix) {
        this._surfix = surfix;
        
        var gunSystem = GameObject.Find("Gun System " + surfix).transform;
        var reloadingConsole = gunSystem.Find("--Reloading Console");
        if (reloadingConsole == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find --Reloading Console");
            return false;
        }

        remainingCharges = reloadingConsole.GetComponentInChildren<OdometerDisplay>();
        
        nextBulletButton = 
            reloadingConsole.Find("Universal Button Move Cylinder")
                .GetComponent<LookAtTarget>();    
        shellSelector = gunSystem.GetComponentInChildren<CylinderShellSelector>();
        
        var loadShell = reloadingConsole.FindChild("Universal Button Load shell Rammer");
        if (loadShell == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find Universal Button Load shell Rammer");
            return false;
        }
        loadBulletButton = loadShell.GetComponent<LookAtTarget>();

        var powderControllerTransform = reloadingConsole.Find("PowderChargeController");
        powderController = powderControllerTransform.GetComponent<PowderChargeController>();
        powderButtons.Clear();
        for (var i = 0; i < powderControllerTransform.childCount; ++i) {
            var child = powderControllerTransform.GetChild(i);
            if (!child.name.StartsWith("Button Dispencer")) continue;
            var button = child.GetComponent<LookAtTarget>();
            if (button == null) {
                MelonLogger.Error($"[FCS] GunSystem {surfix}: Found {child.name} but lack of LookAtTarget Component");
                return false;
            }
            powderButtons.Add(button);
        }

        loadPowderButton = reloadingConsole.FindChild("Universal Button Charge Rammer (1)").GetComponent<LookAtTarget>();
        gunController = GameObject.Find("Gun"+surfix).GetComponent<GunController>();
        elevationLever = GameObject.Find(".Elevation Lever Baseplate")?.transform.FindChild(".Elevation Lever " + surfix)
            .GetComponent<LinearSliderInteractable>();
        return true;
    }
    
    public bool CanFire() {
        return gunController != null && gunController.CanFire;
    }

    /// <summary>
    /// 游戏火炮控制器给出的当前弹丸预计飞行时间。尚未生成有效预测时按射程保守估算，
    /// 避免移动目标被当成零飞行时间的静止目标射击。
    /// </summary>
    public float PredictedImpactSeconds(float distanceKm) {
        var seconds = gunController?.PredictedImpactTime ?? 0f;
        if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 1f || seconds > 180f) {
            seconds = Mathf.Clamp(distanceKm * 4f, 15f, 120f);
        }
        return seconds;
    }

    public IEnumerator SetElevation(float elevation) {
        if (elevationLever == null || gunController == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: Elevation lever or gun controller unbound");
            yield break;
        }
        CommandElevation(elevation);
        yield return new WaitForSeconds(0.1f);
        while (!IsElevationReady(elevation)) {
            CommandElevation(elevation);
            yield return new WaitForSeconds(1f);
        }
    }

    /// <summary>直接把目标仰角交给本管炮，不等待机构到位，供移动中连续跟踪使用。</summary>
    public void CommandElevation(float elevation) {
        if (elevationLever != null) elevationLever.SetSliderValue(elevation);
    }

    public bool IsElevationReady(float elevation, float toleranceDeg = ElevationReadyToleranceDeg) {
        return gunController != null
               && Mathf.Abs(gunController.CurrentElevation - elevation) <= toleranceDeg;
    }

    /// <summary>
    /// 使用炮管自身的 MapElevationToRange 弹道模型反求低伸弹道仰角。
    /// 此时炮弹和装药必须已经入膛；整个过程只读游戏模型，不操作实体弹道计算器，
    /// 因而不会生成左下角的计算卡片。
    /// </summary>
    public bool TrySolveElevation(float distanceKm, out float elevation, out string reason) {
        elevation = 0f;
        reason = "";
        if (gunController == null) {
            reason = "gun controller is not bound";
            return false;
        }
        var shell = gunController.ChamberedShellBlueprint;
        if (shell?.shellDefinition == null) {
            reason = "no chambered shell for direct ballistic solve";
            return false;
        }
        if (!IsFinite(distanceKm) || distanceKm <= 0f) {
            reason = $"invalid range {distanceKm:F2} km";
            return false;
        }

        try {
            var minElevation = gunController.MinElevationAngle;
            var maxElevation = gunController.parentTurret?.maxBarrelElevation ?? 89f;
            if (!IsFinite(minElevation) || !IsFinite(maxElevation) || maxElevation <= minElevation) {
                minElevation = 0f;
                maxElevation = 89f;
            }

            // 优先让炮管控制器用自己的反解入口把射程换成仰角，再以正向模型校验。
            // 这不会触发 ArtilleryComputer 的按钮、事件或卡片生成流程。
            gunController.SetDesiredRange(distanceKm);
            var nativeElevation = gunController.DesiredElevationAngle;
            if (IsFinite(nativeElevation)) {
                var nativeRange = gunController.MapElevationToRange(nativeElevation);
                if (IsFinite(nativeRange) && Mathf.Abs(nativeRange - distanceKm) <= 0.05f) {
                    elevation = nativeElevation;
                    return true;
                }
            }

            // 原生反解若因任务状态尚未刷新而没有给出有效值，则用正向模型数值反求兜底。
            // 若存在高、低两条弹道，选择较低仰角，与游戏实体计算器正常输出保持一致。
            const int coarseSteps = 48;
            var previousElevation = minElevation;
            var previousRange = gunController.MapElevationToRange(previousElevation);
            var bestElevation = previousElevation;
            var bestError = IsFinite(previousRange)
                ? Mathf.Abs(previousRange - distanceKm)
                : float.PositiveInfinity;
            var foundBracket = false;
            var leftElevation = minElevation;
            var rightElevation = maxElevation;
            var leftError = previousRange - distanceKm;

            for (var step = 1; step <= coarseSteps; ++step) {
                var candidateElevation = Mathf.Lerp(minElevation, maxElevation, step / (float)coarseSteps);
                var candidateRange = gunController.MapElevationToRange(candidateElevation);
                if (!IsFinite(candidateRange)) continue;
                var candidateError = Mathf.Abs(candidateRange - distanceKm);
                if (candidateError < bestError) {
                    bestError = candidateError;
                    bestElevation = candidateElevation;
                }

                var currentSignedError = candidateRange - distanceKm;
                if (IsFinite(previousRange)
                    && (Mathf.Approximately(leftError, 0f)
                        || Mathf.Approximately(currentSignedError, 0f)
                        || (leftError < 0f) != (currentSignedError < 0f))) {
                    leftElevation = previousElevation;
                    rightElevation = candidateElevation;
                    foundBracket = true;
                    break;
                }
                previousElevation = candidateElevation;
                previousRange = candidateRange;
                leftError = currentSignedError;
            }

            if (foundBracket) {
                var leftRange = gunController.MapElevationToRange(leftElevation);
                for (var iteration = 0; iteration < 18; ++iteration) {
                    var middle = (leftElevation + rightElevation) * 0.5f;
                    var middleRange = gunController.MapElevationToRange(middle);
                    if (!IsFinite(middleRange)) break;
                    var error = Mathf.Abs(middleRange - distanceKm);
                    if (error < bestError) {
                        bestError = error;
                        bestElevation = middle;
                    }
                    if ((leftRange - distanceKm < 0f) != (middleRange - distanceKm < 0f)) {
                        rightElevation = middle;
                    }
                    else {
                        leftElevation = middle;
                        leftRange = middleRange;
                    }
                }
            }

            // 五十米仍未能匹配说明本次装药/弹种不覆盖当前射程，禁止盲射。
            if (!IsFinite(bestError) || bestError > 0.05f) {
                reason = $"direct ballistic model cannot reach {distanceKm:F2} km " +
                         $"(closest error={bestError:F3} km, shell={shell.shellDefinition.ShellId}, " +
                         $"charges={gunController.PowderCharges})";
                return false;
            }
            elevation = bestElevation;
            return true;
        }
        catch (Exception ex) {
            reason = ex.Message;
            return false;
        }
    }

    public bool TryGetLoadedRange(out float minRangeKm, out float maxRangeKm, out string reason) {
        return TryGetRangeForCharge(
            LoadedPowderCharges, out minRangeKm, out maxRangeKm, out reason);
    }

    /// <summary>读取当前膛内弹种在指定装药数下的原生射程范围，不改变机械状态。</summary>
    public bool TryGetRangeForCharge(
        int chargeCount, out float minRangeKm, out float maxRangeKm, out string reason) {
        minRangeKm = 0f;
        maxRangeKm = 0f;
        reason = "";
        if (gunController?.ChamberedShellBlueprint == null) {
            reason = "no chambered shell";
            return false;
        }
        if (chargeCount < 1 || chargeCount > 6) {
            reason = $"invalid powder charge count {chargeCount}";
            return false;
        }
        try {
            gunController.ChamberedShellBlueprint.GetRangeForCharge(
                chargeCount, out minRangeKm, out maxRangeKm);
            if (!IsFinite(minRangeKm) || !IsFinite(maxRangeKm) || maxRangeKm <= minRangeKm) {
                reason = $"invalid range for {chargeCount} charge(s): " +
                         $"{minRangeKm:F2}-{maxRangeKm:F2} km";
                return false;
            }
            return true;
        }
        catch (Exception ex) {
            reason = ex.Message;
            return false;
        }
    }

    public string LoadedRoundDescription {
        get {
            if (gunController == null) return "unbound";
            return $"shell={BulletInChamber() ?? "empty"}, charges={gunController.PowderCharges}, " +
                   $"canFire={gunController.CanFire}, pendingReload={gunController.pendingReload}";
        }
    }

    public int LoadedPowderCharges => gunController?.PowderCharges ?? 0;

    /// <summary>
    /// 已从装药按钮取出、停在托盘上但尚未由推杆送入炮膛的装药数量。
    /// 该数值与 GunController.PowderCharges 不同，热重载时必须同时检查两者。
    /// </summary>
    public int StagedPowderCharges {
        get {
            if (powderController == null) return 0;
            return Mathf.Clamp(
                Mathf.RoundToInt(powderController.DispensedChargesFloat), 0, 6);
        }
    }

    /// <summary>
    /// 只读当前炮管的真实机械状态，把实际仰角通过原生正向弹道模型换算为射程。
    /// 供手动/自动调炮过程的桌面落点预览使用，不改变 DesiredRange、仰角或装填状态。
    /// </summary>
    public bool TryGetCurrentBallisticState(
        out BulletType bulletType, out float distanceKm, out string reason) {
        bulletType = default;
        distanceKm = 0f;
        reason = "";
        if (gunController == null) {
            reason = "gun controller is not bound";
            return false;
        }
        var shellId = BulletInChamber();
        if (!BulletTypeNames.TryParse(shellId, out bulletType)) {
            reason = string.IsNullOrWhiteSpace(shellId)
                ? "no chambered shell"
                : $"unknown chambered shell {shellId}";
            return false;
        }
        if (gunController.PowderCharges <= 0) {
            reason = "no powder charge loaded";
            return false;
        }

        try {
            var elevation = gunController.CurrentElevation;
            distanceKm = gunController.MapElevationToRange(elevation);
            if (!IsFinite(distanceKm) || distanceKm <= 0f) {
                reason = $"current elevation {elevation:F2} has no valid range";
                return false;
            }

            return true;
        }
        catch (Exception ex) {
            reason = ex.Message;
            return false;
        }
    }

    public void RequestFireDirect() {
        gunController?.RequestFire();
    }

    public float DirectFireLeadSeconds {
        get {
            var delay = gunController?.fireDelay ?? 0f;
            return IsFinite(delay) ? Mathf.Clamp(delay + 0.1f, 0.1f, 3f) : 0.35f;
        }
    }

    private static bool IsFinite(float value) {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
    
    public string? BulletInChamber() {
        return BulletTypeNames.Canonicalize(
            gunController?.ChamberedShellBlueprint?.shellDefinition?.ShellId);
    }
    
    public bool IsChamberEmpty() {
        return BulletInChamber() == null;
    }

    private void RefreshBullets() {
        bullets.Clear();
        if (shellSelector == null) return;
        foreach (var shell in shellSelector.bullets) {
            bullets.Add(BulletTypeNames.Canonicalize(
                shell?.GetComponent<ShellBlueprint>()?.shellDefinition?.ShellId));
        }
        MelonLogger.Msg($"[FCS] GunSystem {_surfix}: Cylinder bullets: {string.Join(", ", bullets)}");
    }

    public void NextBullet() {
        if (nextBulletButton == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: NextBulletButton unbound");
        }
        MelonLogger.Msg("[GunSystem] NextBullet");
        nextBulletButton!.OnClickDown();
    }
    
    /// <summary>
    /// 装填指定弹种：先把弹仓转到目标弹，再按装填。转弹仓每步之间要等 1 秒
    /// （游戏有转动动画/物理）。返回 IEnumerator，调用方用 yield return 等待它跑完。
    /// 必须走协程而非 async：continuation 要留在主线程才能安全访问 IL2CPP 对象。
    /// </summary>
    public IEnumerator LoadBullet(BulletType type) {
        var expectedShellId = type.ToString();
        if (BulletInChamber() == expectedShellId) yield break;
        RefreshBullets();
        var index = bullets.IndexOf(expectedShellId);
        if (index == -1) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: " +
                              $"No {type} available in cylinder, current bullets: {string.Join(", ", bullets)}");
            yield break;
        }
        
        for (var i = 0; i < bullets.Count; ++i) {
            if (bullets[0] == expectedShellId) {
                break;
            };
            NextBullet();
            yield return new WaitForSeconds(1.5f);
            RefreshBullets();
        }
        if (bullets[0] != expectedShellId) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: Can't find {type} after rotation, " +
                              $"current: {string.Join(", ", bullets)}");
            yield break;
        }
        // 强制退弹/复位后，原生推杆可能比固定安全间隔更晚恢复。不要释放炮管，
        // 也不要退弹；持续按真实按钮状态重试，直到这一发确实进入炮膛。
        var nextWaitLogAt = Time.realtimeSinceStartup + 15f;
        while (BulletInChamber() != expectedShellId) {
            if (loadBulletButton != null
                && loadBulletButton.isActive
                && loadBulletButton.nextAllowedClickTime <= Time.realtimeSinceStartup) {
                yield return FcsSceneInteractor.WaitAndClick(loadBulletButton);
                yield return new WaitForSeconds(0.5f);
            }
            else {
                yield return new WaitForSeconds(0.5f);
            }

            if (Time.realtimeSinceStartup < nextWaitLogAt) continue;
            MelonLogger.Warning(
                $"[FCS] GunSystem {_surfix}: waiting for native shell rammer recovery; " +
                $"expected={expectedShellId}, chamber={BulletInChamber() ?? "empty"}, " +
                $"buttonActive={loadBulletButton?.isActive}");
            nextWaitLogAt = Time.realtimeSinceStartup + 15f;
        }
    }

    private IEnumerator SelectPowder(int count, int alreadyStaged) {
        count = Mathf.Clamp(count, 1, Math.Min(6, powderButtons.Count));
        var nextWaitLogAt = Time.realtimeSinceStartup + 10f;
        var nextResetAt = Time.realtimeSinceStartup + 10f;
        while (StagedPowderCharges < count) {
            // 托盘在发射后重置时可能从旧值回退到 0；每个周期都从真实数量重新
            // 计算下一个按钮，不能继续沿用进入协程时的 alreadyStaged 快照。
            var index = Mathf.Clamp(StagedPowderCharges, 0, count - 1);
            var button = powderButtons[index];
            if (button.isActive
                && button.nextAllowedClickTime <= Time.realtimeSinceStartup) {
                yield return FcsSceneInteractor.WaitAndClick(button);
                continue;
            }

            if (StagedPowderCharges == 0
                && Time.realtimeSinceStartup >= nextResetAt
                && !IsNativeReloadPending
                && powderController != null) {
                MelonLogger.Warning(
                    $"[FCS] GunSystem {_surfix}: powder tray reset stalled with no staged " +
                    "charges; resetting used dispensers without ejecting the chambered shell");
                powderController.ResetAllUsedDispensers();
                nextResetAt = Time.realtimeSinceStartup + 10f;
            }
            if (Time.realtimeSinceStartup >= nextWaitLogAt) {
                MelonLogger.Warning(
                    $"[FCS] GunSystem {_surfix}: waiting to supplement staged powder " +
                    $"charge {index + 1}/{count}; initialStaged={alreadyStaged}, " +
                    $"currentStaged={StagedPowderCharges}, buttonActive={button.isActive}, " +
                    $"{LoadedRoundDescription}");
                nextWaitLogAt = Time.realtimeSinceStartup + 10f;
            }
            yield return new WaitForSeconds(0.1f);
        }
    }

    /// <summary>
    /// true 表示上一发已经击发、原生机构仍在复位。此时炮膛对象可能尚未从层级中移除，
    /// 但它不是可重新分配的遗留弹。
    /// </summary>
    public bool IsNativeReloadPending => gunController != null && gunController.pendingReload;

    /// <summary>
    /// 把装药托盘上的现有装药推入炮膛。热重载后托盘可能已经有 1～6 份装药，
    /// 此时只补足缺少的数量，绝不从第一个已经失活的按钮重新开始点击。
    /// </summary>
    public IEnumerator LoadPowder(int count) {
        if (LoadedPowderCharges > 0) yield break;

        count = Mathf.Clamp(count, 1, 6);
        var staged = StagedPowderCharges;
        if (staged > 0) {
            MelonLogger.Msg(
                $"[FCS] GunSystem {_surfix}: recovered {staged} staged powder charge(s) " +
                $"from the tray; requestedMinimum={count}");
        }
        if (staged < count) yield return SelectPowder(count, staged);

        var nextRammerAttemptAt = Time.realtimeSinceStartup;
        var nextWaitLogAt = Time.realtimeSinceStartup + 10f;
        while (LoadedPowderCharges <= 0) {
            // 装药托盘的旧读数可能在等待推杆时被原生发射后复位清零。此时重新
            // 选择所需数量，而不是拿空托盘永久等待一个不会启用的推杆。
            if (StagedPowderCharges <= 0) {
                yield return SelectPowder(count, 0);
                nextRammerAttemptAt = Time.realtimeSinceStartup;
                continue;
            }
            if (loadPowderButton != null
                && loadPowderButton.isActive
                && loadPowderButton.nextAllowedClickTime <= Time.realtimeSinceStartup
                && Time.realtimeSinceStartup >= nextRammerAttemptAt) {
                MelonLogger.Msg(
                    $"[FCS] GunSystem {_surfix}: ramming {StagedPowderCharges} staged " +
                    "powder charge(s) into the chamber");
                yield return FcsSceneInteractor.WaitAndClick(loadPowderButton);
                nextRammerAttemptAt = Time.realtimeSinceStartup + 3f;
            }
            else {
                yield return new WaitForSeconds(0.1f);
            }

            if (Time.realtimeSinceStartup < nextWaitLogAt) continue;
            MelonLogger.Warning(
                $"[FCS] GunSystem {_surfix}: waiting for charge rammer recovery; " +
                $"staged={StagedPowderCharges}, loaded={LoadedPowderCharges}, " +
                $"buttonActive={loadPowderButton?.isActive}, {LoadedRoundDescription}");
            nextWaitLogAt = Time.realtimeSinceStartup + 10f;
        }

        MelonLogger.Msg(
            $"[FCS] GunSystem {_surfix}: powder rammed successfully; " +
            $"loadedCharges={LoadedPowderCharges}");
    }

    public bool HaveBulletInCylinder(BulletType type) {
        RefreshBullets();
        return bullets.Contains(type.ToString());
    }
    
    public bool HaveEmptyShellInCylinder() {
        RefreshBullets();
        return bullets.Contains(null);
    }

    public IEnumerator WaitBackToIdle() {
        yield return WaitReadyForNextLoad();
    }

    public IEnumerator WaitReadyForNextLoad() {
        while (gunController != null && gunController.elevationChangeVelocity != 0) {
            yield return new WaitForSeconds(0.1f);
        }
        if (_postFireRecoveryPending && _nextLoadReadyAt <= 0f) {
            // 保留原流程“停止回位后再等 13 秒”的完整安全间隔；
            // 多个等待协程共享同一个绝对时刻，不会把 13 秒重复计算。
            _nextLoadReadyAt = Time.realtimeSinceStartup + PostFireRecoverySeconds;
        }
        while (_postFireRecoveryPending && Time.realtimeSinceStartup < _nextLoadReadyAt) {
            yield return new WaitForSeconds(0.1f);
        }
        _postFireRecoveryPending = false;

        // 炮膛已清空但原生 pendingReload 尚未结束时，装药托盘仍可能保留上一发的
        // 短暂旧读数。现有“机构停止 + 13 秒”安全门已经覆盖真实发射复位；通过安全门后
        // 直接清掉空膛残留，避免把旧 6 份误认成新装药，也避免再额外拖延推弹。
        // 膛内仍有遗留弹时不在这里处理，由遗留弹流程接管。
        if (gunController != null && IsChamberEmpty() && gunController.pendingReload) {
            // 真实发射后的调用到这里之前已经完成“机构停止 + 13 秒”安全等待；首次
            // 进局/热重载若没有 _postFireRecoveryPending，则更不应再额外空等 15 秒。
            // 空膛状态下直接清除原生残留，只复位分配器和状态位，不涉及退弹。
            powderController?.ResetAllUsedDispensers();
            gunController.pendingReload = false;
            MelonLogger.Warning(
                $"[FCS] GunSystem {_surfix}: cleared stale native pendingReload at the " +
                $"empty-chamber safety gate; staged={StagedPowderCharges}, no shell was ejected");
            yield return null;
        }
    }

    public IEnumerator WaitFire() {
        while (gunController != null && !gunController.pendingReload) {
            yield return new WaitForSeconds(0.1f);
        }
        _postFireRecoveryPending = true;
        _nextLoadReadyAt = 0f;
    }
    
    public int RemainingCharges() {
        return remainingCharges == null ? 0 : (int)remainingCharges.CurrentNumber;
    }

}
