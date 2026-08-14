using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using HarmonyInstance = HarmonyLib.Harmony;

namespace IronNestFCS.Logic;

/// <summary>
/// 记录游戏实际发放的征用积分。这里只观察并写日志，不修改积分数值。
/// </summary>
internal static class RequisitionPointsMonitor
{
    private static bool hasBaseline;
    private static int lastObservedTotal;

    public static bool HasCurrentTotal { get; private set; }
    public static int CurrentTotal { get; private set; }
    public static bool HasLastAward { get; private set; }
    public static int LastAwardAmount { get; private set; }
    public static int LastTotal { get; private set; }
    public static string LastSource { get; private set; } = "";

    public static void Install(HarmonyInstance harmony)
    {
        try {
            var original = AccessTools.Method(
                typeof(MissionStatsTracker),
                nameof(MissionStatsTracker.AddRequisitionPoints),
                new[] { typeof(int), typeof(string) });
            var postfix = AccessTools.Method(
                typeof(RequisitionPointsMonitor),
                nameof(AfterAddRequisitionPoints));
            if (original == null || postfix == null) {
                MelonLogger.Warning("[FCS] Requisition monitor: method not found");
                return;
            }

            harmony.Patch(original, postfix: new HarmonyMethod(postfix));
            MelonLogger.Msg("[FCS] Requisition monitor: installed");
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS] Requisition monitor: install failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 直接监测游戏的积分总数。部分 IL2CPP 内部调用不会经过 Harmony 的托管包装器，
    /// 但最终都必须更新这个计数器，因此轮询差额可以覆盖任务脚本和原生调用。
    /// </summary>
    public static void Poll()
    {
        try {
            var tracker = MissionStatsTracker.Instance;
            if (tracker == null) {
                HasCurrentTotal = false;
                hasBaseline = false;
                return;
            }

            var total = tracker.RequisitionPoints;
            HasCurrentTotal = true;
            CurrentTotal = total;

            if (!hasBaseline) {
                hasBaseline = true;
                lastObservedTotal = total;
                MelonLogger.Msg($"[FCS] Requisition counter: baseline={total}");
                return;
            }

            if (total > lastObservedTotal) {
                RecordAward(total - lastObservedTotal, "counter change", total);
            }

            lastObservedTotal = total;
        }
        catch (Exception ex) {
            HasCurrentTotal = false;
            MelonLogger.Warning($"[FCS] Requisition counter: read failed: {ex.Message}");
        }
    }

    private static void AfterAddRequisitionPoints(
        MissionStatsTracker __instance, int amount, string? source)
    {
        try {
            var label = string.IsNullOrWhiteSpace(source) ? "unspecified" : source;
            RecordAward(amount, label, __instance.RequisitionPoints);
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS] Requisition monitor: log failed: {ex.Message}");
        }
    }

    private static void RecordAward(int amount, string source, int total)
    {
        HasCurrentTotal = true;
        CurrentTotal = total;
        HasLastAward = true;
        LastAwardAmount = amount;
        LastTotal = total;
        LastSource = source;
        hasBaseline = true;
        lastObservedTotal = total;
        MelonLogger.Msg(
            $"[FCS] Requisition awarded: +{amount}, source={source}, total={total}");
    }
}
