using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class Turret {
    private const float RotationReadyToleranceDeg = 0.25f;
    private TurretController? _turret;


    public bool TryBind() {
        var turretObj = GameObject.Find("TurretSystem");
        if (turretObj == null) {
            MelonLogger.Error("[FCS] Aiming: Can't find TurretSystem");
            return false;
        }
        _turret = turretObj.GetComponent<TurretController>();
        return true;
    }
    
    public IEnumerator SetRotation(float angle) {
        if (_turret == null) {
            MelonLogger.Error("[FCS] Aiming: unbound TurretController");
            yield break;
        }

        CommandRotation(angle);
        yield return new WaitForSeconds(1f);
        while (_turret.rotationVelocity != 0) {
            yield return new WaitForSeconds(1f);
        }
    }

    /// <summary>更新共享炮塔目标方向但不等待，用于移动中连续跟踪。</summary>
    public void CommandRotation(float angle) {
        if (_turret != null) _turret.DesiredRotation = -angle;
    }

    public bool IsRotationReady(float angle, float toleranceDeg = RotationReadyToleranceDeg) {
        return _turret != null
               && Mathf.Abs(Mathf.DeltaAngle(_turret.CurrentAngle, -angle)) <= toleranceDeg;
    }
    
}
