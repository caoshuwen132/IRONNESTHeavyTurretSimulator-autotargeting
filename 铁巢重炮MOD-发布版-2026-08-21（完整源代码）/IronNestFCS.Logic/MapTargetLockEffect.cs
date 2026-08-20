using IronNestFCS.Logic.FCS;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

/// <summary>
/// 在测绘台上为已进入任务队列的目标显示旋转准星。
/// 该类只读取任务与目标坐标，不参与索敌、瞄准、选弹或击发。
/// </summary>
public sealed class MapTargetLockEffect {
    private const float RefreshInterval = 0.1f;
    private const int MaximumLockEffects = 6;
    private const float MinimumReticleOuterRadius = 0.16f;
    private const float MaximumReticleOuterRadius = 1.2f;
    private const float ReticleCornerLength = 0.09f;
    private const float LineWidth = 0.025f;
    private const float RotationSpeedDegrees = 55f;

    // 地图后处理会压暗普通颜色，使用 HDR 黄色保持清晰可见。
    private static readonly Color LockColor = new(3f, 2.35f, 0.05f, 1f);

    private readonly FSC fcs;
    private readonly List<LockVisual> visuals = new();
    private readonly List<Vector3> positions = new();
    private readonly List<float> impactRadii = new();
    private readonly HashSet<string> targetKeys = new();
    private RectTransform? effectRoot;
    private float nextRefreshAt;

    public MapTargetLockEffect(FSC fcs) {
        this.fcs = fcs;
    }

    public void Update(bool enabled) {
        if (!enabled
            || !fcs.IsBound
            || !fcs.MapTable.TryGetPreviewSurface(out var root, out var localZ)) {
            HideAll();
            return;
        }

        if (effectRoot != root) {
            DestroyVisuals();
            effectRoot = root;
            nextRefreshAt = 0f;
        }

        PruneInvalidVisuals();
        var now = Time.realtimeSinceStartup;
        if (now >= nextRefreshAt) {
            nextRefreshAt = now + RefreshInterval;
            RefreshTargets();
            EnsureVisualCount(positions.Count, root);
            for (var index = 0; index < visuals.Count; ++index) {
                if (index >= positions.Count) {
                    visuals[index].TrySetActive(false);
                    continue;
                }

                var depthDirection = Mathf.Sign(localZ == 0f ? 1f : localZ);
                visuals[index].Show(
                    positions[index],
                    impactRadii[index],
                    localZ + depthDirection * (0.012f + index * 0.001f));
            }
        }

        for (var index = visuals.Count - 1; index >= 0; --index) {
            if (!visuals[index].TryAnimate(now)) visuals.RemoveAt(index);
        }
    }

    public void Shutdown() {
        DestroyVisuals();
        positions.Clear();
        impactRadii.Clear();
        targetKeys.Clear();
        effectRoot = null;
        nextRefreshAt = 0f;
    }

    private void RefreshTargets() {
        positions.Clear();
        impactRadii.Clear();
        targetKeys.Clear();
        AddTask(fcs.LeftTask);
        AddTask(fcs.RightTask);
        var queued = fcs.QueueCan;
        while (queued.Count > 0 && positions.Count < MaximumLockEffects) {
            AddTask(queued.Dequeue());
        }
    }

    private void AddTask(ArtilleryTask? task) {
        if (task == null
            || positions.Count >= MaximumLockEffects
            || task.progress is Progress.Finished or Progress.Failed
            || !fcs.MapTable.TryGetTargetDisplayPosition(task, out var position)) return;

        var key = string.IsNullOrWhiteSpace(task.sourceEntityId)
            ? $"P:{Mathf.RoundToInt(position.x * 100f)}:{Mathf.RoundToInt(position.y * 100f)}"
            : $"E:{task.sourceEntityId}";
        if (!targetKeys.Add(key)) return;
        positions.Add(position);
        impactRadii.Add(fcs.GetImpactPreviewRadiusMission(task));
    }

    private void EnsureVisualCount(int count, RectTransform root) {
        while (visuals.Count < count) {
            visuals.Add(new LockVisual(root, visuals.Count));
        }
    }

    private void HideAll() {
        foreach (var visual in visuals) visual.TrySetActive(false);
        PruneInvalidVisuals();
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

    private sealed class LockVisual {
        public readonly GameObject Root;
        private readonly Transform rotatingReticle;
        private readonly LineRenderer[] corners = new LineRenderer[4];
        private float lastImpactRadius = float.NaN;

        public LockVisual(RectTransform parent, int index) {
            Root = new GameObject($"FCS Target Lock {index + 1}");
            Root.transform.SetParent(parent, false);

            var reticleObject = new GameObject("Rotating Reticle");
            reticleObject.transform.SetParent(Root.transform, false);
            rotatingReticle = reticleObject.transform;
            for (var corner = 0; corner < corners.Length; ++corner) {
                corners[corner] = CreateLine(
                    rotatingReticle,
                    $"Corner {corner + 1}",
                    false,
                    3);
                SetCornerGeometry(corners[corner], corner, 0f);
            }
        }

        public void Show(Vector3 position, float impactRadius, float localZ) {
            try {
                Root.SetActive(true);
                Root.transform.localPosition = new Vector3(position.x, position.y, localZ);
                impactRadius = Mathf.Max(0f, impactRadius);
                if (!Mathf.Approximately(impactRadius, lastImpactRadius)) {
                    for (var corner = 0; corner < corners.Length; ++corner) {
                        SetCornerGeometry(corners[corner], corner, impactRadius);
                    }
                    lastImpactRadius = impactRadius;
                }
            }
            catch {
                // 场景可能在本帧中销毁测绘台；下帧会清除旧引用。
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

        public bool TryAnimate(float now) {
            try {
                if (Root == null) return false;
                if (!Root.activeSelf) return true;
                rotatingReticle.localEulerAngles = new Vector3(
                    0f,
                    0f,
                    now * RotationSpeedDegrees);
                return true;
            }
            catch {
                return false;
            }
        }

        public void Destroy() {
            foreach (var corner in corners) {
                try { DestroyLineMaterial(corner); } catch { }
            }
            try {
                if (Root != null) Object.Destroy(Root);
            }
            catch {
                // Unity 已随父级销毁对象时无需再次处理。
            }
        }

        private static void SetCornerGeometry(
            LineRenderer line, int corner, float impactRadius) {
            // 四个 L 形角保持原有长度，只按杀伤半径改变两两间距。
            var outerRadius = Mathf.Clamp(
                impactRadius,
                MinimumReticleOuterRadius,
                MaximumReticleOuterRadius);
            var innerRadius = outerRadius - ReticleCornerLength;
            var x = corner is 0 or 3 ? 1f : -1f;
            var y = corner is 0 or 1 ? 1f : -1f;
            line.SetPosition(0, new Vector3(x * innerRadius, y * outerRadius, 0f));
            line.SetPosition(1, new Vector3(x * outerRadius, y * outerRadius, 0f));
            line.SetPosition(2, new Vector3(x * outerRadius, y * innerRadius, 0f));
        }

        private static LineRenderer CreateLine(
            Transform parent,
            string name,
            bool loop,
            int positionCount) {
            var child = new GameObject(name);
            child.transform.SetParent(parent, false);
            var line = child.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.alignment = LineAlignment.TransformZ;
            line.loop = loop;
            line.positionCount = positionCount;
            line.startWidth = LineWidth;
            line.endWidth = LineWidth;
            line.numCornerVertices = 2;
            line.numCapVertices = 2;
            line.sortingOrder = 520;
            FcsSceneInteractor.SetColor(child, LockColor);
            line.startColor = Color.white;
            line.endColor = Color.white;
            var material = line.sharedMaterial;
            if (material != null) {
                if (material.HasProperty("_EmissionColor")) {
                    material.EnableKeyword("_EMISSION");
                    material.SetColor("_EmissionColor", LockColor);
                }
                if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
                if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            }
            return line;
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
