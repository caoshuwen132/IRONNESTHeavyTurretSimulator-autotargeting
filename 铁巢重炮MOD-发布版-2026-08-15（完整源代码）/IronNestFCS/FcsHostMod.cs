using System.Collections;
using IronNestFCS;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(FcsHostMod), "IronNestFCS", "1.0.6", "svr2kos2")]
// 游戏 1577 更新移除了产品名中的冒号。MelonGameAttribute 允许重复声明，
// 同时保留旧名称可兼容更新前的 Demo/正式版以及更新后的游戏。
[assembly: MelonGame("Iron Nest", "Iron Nest: Heavy Turret Simulator")]
[assembly: MelonGame("Iron Nest", "Iron Nest Heavy Turret Simulator")]

namespace IronNestFCS;

/// <summary>
/// 稳定的宿主 Mod。启动时加载一次，永不重载。
/// 职责：首次加载 Logic、监听 F9 触发热重载、把生命周期回调转发给 Logic。
/// 所有高频改动的火控代码都在 Logic 程序集里。
/// </summary>
public class FcsHostMod : MelonMod
{
    // 游戏启用了新 Input System，旧的 UnityEngine.Input 会直接抛异常，
    // 因此通过 Keyboard.current 读取 F9。
    private const string ReloadKeyName = "F9";

    // Logic 程序集放在 UserData 下、而非 Mods/，避免被 MelonLoader 当作 mod 自动加载。
    // 类型全名必须与 Logic 项目里的实现类一致。
    private const string LogicTypeName = "IronNestFCS.Logic.FcsModule";

    private LogicReloader? reloader;
    private object? pendingSceneReload;
    private int sceneReloadRequestId;

    public override void OnInitializeMelon()
    {
        string logicDir = Path.Combine(MelonEnvironment.UserDataDirectory, "IronNestFCS");
        Directory.CreateDirectory(logicDir);
        string logicDll = Path.Combine(logicDir, "IronNestFCS.Logic.dll");

        MelonLogger.Msg($"IronNestFCS Host Started。Logic path: {logicDll}");
        MelonLogger.Msg($"Press {ReloadKeyName} to hot reload Logic.");

        reloader = new LogicReloader(logicDll, LogicTypeName);
        reloader.Reload();
    }

    /// <summary>用新 Input System 读 F9，避免触碰会抛异常的 UnityEngine.Input。</summary>
    private static bool ReloadKeyPressed()
    {
        Keyboard? kb = Keyboard.current;
        return kb != null && kb.f9Key.wasPressedThisFrame;
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        CancelPendingSceneReload();
        var requestId = sceneReloadRequestId;
        pendingSceneReload = MelonCoroutines.Start(ReloadCoroutine(requestId));
    }
    
    private IEnumerator ReloadCoroutine(int requestId)
    {
        try
        {
            yield return new WaitForSeconds(3f);
            if (requestId == sceneReloadRequestId)
                reloader?.Reload();
        }
        finally
        {
            if (requestId == sceneReloadRequestId)
                pendingSceneReload = null;
        }
    }

    private void CancelPendingSceneReload()
    {
        ++sceneReloadRequestId;
        var handle = pendingSceneReload;
        pendingSceneReload = null;
        if (handle == null)
            return;

        try { MelonCoroutines.Stop(handle); }
        catch (Exception ex) { MelonLogger.Warning($"Cancel delayed scene reload failed: {ex.Message}"); }
    }

    public override void OnUpdate()
    {
        if (reloader == null)
            return;

        if (ReloadKeyPressed() || reloader.CheckDllUpdated())
        {
            // 手动或文件更新触发的重载已经满足当前需求，取消尚未执行的场景延时重载，
            // 避免几秒后再次卸载/加载同一个 Logic。
            CancelPendingSceneReload();
            MelonLogger.Msg($"[{ReloadKeyName}] Hot reloading...");
            reloader.Reload();
            return; // 本帧不再 Update，避免对刚换上的实例做半截调用
        }

        try { reloader.Current?.Update(); }
        catch (Exception ex) { MelonLogger.Error($"Logic.Update() exception: {ex}"); }
    }

    public override void OnGUI()
    {
        if (reloader?.Current == null)
            return;

        try { reloader.Current.OnGui(); }
        catch (Exception ex) { MelonLogger.Error($"Logic.OnGui() exception: {ex}"); }
    }

    public override void OnDeinitializeMelon()
    {
        CancelPendingSceneReload();
        reloader?.Unload();
        reloader = null;
    }
}
