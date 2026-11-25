using System;
using System.Collections.Generic;
using System.IO;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Missiles;
using MDSFramework.Sensors;
using UnityEngine;

namespace MDSFramework.Deployment
{
    internal sealed class MdsWreckController : MonoBehaviour
    {
        private GlbKeyframedAnimator? animator;
        private readonly List<ParticleSystem> systems = new List<ParticleSystem>(4);
        private readonly List<float> baseRates = new List<float>(4);
        private float elapsed;
        private bool finishedBurn;

        private const float TotalSmolderSeconds = 45f;

        internal void Configure(GlbKeyframedAnimator? destroyAnimator, IList<ParticleSystem> spawnedParticles)
        {
            animator = destroyAnimator;
            systems.Clear();
            baseRates.Clear();
            if (spawnedParticles != null)
            {
                for (int i = 0; i < spawnedParticles.Count; i++)
                {
                    ParticleSystem? ps = spawnedParticles[i];
                    if (ps != null)
                    {
                        systems.Add(ps);
                        baseRates.Add(ps.emission.rateOverTime.constant);
                    }
                }
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f)
            {
                return;
            }

            if (animator != null && !animator.IsFullyDeployed)
            {
                animator.TickAnimation(dt, allowTurretOverrides: false);
            }

            if (finishedBurn)
            {
                return;
            }

            elapsed += dt;
            float fade = Mathf.Clamp01(1f - (elapsed / TotalSmolderSeconds));
            for (int i = 0; i < systems.Count; i++)
            {
                ParticleSystem? ps = systems[i];
                if (ps == null)
                {
                    continue;
                }

                ParticleSystem.EmissionModule emission = ps.emission;
                if (fade <= 0.005f)
                {
                    emission.enabled = false;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
                else
                {
                    emission.rateOverTime = baseRates[i] * fade;
                }
            }

            if (fade <= 0.005f)
            {
                finishedBurn = true;
            }
        }
    }

    internal sealed class MdsFlyingDebrisBehavior : MonoBehaviour
    {
        private Rigidbody? rb;
        private Collider? col;
        private float age;
        private bool frozen;

        internal void Initialize(Rigidbody body, Collider collider)
        {
            rb = body;
            col = collider;
        }

        private void Update()
        {
            age += Time.deltaTime;
            if (!frozen && rb != null && ((age >= 1.5f && rb.velocity.sqrMagnitude < 0.25f) || age >= 12f))
            {
                frozen = true;
                Destroy(rb);
                rb = null;
                if (col != null)
                {
                    Destroy(col);
                    col = null;
                }
            }

            if (age >= 90f)
            {
                Destroy(gameObject);
            }
        }
    }

    internal sealed class MdsClientDestructionWatcher : MonoBehaviour
    {
        private Unit? unit;
        private Building? subscribedBuilding;
        private ContractorPackage? package;
        private ModularUnitSpec? spec;
        private GameObject? activeWreckRoot;
        private bool wreckSpawned;

        internal void Configure(Unit targetUnit, ContractorPackage pkg, ModularUnitSpec unitSpec)
        {
            if (targetUnit == null || targetUnit.GetComponent<ModularSiteBehavior>() != null)
            {
                return;
            }

            unit = targetUnit;
            package = pkg;
            spec = unitSpec;
            MdsDestructionManager.SuppressVanillaWreckage(targetUnit);

            targetUnit.onDisableUnit -= HandleDisabled;
            targetUnit.onDisableUnit += HandleDisabled;

            if (targetUnit is Building b)
            {
                subscribedBuilding = b;
                b.OnRepair -= HandleRepaired;
                b.OnRepair += HandleRepaired;
            }
        }

        private void HandleDisabled(Unit _)
        {
            if (wreckSpawned || unit == null || package == null || spec == null || MdsRuntimeController.IsResettingScene)
            {
                return;
            }

            if (unit.GetComponent<ModularSiteBehavior>() != null)
            {
                return;
            }

            wreckSpawned = true;
            Transform? intactRig = unit.transform.Find($"MDS_Rig_{spec.UnitId}");
            if (intactRig != null)
            {
                intactRig.gameObject.SetActive(false);
            }

            Vector3 spawnPos = unit is Building ? unit.startPosition.ToLocalPosition() : unit.transform.position;
            activeWreckRoot = MdsDestructionManager.SpawnDestructionSequence(
                unit,
                package,
                spec,
                spawnPos,
                unit.transform.rotation);
        }

        private void HandleRepaired()
        {
            wreckSpawned = false;
            MdsDestructionManager.CleanupWreck(activeWreckRoot);
            activeWreckRoot = null;

            if (unit != null && spec != null)
            {
                Transform? intactRig = unit.transform.Find($"MDS_Rig_{spec.UnitId}");
                if (intactRig != null)
                {
                    intactRig.gameObject.SetActive(true);
                }

                ModularSiteManager.ReapplyHiddenVanillaVisuals(unit);
            }
        }

        private void OnDestroy()
        {
            if (unit != null)
            {
                unit.onDisableUnit -= HandleDisabled;
            }

            if (subscribedBuilding != null)
            {
                subscribedBuilding.OnRepair -= HandleRepaired;
                subscribedBuilding = null;
            }
        }
    }

    internal static class MdsDestructionManager
    {
        private const int MaxActiveWrecks = 36;

        private static readonly List<GameObject> ActiveWreckRoots = new List<GameObject>(MaxActiveWrecks + 2);
        private static Texture2D? smokeSoftTexture;
        private static Material? smokeMaterial;
        private static Material? emberMaterial;

        internal static void SuppressVanillaWreckage(Unit? unit)
        {
            if (unit is Building building && GameInteropHelpers.BuildingWreckageField != null)
            {
                GameInteropHelpers.BuildingWreckageField.SetValue(building, null);
            }
        }

        internal static GameObject? SpawnDestructionSequence(
            Unit unit,
            ContractorPackage package,
            ModularUnitSpec spec,
            Vector3 worldPos,
            Quaternion worldRot)
        {
            if (unit == null || package == null || spec == null || MdsRuntimeController.IsResettingScene)
            {
                return null;
            }

            if (!MdsConfig.EnableCustomGlbVisuals.Value || string.IsNullOrWhiteSpace(spec.DestroyedGlbRelativePath))
            {
                return null;
            }

            string? wreckGlbPath = CustomMissileBehavior.ResolveAssetPath(
                package,
                spec.DestroyedGlbRelativePath,
                Array.Empty<string>());
            if (string.IsNullOrEmpty(wreckGlbPath) || !File.Exists(wreckGlbPath))
            {
                return null;
            }

            while (ActiveWreckRoots.Count >= MaxActiveWrecks)
            {
                GameObject oldest = ActiveWreckRoots[0];
                ActiveWreckRoots.RemoveAt(0);
                if (oldest != null)
                {
                    UnityEngine.Object.Destroy(oldest);
                }
            }

            Vector3 groundedPos = SnapToGround(worldPos);
            Transform parentOrigin = Datum.origin != null ? Datum.origin : unit.transform.parent;

            var wreckRoot = new GameObject($"MDS_Wreck_{spec.UnitId}");
            if (parentOrigin != null)
            {
                wreckRoot.transform.SetParent(parentOrigin, false);
            }

            wreckRoot.transform.position = groundedPos;
            wreckRoot.transform.rotation = worldRot;

            GlbInstantiatedRig? wreckRig = GlbRuntimeLoader.Instantiate(
                $"{spec.UnitId}_wreck",
                wreckGlbPath,
                "container",
                null,
                wreckRoot.transform,
                spec.ModelYawOffsetDeg);

            GlbKeyframedAnimator? wreckAnimator = null;
            var fireParticles = new List<ParticleSystem>(4);

            if (wreckRig != null && wreckRig.RootObject != null)
            {
                if (Mathf.Abs(wreckRig.Bounds.min.y) > 0.01f)
                {
                    wreckRig.RootObject.transform.localPosition = new Vector3(0f, -wreckRig.Bounds.min.y, 0f);
                }

                if (wreckRig.DestroyClip != null || wreckRig.DeployClip != null)
                {
                    wreckAnimator = wreckRig.RootObject.GetComponent<GlbKeyframedAnimator>()
                        ?? wreckRig.RootObject.AddComponent<GlbKeyframedAnimator>();
                    wreckAnimator.InitializeOneShot(
                        wreckRig,
                        wreckRig.DestroyClip,
                        Mathf.Max(0.4f, spec.DestroyAnimationDurationSeconds));
                }

                if (wreckRig.FireOrigins.Count > 0)
                {
                    for (int i = 0; i < wreckRig.FireOrigins.Count; i++)
                    {
                        Transform origin = wreckRig.FireOrigins[i];
                        if (origin != null)
                        {
                            AddSmolderParticles(origin, fireParticles);
                        }
                    }
                }
                else
                {
                    AddSmolderParticles(wreckRoot.transform, fireParticles, new Vector3(0f, 0.85f, 0f));
                }
            }

            SpawnNativeDestructionFx(groundedPos, worldRot, wreckRoot.transform);
            SpawnPhysicsDebris(package, spec, groundedPos);

            MdsWreckController controller = wreckRoot.AddComponent<MdsWreckController>();
            controller.Configure(wreckAnimator, fireParticles);
            ActiveWreckRoots.Add(wreckRoot);
            return wreckRoot;
        }

        internal static void CleanupWreck(GameObject? wreckRoot)
        {
            if (wreckRoot == null)
            {
                return;
            }

            ActiveWreckRoots.Remove(wreckRoot);
            UnityEngine.Object.Destroy(wreckRoot);
        }

        internal static void ResetSession()
        {
            for (int i = 0; i < ActiveWreckRoots.Count; i++)
            {
                if (ActiveWreckRoots[i] != null)
                {
                    UnityEngine.Object.Destroy(ActiveWreckRoots[i]);
                }
            }

            ActiveWreckRoots.Clear();
        }

        internal static void Dispose()
        {
            ResetSession();
            if (smokeMaterial != null)
            {
                UnityEngine.Object.Destroy(smokeMaterial);
                smokeMaterial = null;
            }

            if (emberMaterial != null)
            {
                UnityEngine.Object.Destroy(emberMaterial);
                emberMaterial = null;
            }

            if (smokeSoftTexture != null)
            {
                UnityEngine.Object.Destroy(smokeSoftTexture);
                smokeSoftTexture = null;
            }
        }

        private static Vector3 SnapToGround(Vector3 worldPos)
        {
            if (Physics.Raycast(
                    worldPos + (Vector3.up * 4.0f),
                    Vector3.down,
                    out RaycastHit hit,
                    12.0f,
                    (int)PhysicsLayers.StaticsMask,
                    QueryTriggerInteraction.Ignore))
            {
                return hit.point;
            }

            return worldPos;
        }

        private static void SpawnNativeDestructionFx(Vector3 groundedPos, Quaternion worldRot, Transform wreckRootTf)
        {
            GameAssets? assets = GameAssets.i;
            if (assets == null)
            {
                return;
            }

            if (assets.vehicleWreckDestroyed != null)
            {
                Transform? origin = Datum.origin != null ? Datum.origin : wreckRootTf.parent;
                GameObject explosionFx = origin != null
                    ? UnityEngine.Object.Instantiate(assets.vehicleWreckDestroyed, groundedPos, worldRot, origin)
                    : UnityEngine.Object.Instantiate(assets.vehicleWreckDestroyed, groundedPos, worldRot);
                UnityEngine.Object.Destroy(explosionFx, 18f);
            }

            if (assets.scorchMarkDecal != null)
            {
                GameObject scorch = UnityEngine.Object.Instantiate(
                    assets.scorchMarkDecal,
                    groundedPos + (Vector3.up * 0.03f),
                    Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f),
                    wreckRootTf);
                scorch.transform.localScale = new Vector3(4.2f, 1.0f, 6.8f);
            }
        }

        private static void SpawnPhysicsDebris(ContractorPackage package, ModularUnitSpec spec, Vector3 groundedPos)
        {
            int count = Mathf.Clamp(spec.DestructionDebrisCount, 0, 12);
            if (count <= 0 || string.IsNullOrWhiteSpace(spec.DebrisKitGlbRelativePath))
            {
                return;
            }

            string? kitPath = CustomMissileBehavior.ResolveAssetPath(
                package,
                spec.DebrisKitGlbRelativePath,
                Array.Empty<string>());
            if (string.IsNullOrEmpty(kitPath) || !File.Exists(kitPath))
            {
                return;
            }

            GameObject staging = new GameObject("MDS_DebrisStaging");
            staging.SetActive(false);
            GlbInstantiatedRig? kitRig = GlbRuntimeLoader.Instantiate(
                "skyshield_debris_kit",
                kitPath,
                "debris",
                null,
                staging.transform,
                0f);

            try
            {
                if (kitRig == null || kitRig.DebrisChunks.Count == 0)
                {
                    return;
                }

                PhysicMaterial? physMat = GameAssets.i != null ? GameAssets.i.debrisMaterial : null;
                Transform? parentOrigin = Datum.origin;

                for (int i = 0; i < count; i++)
                {
                    Transform srcChunk = kitRig.DebrisChunks[i % kitRig.DebrisChunks.Count];
                    if (srcChunk == null)
                    {
                        continue;
                    }

                    GameObject debrisGo = UnityEngine.Object.Instantiate(srcChunk.gameObject);
                    debrisGo.name = $"MDS_Debris_{spec.UnitId}_{i + 1}";
                    if (parentOrigin != null)
                    {
                        debrisGo.transform.SetParent(parentOrigin, false);
                    }

                    Vector2 radial2 = UnityEngine.Random.insideUnitCircle.normalized;
                    if (radial2.sqrMagnitude < 0.01f)
                    {
                        radial2 = Vector2.right;
                    }

                    Vector3 spawnOffset = new Vector3(
                        radial2.x * UnityEngine.Random.Range(0.4f, 1.2f),
                        UnityEngine.Random.Range(0.8f, 1.8f),
                        radial2.y * UnityEngine.Random.Range(0.4f, 1.6f));
                    debrisGo.transform.position = groundedPos + spawnOffset;
                    debrisGo.transform.rotation = UnityEngine.Random.rotation;

                    MeshFilter? mf = debrisGo.GetComponentInChildren<MeshFilter>(true);
                    Vector3 boxSize = mf != null && mf.sharedMesh != null
                        ? Vector3.Max(mf.sharedMesh.bounds.size, new Vector3(0.20f, 0.14f, 0.20f))
                        : new Vector3(0.55f, 0.18f, 0.55f);

                    BoxCollider col = debrisGo.AddComponent<BoxCollider>();
                    col.size = boxSize;
                    col.center = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds.center : Vector3.zero;
                    if (physMat != null)
                    {
                        col.sharedMaterial = physMat;
                    }

                    Rigidbody rb = debrisGo.AddComponent<Rigidbody>();
                    rb.mass = UnityEngine.Random.Range(45f, 140f);
                    rb.drag = 0.35f;
                    rb.angularDrag = 0.45f;
                    rb.interpolation = RigidbodyInterpolation.Interpolate;

                    Vector3 launchDir = new Vector3(
                        radial2.x * UnityEngine.Random.Range(0.55f, 1.0f),
                        UnityEngine.Random.Range(0.75f, 1.35f),
                        radial2.y * UnityEngine.Random.Range(0.55f, 1.0f)).normalized;
                    float speed = UnityEngine.Random.Range(8f, 22f);
                    rb.AddForce(launchDir * speed, ForceMode.VelocityChange);
                    rb.AddTorque(UnityEngine.Random.insideUnitSphere * UnityEngine.Random.Range(2.5f, 7.5f), ForceMode.VelocityChange);

                    MdsFlyingDebrisBehavior behavior = debrisGo.AddComponent<MdsFlyingDebrisBehavior>();
                    behavior.Initialize(rb, col);
                    DebrisManager.RegisterDebris(debrisGo);
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(staging);
            }
        }

        private static void AddSmolderParticles(Transform parent, List<ParticleSystem> outList, Vector3 localOffset = default)
        {
            EnsureParticleAssets();

            var smokeGo = new GameObject("MDS_WreckSmoke");
            smokeGo.transform.SetParent(parent, false);
            smokeGo.transform.localPosition = localOffset;
            ParticleSystem smokePs = smokeGo.AddComponent<ParticleSystem>();
            ParticleSystemRenderer smokeRend = smokeGo.GetComponent<ParticleSystemRenderer>();
            if (smokeMaterial != null)
            {
                smokeRend.sharedMaterial = smokeMaterial;
            }

            ParticleSystem.MainModule sm = smokePs.main;
            sm.loop = true;
            sm.startLifetime = new ParticleSystem.MinMaxCurve(2.8f, 4.6f);
            sm.startSpeed = new ParticleSystem.MinMaxCurve(1.4f, 3.0f);
            sm.startSize = new ParticleSystem.MinMaxCurve(0.85f, 2.1f);
            sm.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.08f, 0.08f, 0.08f, 0.72f),
                new Color(0.16f, 0.14f, 0.13f, 0.48f));
            sm.gravityModifier = -0.14f;
            sm.simulationSpace = ParticleSystemSimulationSpace.World;
            sm.maxParticles = 96;

            ParticleSystem.EmissionModule se = smokePs.emission;
            se.rateOverTime = 14f;

            ParticleSystem.ShapeModule ss = smokePs.shape;
            ss.shapeType = ParticleSystemShapeType.Cone;
            ss.angle = 22f;
            ss.radius = 0.35f;

            smokePs.Play();
            outList.Add(smokePs);

            var sparkGo = new GameObject("MDS_WreckSparks");
            sparkGo.transform.SetParent(parent, false);
            sparkGo.transform.localPosition = localOffset;
            ParticleSystem sparkPs = sparkGo.AddComponent<ParticleSystem>();
            ParticleSystemRenderer sparkRend = sparkGo.GetComponent<ParticleSystemRenderer>();
            if (emberMaterial != null)
            {
                sparkRend.sharedMaterial = emberMaterial;
            }

            ParticleSystem.MainModule fm = sparkPs.main;
            fm.loop = true;
            fm.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 1.15f);
            fm.startSpeed = new ParticleSystem.MinMaxCurve(2.2f, 5.2f);
            fm.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.28f);
            fm.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1.0f, 0.48f, 0.08f, 0.95f),
                new Color(1.0f, 0.22f, 0.03f, 0.85f));
            fm.gravityModifier = 0.45f;
            fm.simulationSpace = ParticleSystemSimulationSpace.World;
            fm.maxParticles = 48;

            ParticleSystem.EmissionModule fe = sparkPs.emission;
            fe.rateOverTime = 12f;

            ParticleSystem.ShapeModule fs = sparkPs.shape;
            fs.shapeType = ParticleSystemShapeType.Cone;
            fs.angle = 36f;
            fs.radius = 0.25f;

            sparkPs.Play();
            outList.Add(sparkPs);
        }

        private static void EnsureParticleAssets()
        {
            if (smokeSoftTexture == null)
            {
                const int size = 32;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                var pixels = new Color32[size * size];
                float inv = 1f / ((size - 1) * 0.5f);
                for (int y = 0; y < size; y++)
                {
                    float dy = (y * inv) - 1f;
                    for (int x = 0; x < size; x++)
                    {
                        float dx = (x * inv) - 1f;
                        float r2 = (dx * dx) + (dy * dy);
                        float alpha = r2 >= 1f ? 0f : Mathf.Pow(1f - r2, 2.2f);
                        byte a = (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255);
                        pixels[(y * size) + x] = new Color32(255, 255, 255, a);
                    }
                }

                tex.SetPixels32(pixels);
                tex.Apply(false, true);
                smokeSoftTexture = tex;
            }

            if (smokeMaterial == null)
            {
                smokeMaterial = CreateParticleMaterial(Color.white, additive: false);
            }

            if (emberMaterial == null)
            {
                emberMaterial = CreateParticleMaterial(new Color(1.2f, 0.65f, 0.18f, 1f), additive: true);
            }
        }

        private static Material CreateParticleMaterial(Color tint, bool additive)
        {
            Shader? shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                ?? Shader.Find("Particles/Standard Unlit")
                ?? Shader.Find("Sprites/Default");
            var mat = shader != null ? new Material(shader) : UrpGlbMaterialBuilder.CreateBeamMaterial(tint);
            if (smokeSoftTexture != null)
            {
                mat.mainTexture = smokeSoftTexture;
                if (mat.HasProperty("_BaseMap"))
                {
                    mat.SetTexture("_BaseMap", smokeSoftTexture);
                }
            }

            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", tint);
            }

            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
            }

            if (additive && mat.HasProperty("_Blend"))
            {
                mat.SetFloat("_Blend", 2f);
            }

            return mat;
        }
    }
}
