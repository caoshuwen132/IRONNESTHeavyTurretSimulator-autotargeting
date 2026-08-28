using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

/// <summary>
/// 火控系统的 IMGUI 窗口。只负责绘制与把用户操作转发给 <see cref="FSC"/>。
/// 不含领域逻辑——按钮点击后调用 logic 的方法。
///
/// 实现说明：在 MelonLoader IL2CPP 下，MelonMod.OnGUI 每帧只触发一次，
/// 无法保证 IMGUI 所需的 Layout / event 多 pass。GUILayout 依赖 Layout pass
/// 预算尺寸，pass 不一致时 controlID 会错位，表现为"只有第一个按钮能点"。
/// 因此这里改用绝对 Rect 的 GUI.* API（不走布局系统），并且不套 GUI.Window
/// （避免回调委托封送丢失 pass）。控件 controlID 仅取决于调用顺序，稳定可靠。
/// </summary>
public class FcsWindow
{
    private readonly FSC fcs;

    private bool showWindow = true;
    private Rect defaultWindowRect = new(40, 40, 300, 226);

    public FcsWindow(FSC fcs)
    {
        this.fcs = fcs;
    }

    public void OnGui()
    {
        if (!showWindow)
            return;

        RequisitionPointsMonitor.Poll();
        var windowRect = defaultWindowRect;

        if (fcs.LeftTask != null) {
            windowRect.height += 28f;
        }
        if (fcs.RightTask != null) {
            windowRect.height += 28f;
        }
        if (RequisitionPointsMonitor.HasCurrentTotal) {
            windowRect.height += 56f;
        }
        if (!string.IsNullOrWhiteSpace(fcs.ManualFriendlyFireWarning)) {
            windowRect.height += 28f;
        }
        windowRect.height += 28f;
        windowRect.height += 28f;
        windowRect.height += 28f;
        windowRect.height += fcs.QueueCan.Count * 28f;

        // 背景框
        GUI.Box(windowRect, "铁巢火控系统");
        
        float x = windowRect.x + 10f;
        float w = windowRect.width - 20f;
        float y = windowRect.y + 25f;
        const float h = 24f;
        const float gap = 4f;

        if (!fcs.IsBound)
        {
            GUI.Label(new Rect(x, y, w, h), "等待游戏开始……");
            y += h + gap;
            GUI.Label(new Rect(x, y, w, h), "也可以按 F9 手动热重载。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(fcs.ManualFriendlyFireWarning)) {
            var originalColor = GUI.color;
            GUI.color = new Color(1f, 0.32f, 0.2f, 1f);
            GUI.Label(new Rect(x, y, w, h), fcs.ManualFriendlyFireWarning);
            GUI.color = originalColor;
            y += h + gap;
        }

        GUI.Label(new Rect(x, y, w, h), "左炮：");
        y += h + gap;
        if (fcs.LeftTask != null) {
            GUI.Label(new Rect(x, y, w, h), $"  {TargetLabel(fcs.LeftTask)}{RewardLabel(fcs.LeftTask)} {fcs.LeftTask.bulletType} {ProgressLabel(fcs.LeftTask.progress)}");
            y += h + gap;
            GUI.Label(new Rect(x, y, w, h), $"  目标：{fcs.LeftTask.angel:F1}°，{fcs.LeftTask.distance:F2} 千米");
            y += h + gap;
        }
        else {
            GUI.Label(new Rect(x, y, w, h), "  空闲");
            y += h + gap;
        }
        GUI.Label(new Rect(x, y, w, h), "右炮：");
        y += h + gap;
        if (fcs.RightTask != null) {
            GUI.Label(new Rect(x, y, w, h), $"  {TargetLabel(fcs.RightTask)}{RewardLabel(fcs.RightTask)} {fcs.RightTask.bulletType} {ProgressLabel(fcs.RightTask.progress)}");
            y += h + gap;
            GUI.Label(new Rect(x, y, w, h), $"  目标：{fcs.RightTask.angel:F1}°，{fcs.RightTask.distance:F2} 千米");
            y += h + gap;
        }
        else {
            GUI.Label(new Rect(x, y, w, h), "  空闲");
            y += h + gap;
        }

        GUI.Label(new Rect(x, y, w, h), $"等待队列：{fcs.PendingCount}");
        y += h + gap;
        GUI.Label(new Rect(x, y, w, h),
            $"自动索敌：{OnOff(fcs.AutoTargetEnabled)}  范围：{(fcs.DesktopOnlyEnabled ? "桌面" : "全部")}  已发现：{fcs.DetectedEnemyCount}");
        y += h + gap;
        GUI.Label(new Rect(x, y, w, h),
            $"自由视角：{OnOff(fcs.FreeCameraActive)}  [F10]");
        y += h + gap;
        GUI.Label(new Rect(x, y, w, h),
            $"碰撞箱：{OnOff(fcs.ColliderOverlayActive)}  [F11]");
        y += h + gap;
        var requisitionMode = fcs.AutoTargetBudgetPaused
            ? "积分不足"
            : RequisitionPointsMonitor.HasCurrentTotal
              && RequisitionPointsMonitor.CurrentTotal < fcs.RequisitionTarget
                ? "积累中"
                : "就绪";
        GUI.Label(new Rect(x, y, w, h),
            $"积分模式：{requisitionMode}  保底：{fcs.RequisitionTarget}");
        y += h + gap;
        GUI.Label(new Rect(x, y, w, h),
            $"阀门：{fcs.DetectedValveCount}  松动：{fcs.LooseValveCount}  已修复：{fcs.RepairedValveCount}");
        y += h + gap;
        if (RequisitionPointsMonitor.HasCurrentTotal) {
            GUI.Label(new Rect(x, y, w, h),
                $"当前积分：{RequisitionPointsMonitor.CurrentTotal}");
            y += h + gap;
            GUI.Label(new Rect(x, y, w, h),
                RequisitionPointsMonitor.HasLastAward
                    ? $"最近奖励：+{RequisitionPointsMonitor.LastAwardAmount}（{RewardSourceLabel(RequisitionPointsMonitor.LastSource)}）"
                    : "最近奖励：等待变化");
            y += h + gap;
        }
        foreach (var item in fcs.QueueCan)
        {
            GUI.Label(new Rect(x, y, w, h), $"  {TargetLabel(item)}{RewardLabel(item)} {ConvertPosition(item.position)} {item.angel,5:F1}°/{item.distance,5:F2}千米 {item.bulletType} ");
            y += h + gap;
        }

    }

    private static string TargetLabel(IronNestFCS.Logic.FCS.ArtilleryTask task)
    {
        var name = string.IsNullOrWhiteSpace(task.targetName) ? $"T{task.targetId}" : task.targetName;
        var hidden = task.isHidden ? "[隐] " : "";
        var moving = task.isMoving ? "[动] " : "";
        if (task.isLocomotive) return $"{hidden}{moving}[机车] {name}";
        if (task.isArtillery) return $"{hidden}{moving}[炮] {name}";
        if (task.isCommander) return $"{hidden}{moving}[指] {name}";
        if (task.isAntiAir) return $"{hidden}{moving}[防空] {name}";
        if (task.isSupply) return $"{hidden}{moving}[补给] {name}";
        if (task.isMechanized) return $"{hidden}{moving}[机步] {name}";
        if (task.isRecon) return $"{hidden}{moving}[侦察] {name}";
        return task.isInfantry ? $"{hidden}{moving}[步兵] {name}" : $"{hidden}{moving}{name}";
    }

    private static string RewardLabel(IronNestFCS.Logic.FCS.ArtilleryTask task)
    {
        var reward = task.sourceRewardPoints >= 0 ? $" 奖+{task.sourceRewardPoints}" : "";
        var area = task.areaTargetCount > 1 ? $" 范围×{task.areaTargetCount}" : "";
        return reward + area;
    }

    private static string OnOff(bool enabled) => enabled ? "开" : "关";

    private static string ProgressLabel(IronNestFCS.Logic.FCS.Progress progress) => progress switch
    {
        IronNestFCS.Logic.FCS.Progress.Pending => "等待中",
        IronNestFCS.Logic.FCS.Progress.Calculating => "计算中",
        IronNestFCS.Logic.FCS.Progress.SelectingBullet => "选弹中",
        IronNestFCS.Logic.FCS.Progress.LoadingBullet => "装弹中",
        IronNestFCS.Logic.FCS.Progress.LoadingPowder => "装药中",
        IronNestFCS.Logic.FCS.Progress.WaitLoading => "等待装填",
        IronNestFCS.Logic.FCS.Progress.Aiming => "瞄准中",
        IronNestFCS.Logic.FCS.Progress.WaitingForFire => "等待发射",
        IronNestFCS.Logic.FCS.Progress.BackToIdle => "复位中",
        IronNestFCS.Logic.FCS.Progress.Finished => "已完成",
        IronNestFCS.Logic.FCS.Progress.Failed => "失败",
        _ => progress.ToString()
    };

    private static string RewardSourceLabel(string source) => source.Trim().ToLowerInvariant() switch
    {
        "counter change" => "积分变化",
        "unspecified" => "未注明来源",
        "" => "未注明来源",
        _ => source
    };

    /// <summary> 计算坐标点所对应的区域字符串 </summary>
    public static string ConvertPosition(Vector3 position)
    {
        int leterIndex = (int)position.x;
        string zoneCol = leterIndex >= 0 && leterIndex < 26 ? ((char)('A' + leterIndex)).ToString() : "#";
        int zoneRow = (int)position.y + 1;
        int subCol = (int)(position.x * 10) % 10;  // B: 第一位小数
        int subRow = (int)(position.y * 10) % 10;  // B: 第一位小数

        return $"{zoneCol}{zoneRow}  {subCol}:{subRow}";
    }
}
