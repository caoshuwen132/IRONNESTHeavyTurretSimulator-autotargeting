using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace IronNestFCS.Logic;

/// <summary>
/// Hot-reload-safe inspection camera. It uses the new Input System directly
/// and does not register a custom IL2CPP MonoBehaviour.
/// </summary>
public sealed class FreeCameraController
{
    private const float NormalSpeed = 25f;
    private const float FastMultiplier = 4f;
    private const float SlowMultiplier = 0.25f;
    private const float MouseSensitivity = 0.08f;
    private const float ColliderRefreshInterval = 0.5f;
    private const float ColliderRescanInterval = 5f;
    private const int MaxDisplayedColliders = 6000;
    private static readonly int[] BoxEdges =
    {
        0, 1, 1, 3, 3, 2, 2, 0,
        4, 5, 5, 7, 7, 6, 6, 4,
        0, 4, 1, 5, 2, 6, 3, 7
    };

    private Camera? _sourceCamera;
    private Camera? _freeCamera;
    private GameObject? _freeCameraObject;
    private GameObject? _colliderOverlayObject;
    private Mesh? _colliderMesh;
    private Material? _solidColliderMaterial;
    private Material? _triggerColliderMaterial;
    private readonly List<Collider> _visibleColliders = new();
    private readonly List<PlayerInput> _suspendedInputs = new();
    private CursorLockMode _previousCursorLock;
    private bool _previousCursorVisible;
    private float _yaw;
    private float _pitch;
    private float _nextColliderRefresh;
    private float _nextColliderRescan;

    public bool IsActive => _freeCamera != null;
    public bool ColliderOverlayActive => _colliderOverlayObject != null;

    public void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard.f10Key.wasPressedThisFrame)
        {
            if (IsActive) Exit();
            else Enter();
        }

        if (keyboard.f11Key.wasPressedThisFrame)
        {
            if (ColliderOverlayActive) DisableColliderOverlay();
            else EnableColliderOverlay();
        }

        if (ColliderOverlayActive && Time.unscaledTime >= _nextColliderRefresh)
        {
            if (Time.unscaledTime >= _nextColliderRescan)
            {
                CollectVisibleColliders();
                _nextColliderRescan = Time.unscaledTime + ColliderRescanInterval;
            }
            RefreshColliderOverlay();
            _nextColliderRefresh = Time.unscaledTime + ColliderRefreshInterval;
        }

        if (!IsActive)
            return;

        if (keyboard.escapeKey.wasPressedThisFrame)
        {
            Exit();
            return;
        }

        if (_freeCamera == null || !_freeCamera)
        {
            Exit();
            return;
        }

        UpdateLook();
        UpdateMovement(keyboard);
    }

    private void Enter()
    {
        try
        {
            _sourceCamera = FindSourceCamera();
            if (_sourceCamera == null)
            {
                MelonLogger.Warning("[FCS] FreeCamera: no active game camera found");
                return;
            }

            _previousCursorLock = Cursor.lockState;
            _previousCursorVisible = Cursor.visible;

            var sourceTransform = _sourceCamera.transform;
            var angles = sourceTransform.eulerAngles;
            _yaw = angles.y;
            _pitch = NormalizePitch(angles.x);

            _freeCameraObject = new GameObject("IronNestFCS Free Camera");
            UnityEngine.Object.DontDestroyOnLoad(_freeCameraObject);
            _freeCamera = _freeCameraObject.AddComponent<Camera>();
            _freeCamera.CopyFrom(_sourceCamera);
            _freeCamera.transform.SetPositionAndRotation(sourceTransform.position, sourceTransform.rotation);
            _freeCamera.depth = _sourceCamera.depth + 100f;
            _freeCamera.farClipPlane = Math.Max(_sourceCamera.farClipPlane, 100000f);
            _freeCamera.enabled = true;
            _sourceCamera.enabled = false;

            SuspendPlayerInput();
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            MelonLogger.Msg("[FCS] FreeCamera ON (F10/Esc exit, WASD move, Space/C vertical, Shift fast)");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[FCS] FreeCamera enter failed: {ex}");
            Exit();
        }
    }

    public void Exit()
    {
        bool wasActive = IsActive || _freeCameraObject != null;
        try
        {
            if (_sourceCamera != null && _sourceCamera)
                _sourceCamera.enabled = true;
            if (_freeCameraObject != null && _freeCameraObject)
                UnityEngine.Object.Destroy(_freeCameraObject);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FCS] FreeCamera cleanup warning: {ex.Message}");
        }
        finally
        {
            _freeCamera = null;
            _freeCameraObject = null;
            _sourceCamera = null;
            RestorePlayerInput();
            if (wasActive)
            {
                Cursor.lockState = _previousCursorLock;
                Cursor.visible = _previousCursorVisible;
                MelonLogger.Msg("[FCS] FreeCamera OFF");
            }
        }
    }

    public void Shutdown()
    {
        Exit();
        DisableColliderOverlay();
    }

    private void EnableColliderOverlay()
    {
        try
        {
            CreateColliderOverlay();
            MelonLogger.Msg($"[FCS] Collider overlay ON (F11, boxes={_visibleColliders.Count})");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[FCS] Collider overlay failed: {ex}");
            DestroyColliderOverlay();
        }
    }

    private void DisableColliderOverlay()
    {
        if (!ColliderOverlayActive)
            return;
        DestroyColliderOverlay();
        MelonLogger.Msg("[FCS] Collider overlay OFF");
    }

    private void CreateColliderOverlay()
    {
        DestroyColliderOverlay();

        _colliderOverlayObject = new GameObject("IronNestFCS Collider Overlay");
        UnityEngine.Object.DontDestroyOnLoad(_colliderOverlayObject);
        var meshFilter = _colliderOverlayObject.AddComponent<MeshFilter>();
        var meshRenderer = _colliderOverlayObject.AddComponent<MeshRenderer>();

        _colliderMesh = new Mesh
        {
            name = "IronNestFCS Collider Wireframes",
            indexFormat = IndexFormat.UInt32
        };
        _colliderMesh.MarkDynamic();
        meshFilter.sharedMesh = _colliderMesh;

        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Color")
                     ?? Shader.Find("Sprites/Default");
        if (shader == null)
            throw new InvalidOperationException("No unlit shader is available for collider wireframes.");

        _solidColliderMaterial = CreateOverlayMaterial(shader, new Color(0.1f, 1f, 0.75f, 0.95f));
        _triggerColliderMaterial = CreateOverlayMaterial(shader, new Color(1f, 0.75f, 0.05f, 0.95f));
        meshRenderer.sharedMaterials = new[] { _solidColliderMaterial, _triggerColliderMaterial };
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        CollectVisibleColliders();
        RefreshColliderOverlay();
        _nextColliderRefresh = Time.unscaledTime + ColliderRefreshInterval;
        _nextColliderRescan = Time.unscaledTime + ColliderRescanInterval;
    }

    private static Material CreateOverlayMaterial(Shader shader, Color color)
    {
        var material = new Material(shader)
        {
            color = color,
            renderQueue = 5000
        };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
        if (material.HasProperty("_ZTest")) material.SetInt("_ZTest", 8); // Always
        if (material.HasProperty("_Cull")) material.SetInt("_Cull", 0);
        return material;
    }

    private void CollectVisibleColliders()
    {
        _visibleColliders.Clear();
        var camera = _freeCamera ?? FindSourceCamera();
        var origin = camera != null ? camera.transform.position : Vector3.zero;
        var candidates = new List<(Collider Collider, float Distance)>(4096);

        foreach (var collider in UnityEngine.Object.FindObjectsOfType<Collider>())
        {
            if (!IsDisplayable(collider))
                continue;
            var distance = (collider.bounds.center - origin).sqrMagnitude;
            candidates.Add((collider, distance));
        }

        candidates.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        int count = Math.Min(candidates.Count, MaxDisplayedColliders);
        for (int i = 0; i < count; i++)
            _visibleColliders.Add(candidates[i].Collider);

        if (candidates.Count > MaxDisplayedColliders)
        {
            MelonLogger.Warning(
                $"[FCS] Collider overlay limited to nearest {MaxDisplayedColliders} of {candidates.Count}");
        }
    }

    private static bool IsDisplayable(Collider? collider)
    {
        if (collider == null || !collider || !collider.enabled || !collider.gameObject.activeInHierarchy)
            return false;
        // Ignore invalid/empty bounds and the character-scale zero boxes sometimes used as markers.
        var size = collider.bounds.size;
        return size.sqrMagnitude > 0.000001f;
    }

    private void RefreshColliderOverlay()
    {
        if (_colliderMesh == null || !_colliderMesh)
            return;

        var vertices = new List<Vector3>(_visibleColliders.Count * 8);
        var solidIndices = new List<int>(_visibleColliders.Count * BoxEdges.Length);
        var triggerIndices = new List<int>(_visibleColliders.Count * 4);
        int removed = 0;

        foreach (var collider in _visibleColliders)
        {
            if (!IsDisplayable(collider))
            {
                removed++;
                continue;
            }

            int firstVertex = vertices.Count;
            AddColliderCorners(collider, vertices);
            var indices = collider.isTrigger ? triggerIndices : solidIndices;
            foreach (int edgeIndex in BoxEdges)
                indices.Add(firstVertex + edgeIndex);
        }

        _colliderMesh.Clear();
        _colliderMesh.vertices = vertices.ToArray();
        _colliderMesh.subMeshCount = 2;
        _colliderMesh.SetIndices(solidIndices.ToArray(), MeshTopology.Lines, 0, false);
        _colliderMesh.SetIndices(triggerIndices.ToArray(), MeshTopology.Lines, 1, false);
        _colliderMesh.RecalculateBounds();

        // Refresh the source list if many objects have disappeared or new mission objects may have spawned.
        if (removed > 32)
            CollectVisibleColliders();
    }

    private static void AddColliderCorners(Collider collider, List<Vector3> vertices)
    {
        if (collider is BoxCollider box)
        {
            var half = box.size * 0.5f;
            var center = box.center;
            var transform = box.transform;
            vertices.Add(transform.TransformPoint(center + new Vector3(-half.x, -half.y, -half.z)));
            vertices.Add(transform.TransformPoint(center + new Vector3( half.x, -half.y, -half.z)));
            vertices.Add(transform.TransformPoint(center + new Vector3(-half.x,  half.y, -half.z)));
            vertices.Add(transform.TransformPoint(center + new Vector3( half.x,  half.y, -half.z)));
            vertices.Add(transform.TransformPoint(center + new Vector3(-half.x, -half.y,  half.z)));
            vertices.Add(transform.TransformPoint(center + new Vector3( half.x, -half.y,  half.z)));
            vertices.Add(transform.TransformPoint(center + new Vector3(-half.x,  half.y,  half.z)));
            vertices.Add(transform.TransformPoint(center + new Vector3( half.x,  half.y,  half.z)));
            return;
        }

        // Sphere, capsule, mesh, terrain and character colliders use their exact
        // world-space bounds. This keeps the overlay cheap and readable.
        var bounds = collider.bounds;
        var min = bounds.min;
        var max = bounds.max;
        vertices.Add(new Vector3(min.x, min.y, min.z));
        vertices.Add(new Vector3(max.x, min.y, min.z));
        vertices.Add(new Vector3(min.x, max.y, min.z));
        vertices.Add(new Vector3(max.x, max.y, min.z));
        vertices.Add(new Vector3(min.x, min.y, max.z));
        vertices.Add(new Vector3(max.x, min.y, max.z));
        vertices.Add(new Vector3(min.x, max.y, max.z));
        vertices.Add(new Vector3(max.x, max.y, max.z));
    }

    private void DestroyColliderOverlay()
    {
        _visibleColliders.Clear();
        if (_colliderOverlayObject != null && _colliderOverlayObject)
            UnityEngine.Object.Destroy(_colliderOverlayObject);
        if (_colliderMesh != null && _colliderMesh)
            UnityEngine.Object.Destroy(_colliderMesh);
        if (_solidColliderMaterial != null && _solidColliderMaterial)
            UnityEngine.Object.Destroy(_solidColliderMaterial);
        if (_triggerColliderMaterial != null && _triggerColliderMaterial)
            UnityEngine.Object.Destroy(_triggerColliderMaterial);
        _colliderOverlayObject = null;
        _colliderMesh = null;
        _solidColliderMaterial = null;
        _triggerColliderMaterial = null;
    }

    private static Camera? FindSourceCamera()
    {
        var main = Camera.main;
        if (main != null && main && main.enabled && main.gameObject.activeInHierarchy)
            return main;

        Camera? best = null;
        foreach (var camera in Camera.allCameras)
        {
            if (camera == null || !camera || !camera.enabled || !camera.gameObject.activeInHierarchy)
                continue;
            if (best == null || camera.depth > best.depth)
                best = camera;
        }
        return best;
    }

    private void SuspendPlayerInput()
    {
        _suspendedInputs.Clear();
        try
        {
            foreach (var playerInput in UnityEngine.Object.FindObjectsOfType<PlayerInput>())
            {
                if (playerInput == null || !playerInput || !playerInput.enabled || !playerInput.inputIsActive)
                    continue;
                playerInput.DeactivateInput();
                _suspendedInputs.Add(playerInput);
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FCS] FreeCamera could not suspend PlayerInput: {ex.Message}");
        }
    }

    private void RestorePlayerInput()
    {
        foreach (var playerInput in _suspendedInputs)
        {
            try
            {
                if (playerInput != null && playerInput && playerInput.enabled)
                    playerInput.ActivateInput();
            }
            catch
            {
                // Scene may have changed while inspection mode was active.
            }
        }
        _suspendedInputs.Clear();
    }

    private void UpdateLook()
    {
        var mouse = Mouse.current;
        if (mouse == null || _freeCamera == null)
            return;

        var delta = mouse.delta.ReadValue();
        _yaw += delta.x * MouseSensitivity;
        _pitch = Mathf.Clamp(_pitch - delta.y * MouseSensitivity, -89f, 89f);
        _freeCamera.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }

    private void UpdateMovement(Keyboard keyboard)
    {
        if (_freeCamera == null)
            return;

        var local = Vector3.zero;
        if (keyboard.wKey.isPressed) local += Vector3.forward;
        if (keyboard.sKey.isPressed) local += Vector3.back;
        if (keyboard.dKey.isPressed) local += Vector3.right;
        if (keyboard.aKey.isPressed) local += Vector3.left;

        var transform = _freeCamera.transform;
        var movement = transform.right * local.x + transform.forward * local.z;
        if (keyboard.spaceKey.isPressed) movement += Vector3.up;
        if (keyboard.cKey.isPressed) movement += Vector3.down;
        if (movement.sqrMagnitude <= 0f)
            return;

        var speed = NormalSpeed;
        if (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed)
            speed *= FastMultiplier;
        if (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed)
            speed *= SlowMultiplier;
        transform.position += movement.normalized * speed * Time.unscaledDeltaTime;
    }

    private static float NormalizePitch(float pitch)
    {
        if (pitch > 180f) pitch -= 360f;
        return Mathf.Clamp(pitch, -89f, 89f);
    }
}
