using IronNestFCS.Logic.FCS;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

/// <summary>
/// 在测绘台上显示左右炮与等待队列的实时预计落点和炮弹杀伤半径。
/// 只读取现有任务数据，不参与目标选择、弹道解算或击发流程。
/// </summary>
public sealed class MapImpactPreview {
    private const float RefreshInterval = 0.1f;
    private const float AllyRefreshInterval = 0.5f;
    private const int CircleSegments = 64;
    // 恢复首版预览的粗线宽度；计划点和排队点仍用倍率区分，当前炮口落点最粗。
    private const float LineWidthMission = 0.045f;
    private const float MinimumCrossHalfSizeMission = 0.035f;
    private const float MaximumCrossHalfSizeMission = 0.12f;
    private const int MaximumQueuedPreviews = 2;

    // 测绘台的后处理会明显压暗普通 0-1 颜色；使用 HDR 强度保持接近界面箭头的亮度。
    private static readonly Color LeftColor = new(0f, 3f, 3f, 1f);
    private static readonly Color RightColor = new(3f, 3f, 0f, 1f);
    private static readonly Color QueuedColor = new(1f, 0.92f, 0.12f, 1f);
    private static readonly Color DangerColor = new(1f, 0.08f, 0.08f, 1f);

    private readonly FSC fcs;
    private readonly List<PreviewVisual> visuals = new();
    private readonly List<Vector3> allyPositions = new();
    private RectTransform? previewRoot;
    private float nextRefreshAt;
    private float nextAllyRefreshAt;

    public MapImpactPreview(FSC fcs) {
        this.fcs = fcs;
    }

    public void Update(bool enabled) {
        if (!enabled || !fcs.IsBound) {
            HideAll();
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (now < nextRefreshAt) return;
        nextRefreshAt = now + RefreshInterval;

        if (!fcs.MapTable.TryGetPreviewSurface(out var root, out var localZ)) {
            HideAll();
            return;
        }
        if (previewRoot != root) {
            DestroyVisuals();
            previewRoot = root;
            allyPositions.Clear();
            nextAllyRefreshAt = 0f;
        }

        PruneInvalidVisuals();
        var entries = new List<PreviewEntry>(4 + MaximumQueuedPreviews);
        AddGun(entries, LeftRight.Left, fcs.LeftTask, LeftColor);
        AddGun(entries, LeftRight.Right, fcs.RightTask, RightColor);
        var queued = fcs.QueueCan;
        for (var index = 0; index < MaximumQueuedPreviews && queued.Count > 0; ++index) {
            AddTask(entries, queued.Dequeue(), QueuedColor, 0.55f);
        }

        EnsureVisualCount(entries.Count, root);
        if (entries.Count > 0 && now >= nextAllyRefreshAt) {
            allyPositions.Clear();
            allyPositions.AddRange(fcs.MapTable.GetLiveAllyPositions());
            nextAllyRefreshAt = now + AllyRefreshInterval;
        }
        for (var index = 0; index < visuals.Count; ++index) {
            if (index >= entries.Count) {
                visuals[index].TrySetActive(false);
                continue;
            }

            var entry = entries[index];
            var radius = fcs.GetImpactPreviewRadiusMission(entry.Task);
            var unsafeImpact = MapTable.HasPositionWithin(
                allyPositions,
                entry.Task.position,
                fcs.GetImpactPreviewSafetyRadiusMission(entry.Task));
            visuals[index].Show(
                entry.Task.position,
                radius,
                localZ + Mathf.Sign(localZ == 0f ? 1f : localZ) * index * 0.001f,
                unsafeImpact ? DangerColor : entry.Color,
                entry.WidthMultiplier);
        }
    }

    public void HideAll() {
        foreach (var visual in visuals) visual.TrySetActive(false);
        PruneInvalidVisuals();
    }

    public void Shutdown() {
        DestroyVisuals();
        previewRoot = null;
        nextRefreshAt = 0f;
        nextAllyRefreshAt = 0f;
        allyPositions.Clear();
    }

    private static void AddTask(
        ICollection<PreviewEntry> entries,
        ArtilleryTask? task,
        Color color,
        float widthMultiplier = 1f) {
        if (task == null || task.progress is Progress.Finished or Progress.Failed) return;
        entries.Add(new PreviewEntry(task, color, widthMultiplier));
    }

    private void AddGun(
        ICollection<PreviewEntry> entries,
        LeftRight side,
        ArtilleryTask? plannedTask,
        Color color) {
        // 自动任务的最终计划点始终保留为细线；炮弹与装药入膛后再叠加一条
        // 较粗的当前机械落点。调炮时粗线持续移动，收敛后覆盖到细线之上。
        AddTask(entries, plannedTask, color, 0.55f);
        if (fcs.TryGetCurrentGunImpact(side, out var currentImpact)) {
            entries.Add(new PreviewEntry(currentImpact, color, 1f));
        }
    }

    private void EnsureVisualCount(int count, RectTransform root) {
        while (visuals.Count < count) {
            visuals.Add(new PreviewVisual(root, visuals.Count));
        }
    }

    private void DestroyVisuals() {
        foreach (var visual in visuals) visual.Destroy();
        visuals.Clear();
    }

    private void PruneInvalidVisuals() {
        for (var index = visuals.Count - 1; index >= 0; --index) {
            if (!visuals[index].IsUsable) visuals.RemoveAt(index);
        }
    }

    private readonly struct PreviewEntry {
        public readonly ArtilleryTask Task;
        public readonly Color Color;
        public readonly float WidthMultiplier;

        public PreviewEntry(ArtilleryTask task, Color color, float widthMultiplier) {
            Task = task;
            Color = color;
            WidthMultiplier = widthMultiplier;
        }
    }

    private sealed class PreviewVisual {
        public readonly GameObject Root;
        private readonly LineRenderer ring;
        private readonly LineRenderer horizontal;
        private readonly LineRenderer vertical;
        private Vector3 lastPosition = new(float.NaN, float.NaN, float.NaN);
        private float lastRadius = float.NaN;
        private Color lastColor = new(float.NaN, float.NaN, float.NaN, float.NaN);
        private float lastWidthMultiplier = float.NaN;

        public PreviewVisual(RectTransform parent, int index) {
            Root = new GameObject($"FCS Impact Preview {index + 1}");
            Root.transform.SetParent(parent, false);
            ring = CreateLine(Root.transform, "Blast Radius", true, CircleSegments);
            horizontal = CreateLine(Root.transform, "Impact Horizontal", false, 2);
            vertical = CreateLine(Root.transform, "Impact Vertical", false, 2);
        }

        public void Show(
            Vector3 position,
            float radius,
            float localZ,
            Color color,
            float widthMultiplier) {
            try {
                Root.SetActive(true);
                var localPosition = new Vector3(position.x, position.y, localZ);
                if (localPosition != lastPosition) {
                    Root.transform.localPosition = localPosition;
                    lastPosition = localPosition;
                }

                radius = Mathf.Max(0f, radius);
                if (!Mathf.Approximately(radius, lastRadius)) {
                    UpdateGeometry(radius);
                    lastRadius = radius;
                }
                if (color != lastColor) {
                    SetLineColor(ring, color);
                    SetLineColor(horizontal, color);
                    SetLineColor(vertical, color);
                    lastColor = color;
                }
                widthMultiplier = Mathf.Clamp(widthMultiplier, 0.25f, 2f);
                if (!Mathf.Approximately(widthMultiplier, lastWidthMultiplier)) {
                    SetLineWidth(ring, widthMultiplier);
                    SetLineWidth(horizontal, widthMultiplier);
                    SetLineWidth(vertical, widthMultiplier);
                    lastWidthMultiplier = widthMultiplier;
                }
            }
            catch {
                // 场景可能在本帧中销毁测绘台；下帧由 PruneInvalidVisuals 移除旧引用。
            }
        }

        public bool IsUsable {
            get {
                try {
                    return Root != null && Root.transform != null;
                }
                catch {
                    return false;
                }
            }
        }

        public bool TrySetActive(bool active) {
            try {
                if (Root == null) return false;
                Root.SetActive(active);
                return true;
            }
            catch {
                return false;
            }
        }

        public void Destroy() {
            try { DestroyLineMaterial(ring); } catch { }
            try { DestroyLineMaterial(horizontal); } catch { }
            try { DestroyLineMaterial(vertical); } catch { }
            try {
                if (Root != null) Object.Destroy(Root);
            }
            catch {
                // Unity 已随父级销毁对象时无需再次处理。
            }
        }

        private void UpdateGeometry(float radius) {
            ring.enabled = radius > 0.0001f;
            if (ring.enabled) {
                for (var index = 0; index < CircleSegments; ++index) {
                    var angle = Mathf.PI * 2f * index / CircleSegments;
                    ring.SetPosition(index, new Vector3(
                        Mathf.Cos(angle) * radius,
                        Mathf.Sin(angle) * radius,
                        0f));
                }
            }

            var halfSize = Mathf.Clamp(
                radius * 0.22f,
                MinimumCrossHalfSizeMission,
                MaximumCrossHalfSizeMission);
            horizontal.SetPosition(0, new Vector3(-halfSize, 0f, 0f));
            horizontal.SetPosition(1, new Vector3(halfSize, 0f, 0f));
            vertical.SetPosition(0, new Vector3(0f, -halfSize, 0f));
            vertical.SetPosition(1, new Vector3(0f, halfSize, 0f));
        }

        private static LineRenderer CreateLine(
            Transform parent, string name, bool loop, int positionCount) {
            var child = new GameObject(name);
            child.transform.SetParent(parent, false);
            var line = child.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            // 圆环必须贴合测绘台本地 XY 平面。默认 View 模式会把线带朝向摄像机，
            // 在倾斜纸面上形成相交，表现为只有上半圆可见。
            line.alignment = LineAlignment.TransformZ;
            line.loop = loop;
            line.positionCount = positionCount;
            line.startWidth = LineWidthMission;
            line.endWidth = LineWidthMission;
            line.sortingOrder = 500;
            FcsSceneInteractor.SetColor(child, Color.white);
            DisableBackFaceCulling(line);
            return line;
        }

        private static void SetLineColor(LineRenderer line, Color color) {
            // 当前游戏的 URP Unlit 材质不读取 LineRenderer 顶点色，只显示材质色。
            // 顶点保持白色、材质承担着色，既不会双重相乘，也不会全部显示为白色。
            line.startColor = Color.white;
            line.endColor = Color.white;
            FcsSceneInteractor.SetColor(line.gameObject, color);
            var material = line.sharedMaterial;
            if (material != null && material.HasProperty("_EmissionColor")) {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color);
            }
            DisableBackFaceCulling(line);
        }

        private static void SetLineWidth(LineRenderer line, float multiplier) {
            line.startWidth = LineWidthMission * multiplier;
            line.endWidth = LineWidthMission * multiplier;
        }

        private static void DisableBackFaceCulling(LineRenderer line) {
            var material = line.sharedMaterial;
            if (material == null) return;
            // coordinateRoot 在不同关卡可能含镜像缩放；双面绘制避免圆环因绕序翻转而缺半边。
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
        }

        private static void DestroyLineMaterial(LineRenderer line) {
            try {
                if (line == null || line.sharedMaterial == null) return;
                Object.Destroy(line.sharedMaterial);
            }
            catch {
                // LineRenderer 可能已随场景销毁。
            }
        }
    }
}
