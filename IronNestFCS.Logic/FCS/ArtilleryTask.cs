using UnityEngine;

using Il2Cpp;

namespace IronNestFCS.Logic.FCS;

public enum Progress {
    Pending,
    Calculating,
    SelectingBullet,
    LoadingBullet,
    LoadingPowder,
    WaitLoading,
    Aiming,
    WaitingForFire,
    BackToIdle,
    Finished,
    Failed,
}

public class ArtilleryTask {
    // 任务实际派发到炮管时分配的全局顺序号；共享炮塔据此只为下一发候选任务预转向。
    public long scheduleOrder;
    // 只有真正启动了预转向流程的任务才拥有“保持当前炮塔方向”的资格。
    // 遗留弹处理、装药或机构恢复中的任务尚未申请预转向，不应阻塞另一炮管。
    public bool preRotationRequested;
    public int targetId;
    public string targetName = "";
    public string sourceEntityId = "";
    public bool isAutoTarget;
    public bool isHidden;
    public bool isMoving;
    public bool isUnderground;
    public bool requiresAp;
    public bool isLocomotive;
    public bool isCommander;
    public bool isArtillery;
    public bool isAntiAir;
    public bool isSupply;
    public bool isMechanized;
    public bool isRecon;
    public bool isInfantry;
    public bool isShip;
    // 范围弹规划：主目标落点预计覆盖的敌军数量，以及随本发一起暂时锁定的其它目标。
    public int areaTargetCount = 1;
    public float impactRadiusKm;
    public List<string> areaCoveredTargetIds = new();
    public bool usesAreaAimPoint;
    // 优化落点相对主目标的位置；移动编组会在每次实时解算时重新平移/优化。
    public Vector3 areaAimOffsetFromPrimary;
    // 没有任何可达敌军时，为释放已装填炮管而生成的地图内安全空放任务。
    public bool isSafeDischarge;
    // 任务曾使用移动中火炮的预测炮位；即使取得发射权时移动已经结束，也要做最终复算。
    public bool usesMovingPlatformSolution;
    public float predictedPlatformLeadSeconds;
    public Vector3 predictedFiringOrigin;
    public string sourceIcon = "";
    public string sourceIconSprite = "";
    public string sourceStatusSprites = "";
    public string sourceImmuneShells = "";
    public int sourceRewardPoints = -1;
    public string sourceRewardSource = "";
    public int sourceHealth;
    public int sourceMaxHealth;
    public int sourceArmour;
    public int sourceStars;
    public EntityRoles sourceRole;
    public MapEntityStates sourceState;
    public Vector3 sourceVelocity;
    public int motionSamples;
    public float predictedLeadSeconds;
    public float angel;
    public float distance;
    public Vector3 position;
    public BulletType bulletType;
    public Progress progress;
}
