using System.Collections;
using MelonLoader;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 统一管理由当前逻辑实例启动的 Melon 协程。完成的协程会自动从集合移除；热重载时
/// StopAll 会停止仍在运行的协程，使迭代器 finally 得以释放炮塔/控制台互斥锁。
/// </summary>
public sealed class CoroutineTracker {
    private readonly HashSet<Registration> _registrations = new();

    public int Count => _registrations.Count;

    public object Start(IEnumerator routine) {
        ArgumentNullException.ThrowIfNull(routine);

        var registration = new Registration();
        _registrations.Add(registration);
        try {
            var handle = MelonCoroutines.Start(RunTracked(routine, registration));
            registration.Handle = handle;
            return handle;
        }
        catch {
            _registrations.Remove(registration);
            throw;
        }
    }

    public void StopAll() {
        var active = _registrations.ToArray();
        _registrations.Clear();
        foreach (var registration in active) {
            if (registration.Handle == null) continue;
            try {
                MelonCoroutines.Stop(registration.Handle);
            }
            catch (Exception ex) {
                MelonLogger.Error($"[FCS] Stop coroutine failed: {ex}");
            }
        }
    }

    private IEnumerator RunTracked(IEnumerator routine, Registration registration) {
        try {
            yield return routine;
        }
        finally {
            _registrations.Remove(registration);
        }
    }

    private sealed class Registration {
        public object? Handle;
    }
}
