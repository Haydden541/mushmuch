using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class HyphaeGrower : MonoBehaviour
{
    [Header("起始引导 (每个物体建议使用独立的OriginPoint)")]
    public Transform originPoint;
    public LayerMask surfaceLayer;
    public int initialRootCount = 25;

    [Header("全包裹控制 (5秒左右缓慢生长)")]
    public float baseMaxLife = 12.0f;
    [Range(0, 5f)] public float lifeRandomness = 3.5f;
    public float growthInterval = 0.05f;
    public int maxTotalPoints = 60000;

    [Header("生长纪律 (解决中部过密与包裹不全)")]
    [Tooltip("每长一段距离才允许分叉，防止基部堆积")]
    public float minSplitInterval = 0.5f;
    [Tooltip("不育率：让更多线只顾着爬行包裹，不浪费点数分叉")]
    [Range(0, 1f)] public float loneWolfChance = 0.4f;

    [Header("表面紧贴 (解决 Collider 误差与中心射线)")]
    public float surfaceOffset = 0.005f;
    public float probeDepth = 0.7f;

    [Header("防聚拢转向 (寻找空旷区)")]
    public float avoidanceRadius = 0.12f;
    [Range(0, 2f)] public float steerSensitivity = 0.6f;
    [Range(0, 2f)] public float upwardForce = 0.45f;
    [Range(0, 5f)] public float turbulence = 1.5f;

    [Header("形态质感")]
    public Material hyphaeMaterial;
    public float rootThickness = 0.012f;
    [Range(0, 1f)] public float splitChance = 0.12f;

    [HideInInspector] public int currentTotalPoints = 0;
    private MeshCollider targetCollider;
    private float sizeScale;

    void Start()
    {
        targetCollider = GetComponent<MeshCollider>();
        if (targetCollider == null || originPoint == null) return;

        // 计算物体大致尺寸
        sizeScale = (targetCollider.bounds.size.x + targetCollider.bounds.size.y + targetCollider.bounds.size.z) / 3f;

        // --- 独立种子探测逻辑：确保只在自己身上生长 ---
        int seeded = 0;
        for (int i = 0; i < initialRootCount * 3 && seeded < initialRootCount; i++)
        {
            Vector3 rayOrigin = originPoint.position + Random.insideUnitSphere * (sizeScale * 0.1f);
            Vector3 dirToMyCenter = (targetCollider.bounds.center - rayOrigin).normalized;

            if (Physics.Raycast(rayOrigin, dirToMyCenter, out RaycastHit hit, sizeScale * 3f, surfaceLayer))
            {
                // 关键点：检查碰撞到的必须是挂载本脚本的 Collider
                if (hit.collider == targetCollider)
                {
                    SpawnBranch(hit.point + hit.normal * surfaceOffset, hit.normal, rootThickness, Vector3.up, 1.0f, false);
                    seeded++;
                }
            }
        }
    }

    public void SpawnBranch(Vector3 pos, Vector3 normal, float width, Vector3 growDir, float lifeMultiplier, bool forceLone)
    {
        if (currentTotalPoints >= maxTotalPoints || width < 0.0005f) return;

        GameObject go = new GameObject("Hyphae_Strand");
        go.transform.SetParent(this.transform);
        go.layer = 2; // 设置为 Ignore Raycast 层，方便互相排斥检测
        var branch = go.AddComponent<HyphaeBranch>();

        float individualLife = baseMaxLife * sizeScale * lifeMultiplier * Random.Range(0.4f, 1f + lifeRandomness);
        float individualSpeed = 0.03f * sizeScale * Random.Range(0.8f, 1.2f);
        bool lone = forceLone || (Random.value < loneWolfChance);

        branch.Init(pos, normal, width, growDir, this, individualLife, individualSpeed, lone);
    }
}

public class HyphaeBranch : MonoBehaviour
{
    private LineRenderer lr;
    private List<Vector3> points = new List<Vector3>();
    private Vector3 currentPos, currentNormal, currentDir;
    private float myWidth, myMaxLen, mySpeed, currentLength = 0f;
    private float lastSplitLength = 0f;
    private HyphaeGrower master;
    private bool isStopped = false;
    private bool isLoneWolf = false;

    public void Init(Vector3 pos, Vector3 normal, float width, Vector3 dir, HyphaeGrower grower, float mLen, float mSpeed, bool lone)
    {
        master = grower; currentPos = pos; currentNormal = normal;
        myWidth = width; currentDir = dir; myMaxLen = mLen; mySpeed = mSpeed;
        isLoneWolf = lone;

        lr = gameObject.AddComponent<LineRenderer>();
        lr.material = master.hyphaeMaterial;
        lr.numCornerVertices = 4;
        lr.startWidth = myWidth;
        lr.endWidth = myWidth;
        lr.positionCount = 1;
        lr.SetPosition(0, pos);
        points.Add(pos);

        // 添加触发器用于排斥检测
        var sc = gameObject.AddComponent<SphereCollider>();
        sc.radius = master.avoidanceRadius * 0.4f;
        sc.isTrigger = true;

        StartCoroutine(GrowStepRoutine());
    }

    IEnumerator GrowStepRoutine()
    {
        while (!isStopped && currentLength < myMaxLen && master.currentTotalPoints < master.maxTotalPoints)
        {
            yield return new WaitForSeconds(master.growthInterval);

            // 1. 防聚拢与环境感知
            Vector3 avoidanceVector = Vector3.zero;
            Collider[] neighbors = Physics.OverlapSphere(currentPos, master.avoidanceRadius, 1 << 2);
            foreach (var n in neighbors)
            {
                if (n.gameObject != gameObject) avoidanceVector += (currentPos - n.transform.position);
            }

            Vector3 noise = new Vector3(Random.value - 0.5f, Random.value - 0.5f, Random.value - 0.5f) * master.turbulence;
            // 混合方向：原方向 + 向上倾向 + 排斥力 + 随机噪音
            Vector3 targetDir = (currentDir * 0.5f + Vector3.up * master.upwardForce + avoidanceVector.normalized * master.steerSensitivity + noise).normalized;
            currentDir = Vector3.Slerp(currentDir, targetDir, 0.4f);

            Vector3 tangentDir = Vector3.ProjectOnPlane(currentDir, currentNormal).normalized;
            Vector3 nextPosBase = currentPos + tangentDir * mySpeed;

            // 2. 强效表面吸附 (向心探测)
            Vector3 center = master.GetComponent<MeshCollider>().bounds.center;
            Vector3 rayDir = (center - nextPosBase).normalized;
            Vector3 rayOrigin = nextPosBase - rayDir * master.probeDepth;

            if (Physics.Raycast(rayOrigin, rayDir, out RaycastHit hit, master.probeDepth * 2.5f, master.surfaceLayer))
            {
                currentPos = hit.point + hit.normal * master.surfaceOffset;
                currentNormal = hit.normal;
                currentLength += mySpeed;
                points.Add(currentPos);
                lr.positionCount = points.Count;
                lr.SetPosition(points.Count - 1, currentPos);
                master.currentTotalPoints++;

                GetComponent<SphereCollider>().center = transform.InverseTransformPoint(currentPos);

                // 3. 动态变细
                float progress = currentLength / myMaxLen;
                lr.startWidth = myWidth * (1.0f - progress * 0.85f);
                lr.endWidth = lr.startWidth * 0.4f;

                // 4. 克制分层逻辑：长一段距离且不拥挤才分叉
                if (!isLoneWolf && (currentLength - lastSplitLength) > master.minSplitInterval && neighbors.Length < 5)
                {
                    if (Random.value < master.splitChance)
                    {
                        lastSplitLength = currentLength;
                        Vector3 splitDir = Vector3.Cross(tangentDir, currentNormal) * (Random.value > 0.5f ? 1 : -1);
                        master.SpawnBranch(currentPos, currentNormal, myWidth * 0.8f, (tangentDir + splitDir).normalized, (1.0f - progress), false);
                    }
                }
            }
            else { isStopped = true; }
        }
    }
}