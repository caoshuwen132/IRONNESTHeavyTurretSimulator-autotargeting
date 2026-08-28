using System.Collections;
using Il2Cpp;
using Il2CppTMPro;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

public class FcsSceneInteractor {
    private readonly FSC fcs;

    private readonly List<GameObject> destroyOnShutdown = new();
    private readonly ClickRaycaster clicks = new();
    private readonly CoroutineTracker coroutines = new();
    private readonly MapImpactPreview impactPreview;
    private readonly MapTargetLockEffect targetLockEffect;
    private TMP_FontAsset? chineseFontAsset;
    private bool chineseFontSearched;

    // 当前选中的弹种（两管炮共享，由调度器决定任务派到哪管炮）。
    public BulletType selectedBulletType = BulletType.HE;

    private readonly List<GameObject> bulletTypeBtns = new();
    private readonly Dictionary<int, GameObject> gameSpeedButtons = new();

    // 每个地图目标对应一个按钮：targetId -> 按钮。点击=用当前弹种为该目标入队一个任务。
    private readonly Dictionary<int, GameObject> targetButtons = new();

    public bool AutoFire = false;
    public bool AutoTarget = false;
    public bool DesktopOnly = false;
    public bool ImpactPreview = false;
    public bool DualGunFocus = false;
    public bool maxCharge = false;
    private int selectedGameSpeed = 1;
    private float timeScaleBeforeModChange = 1f;
    private bool gameSpeedChanged;

    public FcsSceneInteractor(FSC fcs) {
        this.fcs = fcs;
        impactPreview = new MapImpactPreview(fcs);
        targetLockEffect = new MapTargetLockEffect(fcs);
    }

    public void Initialize() {
        InitializeBulletTypeButtons();
        InitializeTargetButtons();
    }

    private void InitializeBulletTypeButtons() {
        const float z = -18.4181f;
        var x = 0.8f;
        var y = -0.65f;
        foreach (BulletType type in Enum.GetValues(typeof(BulletType))) {
            BulletType captured = type;
            // 先声明再赋值：lambda 要捕获 button，不能在其声明表达式内部引用它。
            GameObject button = null!;
            button = AddButton(() => {
                selectedBulletType = captured;
                foreach (var btn in bulletTypeBtns) {
                    SetColor(btn, btn == button ? Color.green : Color.white);
                }
            }, type == BulletType.HE ? Color.green : Color.white);
            button.transform.position = new Vector3(x, y, z);
            button.transform.localScale = Vector3.one * 0.02f;
            bulletTypeBtns.Add(button);
            var text = AddText(type.ToString(), 14f);
            text.transform.SetParent(button.transform, false);
            text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            text.transform.localScale = Vector3.one * 1.0f;
            x -= 0.05f;
            y -= 0.0045f;
        }
    }

    /// <summary>
    /// 4 个目标按钮（对应地图上 1~4 号炮兵标记）。点击即用当前选中弹种为该目标入队一个任务，
    /// 调度器自动派给空闲炮管。用 activeTargets 防止同一目标重复入队。
    /// </summary>
    private void InitializeTargetButtons() {
        const float z = -18.5881f;
        var x = 0.8f;
        var y = -0.65f;
        
        GameObject autoFireButton = null!;
        autoFireButton = AddButton(() => {
            AutoFire = !AutoFire;
            SetColor(autoFireButton, AutoFire ? Color.green : Color.white);
            MelonLogger.Msg($"[FCS] AutoFire {(AutoFire ? "enabled" : "disabled")}");
        }, AutoFire ? Color.green : Color.white);
        autoFireButton.transform.position = new Vector3(x, y, z);
        autoFireButton.transform.localScale = Vector3.one * 0.02f;
        var autoFiretext = AddText("自动开火", 14f);
        autoFiretext.transform.SetParent(autoFireButton.transform, false);
        autoFiretext.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        autoFiretext.transform.localScale = Vector3.one * 1.0f;
        
        x -= 0.05f;
        y -= 0.0045f;

        GameObject autoTargetButton = null!;
        autoTargetButton = AddButton(() => {
            AutoTarget = !AutoTarget;
            SetColor(autoTargetButton, AutoTarget ? Color.green : Color.white);
            MelonLogger.Msg($"[FCS] AutoTarget: {(AutoTarget ? "enabled" : "disabled")}");
        }, AutoTarget ? Color.green : Color.white);
        autoTargetButton.transform.position = new Vector3(x, y, z);
        autoTargetButton.transform.localScale = Vector3.one * 0.02f;
        var autoTargetText = AddText("自动索敌", 14f);
        autoTargetText.transform.SetParent(autoTargetButton.transform, false);
        autoTargetText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        autoTargetText.transform.localScale = Vector3.one;

        x -= 0.05f;
        y -= 0.0045f;

        GameObject desktopOnlyButton = null!;
        desktopOnlyButton = AddButton(() => {
            DesktopOnly = !DesktopOnly;
            SetColor(desktopOnlyButton, DesktopOnly ? Color.green : Color.white);
            MelonLogger.Msg(
                $"[FCS] Desktop-only targeting: {(DesktopOnly ? "enabled" : "disabled")}");
        }, DesktopOnly ? Color.green : Color.white);
        desktopOnlyButton.transform.position = new Vector3(x, y, z);
        desktopOnlyButton.transform.localScale = Vector3.one * 0.02f;
        var desktopOnlyText = AddText("只读桌面目标", 14f);
        desktopOnlyText.transform.SetParent(desktopOnlyButton.transform, false);
        desktopOnlyText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        desktopOnlyText.transform.localScale = Vector3.one;

        x -= 0.05f;
        y -= 0.0045f;
        
        GameObject maxChargeButton = null!;
        maxChargeButton = AddButton(() => {
            maxCharge = !maxCharge;
            SetColor(maxChargeButton, maxCharge ? Color.green : Color.white);
        }, maxCharge ? Color.green : Color.white);
        maxChargeButton.transform.position = new Vector3(x, y, z);
        maxChargeButton.transform.localScale = Vector3.one * 0.02f;
        var maxChargeText = AddText("最大装药", 14f);
        maxChargeText.transform.SetParent(maxChargeButton.transform, false);
        maxChargeText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        maxChargeText.transform.localScale = Vector3.one * 1.0f;
        
        x -= 0.05f;
        y -= 0.0045f;

        GameObject impactPreviewButton = null!;
        impactPreviewButton = AddButton(() => {
            ImpactPreview = !ImpactPreview;
            SetColor(impactPreviewButton, ImpactPreview ? Color.green : Color.white);
            if (!ImpactPreview) impactPreview.HideAll();
            MelonLogger.Msg(
                $"[FCS] Impact preview: {(ImpactPreview ? "enabled" : "disabled")}");
        }, ImpactPreview ? Color.green : Color.white);
        impactPreviewButton.transform.position = new Vector3(x, y, z);
        impactPreviewButton.transform.localScale = Vector3.one * 0.02f;
        var impactPreviewText = AddText("落点预览", 14f);
        impactPreviewText.transform.SetParent(impactPreviewButton.transform, false);
        impactPreviewText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        impactPreviewText.transform.localScale = Vector3.one;

        x -= 0.05f;
        y -= 0.0045f;

        GameObject dualGunFocusButton = null!;
        dualGunFocusButton = AddButton(() => {
            DualGunFocus = !DualGunFocus;
            SetColor(dualGunFocusButton, DualGunFocus ? Color.green : Color.white);
            MelonLogger.Msg(
                $"[FCS] Dual-gun focus: {(DualGunFocus ? "enabled" : "disabled")}");
        }, DualGunFocus ? Color.green : Color.white);
        dualGunFocusButton.transform.position = new Vector3(x, y, z);
        dualGunFocusButton.transform.localScale = Vector3.one * 0.02f;
        var dualGunFocusText = AddText("双炮合一", 14f);
        dualGunFocusText.transform.SetParent(dualGunFocusButton.transform, false);
        dualGunFocusText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        dualGunFocusText.transform.localScale = Vector3.one;

        x -= 0.05f;
        y -= 0.0045f;

        foreach (var multiplier in new[] { 1, 3, 10 }) {
            var capturedMultiplier = multiplier;
            GameObject speedButton = null!;
            speedButton = AddButton(
                () => SelectGameSpeed(capturedMultiplier),
                multiplier == selectedGameSpeed ? Color.green : Color.white);
            speedButton.transform.position = new Vector3(x, y, z);
            speedButton.transform.localScale = Vector3.one * 0.02f;
            gameSpeedButtons[multiplier] = speedButton;
            var speedText = AddText($"X{multiplier}", 14f);
            speedText.transform.SetParent(speedButton.transform, false);
            speedText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            speedText.transform.localScale = Vector3.one;
            x -= 0.05f;
            y -= 0.0045f;
        }
        
        ////////////////
        
        for (var i = 1; i <= 4; i++) {
            var targetId = i;
            var button = AddButton(() => ActivateTargetButton(targetId), Color.red);
            button.transform.position = new Vector3(x, y, z);
            button.transform.localScale = Vector3.one * 0.02f;
            targetButtons[targetId] = button;
            var text = AddText("T" + targetId, 14f);
            text.transform.SetParent(button.transform, false);
            text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            text.transform.localScale = Vector3.one * 1.0f;
            x -= 0.05f;
            y -= 0.0045f;
        }
    }

    /// <summary>任务完成回调</summary>
    public void TaskFinished(ArtilleryTask task) {
    }

    private void SelectGameSpeed(int multiplier) {
        if (multiplier is not (1 or 3 or 10)) return;
        if (!gameSpeedChanged) {
            timeScaleBeforeModChange = Time.timeScale > 0f ? Time.timeScale : 1f;
            gameSpeedChanged = true;
        }

        selectedGameSpeed = multiplier;
        Time.timeScale = multiplier;
        foreach (var pair in gameSpeedButtons) {
            SetColor(pair.Value, pair.Key == selectedGameSpeed ? Color.green : Color.white);
        }
        MelonLogger.Msg($"[FCS] Game speed: X{selectedGameSpeed}");
    }
    
    public void Update(bool allowClicks = true) {
        if (allowClicks) {
            clicks.Update();
            UpdateTargetHotkeys();
        }
        impactPreview.Update(ImpactPreview);
        targetLockEffect.Update(ImpactPreview);
    }

    private void UpdateTargetHotkeys() {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (keyboard.digit1Key.wasPressedThisFrame) ActivateTargetButton(1);
        else if (keyboard.digit2Key.wasPressedThisFrame) ActivateTargetButton(2);
        else if (keyboard.digit3Key.wasPressedThisFrame) ActivateTargetButton(3);
        else if (keyboard.digit4Key.wasPressedThisFrame) ActivateTargetButton(4);
    }

    private void ActivateTargetButton(int targetId) {
        if (!targetButtons.TryGetValue(targetId, out var button)
            || button == null) return;
        var collider = button.GetComponent<Collider>();
        if (collider == null || !collider.enabled) return;

        var task = fcs.MapTable.GetMarkTarget(targetId);
        if (task == null) return;
        task.targetId = targetId;
        task.bulletType = selectedBulletType;
        fcs.EnqueueTask(task);
        SetColor(button, Color.gray);
        collider.enabled = false;
        coroutines.Start(InvokeDelay(() => {
            if (button == null || collider == null) return;
            SetColor(button, Color.red);
            collider.enabled = true;
        }, 1f));
    }

    public void ShutDown() {
        if (gameSpeedChanged && Time.timeScale > 0f) {
            Time.timeScale = timeScaleBeforeModChange;
        }
        selectedGameSpeed = 1;
        timeScaleBeforeModChange = 1f;
        gameSpeedChanged = false;
        coroutines.StopAll();
        clicks.Clear();
        impactPreview.Shutdown();
        targetLockEffect.Shutdown();
        foreach (var obj in destroyOnShutdown) {
            Object.Destroy(obj);
        }
        destroyOnShutdown.Clear();
        bulletTypeBtns.Clear();
        gameSpeedButtons.Clear();
        targetButtons.Clear();
    }
    
    public GameObject AddButton(Action onClick) {
        return AddButton(onClick, Color.white);
    }

    public GameObject AddButton(Action onClick, Color color) {
        // 用自带 BoxCollider 的 cube 当可点击目标，靠 ClickRaycaster 自己 raycast 检测点击，
        // 不依赖游戏的 LookAtTarget，也不注册新 IL2CPP 类型（保持可热重载）。
        var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
        destroyOnShutdown.Add(button);
        var collider = button.GetComponent<Collider>();
        clicks.Register(collider, onClick);
        SetColor(button, color);
        return button;
    }

    /// <summary>
    /// 给对象的 Renderer 换上当前渲染管线（URP）的材质并设颜色。
    /// CreatePrimitive 默认用内置管线的 Standard 材质，在 URP 下 shader 无效会渲染成紫色；
    /// 这里用 URP 的 Unlit shader 重建材质（不受光照影响，纯色所见即所得）。
    /// </summary>
    public static void SetColor(GameObject go, Color color) {
        var renderer = go.GetComponent<Renderer>();
        if (renderer == null)
            return;

        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) {
            MelonLogger.Warning("[FCS] Can't find URP shader. Use default material color instead.");
            // 退而求其次：直接改现有材质颜色
            if (renderer.material != null)
                renderer.material.color = color;
            return;
        }

        var mat = renderer.material;
        if (mat == null || mat.shader != shader) {
            mat = new Material(shader);
            renderer.material = mat;
        }
        // URP Unlit 用 _BaseColor 控制颜色；同时设 color 兼容。
        mat.color = color;
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);
    }

    /// <summary>
    /// 在 3D 世界里创建一段文本（World Space 的 TextMeshPro，非 UGUI）。
    /// 返回 GameObject，调用方自行设 transform.position/scale。文本/字号后续可通过
    /// go.GetComponent&lt;TextMeshPro&gt;() 修改。中文优先复用游戏已经加载的中文字体资产。
    /// </summary>
    public GameObject AddText(string text, float fontSize = 4f) {
        var go = new GameObject("FcsText");
        destroyOnShutdown.Add(go);
        go.transform.Rotate(new Vector3(90, 0, 0));
        go.transform.Rotate(new Vector3(0, 0, -90));
        var tmp = go.AddComponent<TextMeshPro>();
        // AddComponent 后 Awake 未必已执行，字体可能未自动赋值导致不渲染。
        // 控制按钮含中文，复用游戏本身已加载且覆盖这些字符的 TMP 字体；
        // 英文、数字及找不到中文字体时继续沿用默认字体。
        var requestedFont = ContainsNonAscii(text) ? FindChineseFont(text) : null;
        if (requestedFont != null)
            tmp.font = requestedFont;
        else if (tmp.font == null && TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = Color.white;
        // 锚点设到左上角，方便从左上往下排版（Center 会以几何中心为原点）。
        // tmp.alignment = TextAlignmentOptions.MidlineLeft;
        return go;
    }

    private TMP_FontAsset? FindChineseFont(string requiredText) {
        if (chineseFontAsset != null) return chineseFontAsset;
        if (chineseFontSearched) return null;
        chineseFontSearched = true;

        try {
            foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>()) {
                if (font == null || !font.HasCharacters(requiredText)) continue;
                chineseFontAsset = font;
                MelonLogger.Msg($"[FCS] Chinese button font: {font.name}");
                return chineseFontAsset;
            }
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS] Chinese button font lookup failed: {ex.Message}");
        }

        MelonLogger.Warning("[FCS] No loaded TMP font covers the Chinese button labels");
        return null;
    }

    private static bool ContainsNonAscii(string text) {
        foreach (var character in text) {
            if (character > 127) return true;
        }
        return false;
    }
    
    public static IEnumerator WaitAndClick(LookAtTarget? button) {
        if (button == null) {
            MelonLogger.Error("[FCS] WaitAndClick: button is null");
            yield break;
        }
        // LookAtTarget stores its click deadline on Unity's unscaled realtime clock.
        // Comparing it with Time.time breaks after pauses and speed changes because the
        // two clocks diverge, leaving an already usable button waiting indefinitely.
        while (button.isActive == false
               || button.nextAllowedClickTime > Time.realtimeSinceStartup) {
            yield return new WaitForSeconds(0.1f);
        }
        yield return new WaitForSeconds(0.1f);
        button.OnClickDown();
        yield return new WaitForSeconds(0.1f);
        button.OnClickUp();
    }

    /// <summary>
    /// 采购卡在暂停/恢复期间可能退出卡槽，使购买按钮长期保持不可用。卡片退出使用
    /// Time.time 做有界等待，让暂停时间不计入超时；按钮冷却则必须与游戏原生
    /// LookAtTarget 一样使用 realtimeSinceStartup。若实时冷却仍异常超过上限，才清除
    /// 失效截止值，避免任务永久卡住。
    /// </summary>
    public static IEnumerator WaitAndClickWithTimeout(
        LookAtTarget? button,
        float timeoutSeconds,
        Action<bool> completed) {
        completed(false);
        if (button == null) {
            MelonLogger.Error("[FCS] WaitAndClickWithTimeout: button is null");
            yield break;
        }

        var inactiveDeadline = Time.time + Mathf.Max(0.1f, timeoutSeconds);
        var cooldownRecoveryDeadline =
            Time.realtimeSinceStartup + Mathf.Max(0.1f, timeoutSeconds);
        while (button.isActive == false
               || button.nextAllowedClickTime > Time.realtimeSinceStartup) {
            if (button.isActive) {
                if (Time.realtimeSinceStartup >= cooldownRecoveryDeadline) {
                    var staleClickTime = button.nextAllowedClickTime;
                    button.nextAllowedClickTime = 0f;
                    MelonLogger.Warning(
                        $"[FCS] WaitAndClickWithTimeout: cleared stale purchase-button " +
                        $"click deadline after {timeoutSeconds:F1} real seconds; " +
                        $"nextAllowed={staleClickTime:F3}, " +
                        $"realtime={Time.realtimeSinceStartup:F3}");
                    break;
                }
            }
            else if (Time.time >= inactiveDeadline) {
                MelonLogger.Warning(
                    $"[FCS] WaitAndClickWithTimeout: purchase card did not reactivate the " +
                    $"button within {timeoutSeconds:F1} game seconds; " +
                    $"nextAllowed={button.nextAllowedClickTime:F3}, " +
                    $"realtime={Time.realtimeSinceStartup:F3}");
                yield break;
            }
            yield return new WaitForSeconds(0.1f);
        }

        yield return new WaitForSeconds(0.1f);
        button.OnClickDown();
        yield return new WaitForSeconds(0.1f);
        button.OnClickUp();
        completed(true);
    }
    
    public static IEnumerator InvokeDelay(Action action, float delay) {
        yield return new WaitForSeconds(delay);
        action();
    }
    
}
