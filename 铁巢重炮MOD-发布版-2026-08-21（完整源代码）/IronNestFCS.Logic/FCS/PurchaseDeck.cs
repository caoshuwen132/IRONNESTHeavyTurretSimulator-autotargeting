using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;
using System.Collections;
using static System.Enum;

namespace IronNestFCS.Logic.FCS;

public sealed class ShellBlastProfile {
    public BulletType Type { get; init; }
    public float ImpactRadiusMission { get; init; }
    public int Damage { get; init; }
    public int ProjectilesPerShell { get; init; }
    public int Cost { get; init; }
    public bool CanKillTargets { get; init; }
    public int EntityDamageNodeCount { get; init; }
    public bool HasNonImmediateEffectNodes { get; init; }
    public string ImpactNodeTypes { get; init; } = "";
    public string AutoSelectionReason { get; init; } = "";
    public string ImpactGraphName { get; init; } = "";
}

public class PurchaseDeck {
    private Transform? _powderCard;
    private Dictionary<BulletType, Transform> bulletCards = new();
    private readonly List<ShellBlastProfile> blastProfiles = new();
    private LookAtTarget? _buyButton;

    public IReadOnlyList<ShellBlastProfile> BlastProfiles => blastProfiles;
    
    
    public bool TryBind() {
        bulletCards.Clear();
        blastProfiles.Clear();
        _powderCard = null;
        var requisitionConsole = GameObject.Find("Requisition Console").transform;
        var cards = requisitionConsole.GetComponentsInChildren<PunchcardRuntime>();
        foreach (var card in cards) {
            MelonLogger.Msg(
                $"[FCS] PurchaseDeck: Found card {card.CurrentDefinition.ID}, " +
                $"cost={card.CurrentDefinition.Cost}");
            if (BulletTypeNames.TryParse(card.CurrentDefinition.ID, out var type)) {
                bulletCards[type] = card.transform;
            }
            else if (card.CurrentDefinition.ID == "PowderCharges") {
                _powderCard = card.transform;
            }
        }
        _buyButton = requisitionConsole.FindChild("Universal Button").GetComponent<LookAtTarget>();
        DiscoverBlastProfiles();
        AuditAmmunitionCatalog();
        
        return true;
    }

    private void DiscoverBlastProfiles() {
        try {
            var bestByType = new Dictionary<BulletType, ShellBlastProfile>();
            foreach (var definition in Resources.FindObjectsOfTypeAll<ShellDefinition>()) {
                if (definition == null || definition.Damage <= 0 || definition.ImpactRadius < 0f) {
                    continue;
                }
                if (!BulletTypeNames.TryParse(definition.ShellId, out var type)
                    || !bulletCards.ContainsKey(type)) continue;

                AnalyzeImpactGraph(
                    definition,
                    out var entityDamageNodeCount,
                    out var hasNonImmediateEffectNodes,
                    out var impactNodeTypes);
                var isObservedNonImmediateShell = IsObservedNonImmediateShell(type);
                var canKillTargets = definition.Damage > 0
                                     && entityDamageNodeCount > 0
                                     && !hasNonImmediateEffectNodes
                                     && !isObservedNonImmediateShell;
                var profile = new ShellBlastProfile {
                    Type = type,
                    ImpactRadiusMission = definition.ImpactRadius,
                    Damage = definition.Damage,
                    ProjectilesPerShell = Math.Max(1, definition.projectilesPerShell),
                    Cost = GetShellCost(type),
                    CanKillTargets = canKillTargets,
                    EntityDamageNodeCount = entityDamageNodeCount,
                    HasNonImmediateEffectNodes = hasNonImmediateEffectNodes,
                    ImpactNodeTypes = impactNodeTypes,
                    AutoSelectionReason = canKillTargets
                        ? "direct entity damage"
                        : isObservedNonImmediateShell
                            ? "observed conditional/special effect"
                            : hasNonImmediateEffectNodes
                                ? "conditional, delayed, or state-changing effect graph"
                                : entityDamageNodeCount == 0
                                    ? "no entity damage node"
                                    : "non-positive damage",
                    ImpactGraphName = definition.Graph?.name ?? ""
                };
                if (!bestByType.TryGetValue(type, out var existing)
                    || profile.ImpactRadiusMission > existing.ImpactRadiusMission
                    || profile.ImpactRadiusMission == existing.ImpactRadiusMission
                    && profile.Damage > existing.Damage) {
                    bestByType[type] = profile;
                }
            }

            blastProfiles.AddRange(bestByType.Values);
            foreach (var profile in blastProfiles.OrderBy(item => item.Type)) {
                MelonLogger.Msg(
                    $"[FCS] Shell profile: {profile.Type}, damage={profile.Damage}, " +
                    $"impactRadius={profile.ImpactRadiusMission:F3} mission units, " +
                    $"projectiles={profile.ProjectilesPerShell}, cost={profile.Cost}, " +
                    $"lethal={profile.CanKillTargets}, damageNodes={profile.EntityDamageNodeCount}, " +
                    $"nonImmediateNodes={profile.HasNonImmediateEffectNodes}, " +
                    $"reason={profile.AutoSelectionReason}, graph={profile.ImpactGraphName}, " +
                    $"nodes=[{profile.ImpactNodeTypes}]");
            }
            if (blastProfiles.Count == 0) {
                MelonLogger.Warning(
                    "[FCS] Shell profiles: no purchasable damaging area shells were found");
            }
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS] Shell profiles: discovery failed: {ex.Message}");
        }
    }

    private static void AnalyzeImpactGraph(
        ShellDefinition definition,
        out int entityDamageNodeCount,
        out bool hasNonImmediateEffectNodes,
        out string impactNodeTypes) {
        // ShellDefinition.Damage 只是数值，不代表效果图一定会把它施加给地图实体。
        // SMK 等功能弹也可能带正 Damage 值，但图中没有 State_DamageEntity。
        // 检查实际效果图可以自动兼容以后加入的非致命/特殊效果弹，不依赖弹种名称。
        var nodes = definition.Graph?.nodes;
        entityDamageNodeCount = 0;
        hasNonImmediateEffectNodes = false;
        if (nodes == null) {
            impactNodeTypes = "";
            return;
        }

        var nodeTypes = new List<string>();
        foreach (var node in nodes) {
            if (node == null) continue;
            var nodeType = node.GetIl2CppType().Name;
            nodeTypes.Add(nodeType);
            if (node.TryCast<Il2CppSleepyNodes.State_DamageEntity>() != null) {
                entityDamageNodeCount++;
            }
            if (IsNonImmediateEffectNode(nodeType)) hasNonImmediateEffectNodes = true;
        }
        impactNodeTypes = string.Join(",", nodeTypes);
    }

    private static bool IsNonImmediateEffectNode(string nodeType) {
        // 直接命中杀伤不应依赖条件分支、等待/计时、状态改变或驱散移动。
        // 新增弹种只要使用这些效果节点，也会自动退出自动选弹候选。
        return nodeType.Contains("Condition", StringComparison.OrdinalIgnoreCase)
               || nodeType.Contains("Wait", StringComparison.OrdinalIgnoreCase)
               || nodeType.Contains("Delay", StringComparison.OrdinalIgnoreCase)
               || nodeType.Contains("Timer", StringComparison.OrdinalIgnoreCase)
               || nodeType.Contains("SetEntityState", StringComparison.OrdinalIgnoreCase)
               || nodeType.Contains("MoveMapEntity", StringComparison.OrdinalIgnoreCase)
               || nodeType.Contains("Status", StringComparison.OrdinalIgnoreCase)
               || nodeType.Contains("Disper", StringComparison.OrdinalIgnoreCase)
               || nodeType.Contains("Scatter", StringComparison.OrdinalIgnoreCase)
               || nodeType.Contains("Flee", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsObservedNonImmediateShell(BulletType type) {
        // 当前游戏把 PHGN/CYAN 的后续效果也序列化为 DamageEntity，单凭 Damage=1
        // 会误判为一击必杀；实战中它们需要额外条件或只造成驱散/状态效果。
        // 这里是运行数据无法表达因果关系时的安全兜底，功能弹仍可手动使用。
        return type == BulletType.PHGN
               || type == BulletType.CYAN
               || type == BulletType.SMK
               || type == BulletType.STAR
               || type == BulletType.TEAR;
    }

    public bool TryGetShellProfile(BulletType type, out ShellBlastProfile profile) {
        profile = blastProfiles.FirstOrDefault(item => item.Type == type)!;
        return profile != null;
    }

    private void AuditAmmunitionCatalog() {
        var definitions = new Dictionary<BulletType, List<string>>();
        foreach (var definition in Resources.FindObjectsOfTypeAll<ShellDefinition>()) {
            if (definition == null
                || !BulletTypeNames.TryParse(definition.ShellId, out var type)) continue;
            if (!definitions.TryGetValue(type, out var shellIds)) {
                shellIds = new List<string>();
                definitions[type] = shellIds;
            }
            if (!shellIds.Contains(definition.ShellId)) shellIds.Add(definition.ShellId);
        }

        var dialReady = false;
        try {
            dialReady = FindLeftRightDial() != null;
        }
        catch {
            // 审计只报告状态，不能让缺少控制台的场景中断绑定。
        }
        foreach (BulletType type in Enum.GetValues(typeof(BulletType))) {
            bulletCards.TryGetValue(type, out var card);
            var cardRuntime = card?.GetComponent<PunchcardRuntime>();
            var cardReady = card != null
                            && cardRuntime != null
                            && FindDraggable(card) != null;
            var purchaseBindingReady = cardReady && _buyButton != null && dialReady;
            definitions.TryGetValue(type, out var shellIds);
            var definitionReady = shellIds is { Count: > 0 };
            var hasProfile = TryGetShellProfile(type, out var profile);
            MelonLogger.Msg(
                $"[FCS] Ammo audit: {type}, " +
                $"purchaseBinding={(purchaseBindingReady ? "PASS" : "FAIL")}, " +
                $"card={cardRuntime?.CurrentDefinition?.ID ?? "missing"}, " +
                $"cost={(cardRuntime?.CurrentDefinition?.Cost.ToString() ?? "unknown")}, " +
                $"definition={(definitionReady ? string.Join("|", shellIds!) : "missing")}, " +
                $"profile={(hasProfile ? "yes" : "no")}, " +
                $"autoLethal={(hasProfile && profile.CanKillTargets ? "yes" : "no")}");
        }
    }

    public bool HasShellCard(BulletType type) => bulletCards.ContainsKey(type);

    public int GetShellCost(BulletType type) {
        var card = bulletCards.GetValueOrDefault(type);
        return card?.GetComponent<PunchcardRuntime>()?.CurrentDefinition?.Cost ?? 3;
    }

    public int GetPowderCost() {
        return _powderCard?.GetComponent<PunchcardRuntime>()?.CurrentDefinition?.Cost ?? 0;
    }
    
    private DialInteractable? FindLeftRightDial() {
        var consoleBox = GameObject.Find("Console Box")?.transform;
        return consoleBox?.GetComponentInChildren<DialInteractable>();
    }

    /// <summary>
    /// 正式版不同任务场景的卡牌层级并不完全相同：PunchcardRuntime 有时挂在
    /// DraggableItem 自身，有时位于它的子物体。购买与审计统一解析真实拖拽根节点。
    /// </summary>
    private static DraggableItem? FindDraggable(Transform card) {
        return card.GetComponent<DraggableItem>()
               ?? card.GetComponentInParent<DraggableItem>()
               ?? card.GetComponentInChildren<DraggableItem>();
    }

    public IEnumerator BuyShell(BulletType type, LeftRight leftRight) {
        var card = bulletCards.GetValueOrDefault(type);
        if (card == null) {
            MelonLogger.Error($"[FCS] BuyShell: Can't find {type} card");
            yield break;
        }
        var draggable = FindDraggable(card);
        if (draggable == null) {
            MelonLogger.Error(
                $"[FCS] BuyShell: Can't find draggable hierarchy for {type} card " +
                $"({card.name})");
            yield break;
        }
        var target = new Vector3(6.4814f, -2.4675f, -22.0968f);
        draggable.transform.position = target;
        draggable.MoveToSlot();
        yield return new WaitForSeconds(0.5f);
        
        var leftRightDial = FindLeftRightDial();
        if (leftRightDial == null) {
            MelonLogger.Error("[FCS] BuyShell: Can't find left/right purchase dial");
            yield break;
        }
        switch (leftRight) {
            case LeftRight.Left:
                leftRightDial.SetDialValue(0);
                break;
            case LeftRight.Right:
                leftRightDial.SetDialValue(1);
                break;
        }
        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return new WaitForSeconds(2f);
    }

    public IEnumerator BuyPowders() {
        if (_powderCard == null) {
            MelonLogger.Error("[FCS] BuyPowders: Can't find PowderCharges card");
            yield break;
        }
        var draggable = FindDraggable(_powderCard);
        if (draggable == null) {
            MelonLogger.Error(
                $"[FCS] BuyPowders: Can't find draggable hierarchy for " +
                $"{_powderCard.name}");
            yield break;
        }
        draggable.transform.position = new Vector3(6.4814f, -2.4675f, -22.0968f);
        draggable.MoveToSlot();
        // 与 BuyShell 一致：等卡牌入槽稳定后再点购买，避免点击早于入槽导致本次采购无效。
        yield return new WaitForSeconds(0.5f);
        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return new WaitForSeconds(2f);
    }
    
}
