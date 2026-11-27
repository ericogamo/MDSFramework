using System;
using System.Collections.Generic;
using System.IO;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Missiles;
using MDSFramework.Sensors;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

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
                if (animator.IsFullyDeployed && animator.NormalizedProgress < 1f)
                {
                    animator.ForceProgress(1f);
                }
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
            MdsDestructionManager.SuppressVanillaWreckage(targetUnit, pkg, unitSpec);

            targetUnit.onDisableUnit -= HandleDisabled;
            targetUnit.onDisableUnit += HandleDisabled;

            if (targetUnit is Building b)
            {
                subscribedBuilding = b;
                b.OnRepair -= HandleRepaired;
                b.OnRepair += HandleRepaired;
            }

            if (targetUnit.disabled)
            {
                HandleDisabledInternal(spawnExplosionFx: false);
            }
        }

        private void HandleDisabled(Unit _)
        {
            HandleDisabledInternal(spawnExplosionFx: true);
        }

        private void HandleDisabledInternal(bool spawnExplosionFx)
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

            MdsDestructionManager.DisableDestroyedUnitCollidersAndRenderers(unit, spec);

            Vector3 spawnPos = unit is Building ? unit.startPosition.ToLocalPosition() : unit.transform.position;
            activeWreckRoot = MdsDestructionManager.SpawnDestructionSequence(
                unit,
                package,
                spec,
                spawnPos,
                unit.transform.rotation,
                spawnExplosionFx);
        }

        internal void HandleRepaired()
        {
            wreckSpawned = false;
            MdsDestructionManager.CleanupWreck(activeWreckRoot);
            activeWreckRoot = null;

            if (unit != null && spec != null)
            {
                Transform? intactRig = unit.transform.Find($"MDS_Rig_{spec.UnitId}");
                Collider[] cols = unit.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < cols.Length; i++)
                {
                    Collider? col = cols[i];
                    if (col != null && (intactRig == null || !col.transform.IsChildOf(intactRig)))
                    {
                        col.enabled = true;
                    }
                }

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

        internal static bool HasCustomWreckVisual(ContractorPackage? package, ModularUnitSpec? spec)
        {
            if (!MdsConfig.EnableCustomGlbVisuals.Value ||
                package == null ||
                spec == null ||
                string.IsNullOrWhiteSpace(spec.DestroyedGlbRelativePath))
            {
                return false;
            }

            string? wreckGlbPath = CustomMissileBehavior.ResolveAssetPath(
                package,
                spec.DestroyedGlbRelativePath,
                Array.Empty<string>());
            return !string.IsNullOrEmpty(wreckGlbPath) && File.Exists(wreckGlbPath);
        }

        internal static void SuppressVanillaWreckage(Unit? unit, ContractorPackage? package = null, ModularUnitSpec? spec = null)
        {
            if (unit == null)
            {
                return;
            }

            if (package != null && spec != null && !HasCustomWreckVisual(package, spec))
            {
                return;
            }

            if (unit is Building building && GameInteropHelpers.BuildingWreckageField != null)
            {
                GameInteropHelpers.BuildingWreckageField.SetValue(building, null);
            }
            else if (unit is GroundVehicle gv && GameInteropHelpers.GroundVehicleWreckageField != null)
            {
                GameInteropHelpers.GroundVehicleWreckageField.SetValue(gv, null);
            }
        }

        internal static void DisableDestroyedUnitCollidersAndRenderers(Unit? unit, ModularUnitSpec? spec)
        {
            if (unit == null)
            {
                return;
            }

            Transform? rigTf = spec != null ? unit.transform.Find($"MDS_Rig_{spec.UnitId}") : null;

            Collider[] cols = unit.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                Collider? col = cols[i];
                if (col != null && (rigTf == null || !col.transform.IsChildOf(rigTf)))
                {
                    col.enabled = false;
                }
            }

            Renderer[] renderers = unit.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer? r = renderers[i];
                if (r != null &&
                    !(r is ParticleSystemRenderer) &&
                    (rigTf == null || !r.transform.IsChildOf(rigTf)))
                {
                    r.enabled = false;
                }
            }

            Physics.SyncTransforms();
        }

        internal static GameObject? SpawnDestructionSequence(
            Unit unit,
            ContractorPackage package,
            ModularUnitSpec spec,
            Vector3 worldPos,
            Quaternion worldRot,
            bool spawnExplosionFx = true)
        {
            if (unit == null || package == null || spec == null || MdsRuntimeController.IsResettingScene)
            {
                return null;
            }

            DisableDestroyedUnitCollidersAndRenderers(unit, spec);

            string? wreckGlbPath = null;
            if (MdsConfig.EnableCustomGlbVisuals.Value && !string.IsNullOrWhiteSpace(spec.DestroyedGlbRelativePath))
            {
                string? resolved = CustomMissileBehavior.ResolveAssetPath(
                    package,
                    spec.DestroyedGlbRelativePath,
                    Array.Empty<string>());
                if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                {
                    wreckGlbPath = resolved;
                }
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

            Vector3 groundedPos = SnapToGround(worldPos, unit, out Vector3 groundNormal);
            Transform parentOrigin = Datum.origin != null ? Datum.origin : unit.transform.parent;

            var wreckRoot = new GameObject($"MDS_Wreck_{spec.UnitId}");
            if (parentOrigin != null)
            {
                wreckRoot.transform.SetParent(parentOrigin, false);
            }

            wreckRoot.transform.position = groundedPos;
            wreckRoot.transform.rotation = worldRot;

            GlbKeyframedAnimator? wreckAnimator = null;
            var fireParticles = new List<ParticleSystem>(4);

            if (!string.IsNullOrEmpty(wreckGlbPath))
            {
                GlbInstantiatedRig? wreckRig = GlbRuntimeLoader.Instantiate(
                    $"{spec.UnitId}_wreck",
                    wreckGlbPath,
                    "container",
                    null,
                    wreckRoot.transform,
                    spec.ModelYawOffsetDeg);

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
            }
            else if (HasNoVanillaWreckage(unit))
            {
                AddSmolderParticles(wreckRoot.transform, fireParticles, new Vector3(0f, 0.85f, 0f));
            }

            SpawnNativeDestructionFx(groundedPos, groundNormal, worldRot, wreckRoot.transform, spec.IsMobileVehicle, spawnExplosionFx);
            SpawnPhysicsDebris(package, spec, groundedPos);

            MdsWreckController controller = wreckRoot.AddComponent<MdsWreckController>();
            controller.Configure(wreckAnimator, fireParticles);
            ActiveWreckRoots.Add(wreckRoot);
            return wreckRoot;
        }

        private static bool HasNoVanillaWreckage(Unit unit)
        {
            if (unit is Building b)
            {
                return GameInteropHelpers.BuildingWreckageField?.GetValue(b) == null;
            }

            if (unit is GroundVehicle gv)
            {
                return GameInteropHelpers.GroundVehicleWreckageField?.GetValue(gv) == null;
            }

            return true;
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

        private static Vector3 SnapToGround(Vector3 worldPos, Unit? ignoreUnit, out Vector3 groundNormal)
        {
            RaycastHit[] hits = Physics.RaycastAll(
                worldPos + (Vector3.up * 4.0f),
                Vector3.down,
                12.0f,
                (int)PhysicsLayers.StaticsMask,
                QueryTriggerInteraction.Ignore);

            bool found = false;
            float bestDist = float.MaxValue;
            RaycastHit bestHit = default;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider? col = hits[i].collider;
                if (col == null)
                {
                    continue;
                }

                if (ignoreUnit != null &&
                    (col.transform.IsChildOf(ignoreUnit.transform) ||
                     ReferenceEquals(col.GetComponentInParent<Unit>(), ignoreUnit)))
                {
                    continue;
                }

                if (hits[i].distance < bestDist)
                {
                    bestDist = hits[i].distance;
                    bestHit = hits[i];
                    found = true;
                }
            }

            if (found)
            {
                groundNormal = bestHit.normal.sqrMagnitude > 0.001f ? bestHit.normal.normalized : Vector3.up;
                return bestHit.point;
            }

            groundNormal = Vector3.up;
            return worldPos;
        }

        private static void SpawnNativeDestructionFx(
            Vector3 groundedPos,
            Vector3 groundNormal,
            Quaternion worldRot,
            Transform wreckRootTf,
            bool isMobileVehicle,
            bool spawnExplosionFx)
        {
            GameAssets? assets = GameAssets.i;
            if (assets == null)
            {
                return;
            }

            if (spawnExplosionFx && assets.vehicleWreckDestroyed != null)
            {
                Transform? origin = Datum.origin != null ? Datum.origin : wreckRootTf.parent;
                GameObject explosionFx = origin != null
                    ? UnityEngine.Object.Instantiate(assets.vehicleWreckDestroyed, groundedPos, worldRot, origin)
                    : UnityEngine.Object.Instantiate(assets.vehicleWreckDestroyed, groundedPos, worldRot);
                UnityEngine.Object.Destroy(explosionFx, 18f);
            }

            if (assets.scorchMarkDecal != null)
            {
                GameObject scorch = UnityEngine.Object.Instantiate(assets.scorchMarkDecal, wreckRootTf);
                scorch.transform.position = groundedPos;
                scorch.transform.localScale = Vector3.one;

                DecalProjector? projector = scorch.GetComponent<DecalProjector>();
                if (projector != null)
                {
                    float decalSize = isMobileVehicle ? 9.5f : 7.5f;
                    projector.size = new Vector3(decalSize, decalSize, decalSize * 0.25f);
                    projector.fadeFactor = 0.85f;
                    Vector3 normal = groundNormal.sqrMagnitude > 0.001f ? groundNormal.normalized : Vector3.up;
                    scorch.transform.rotation = Quaternion.LookRotation(-normal)
                        * Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
                }
                else
                {
                    scorch.transform.position = groundedPos + (Vector3.up * 0.03f);
                    scorch.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                }
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
                    debrisGo.layer = PhysicsLayers.IgnoreCollisions;
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
            sm.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
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

            ParticleSystem.ColorOverLifetimeModule sc = smokePs.colorOverLifetime;
            sc.enabled = true;
            var smokeGrad = new Gradient();
            smokeGrad.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.65f, 0.15f),
                    new GradientAlphaKey(0.45f, 0.55f),
                    new GradientAlphaKey(0f, 1f)
                });
            sc.color = new ParticleSystem.MinMaxGradient(smokeGrad);

            ParticleSystem.SizeOverLifetimeModule ssz = smokePs.sizeOverLifetime;
            ssz.enabled = true;
            ssz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.55f),
                new Keyframe(1f, 2.1f)));

            ParticleSystem.RotationOverLifetimeModule srot = smokePs.rotationOverLifetime;
            srot.enabled = true;
            srot.z = new ParticleSystem.MinMaxCurve(-0.45f, 0.45f);

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

            ParticleSystem.ColorOverLifetimeModule fc = sparkPs.colorOverLifetime;
            fc.enabled = true;
            var sparkGrad = new Gradient();
            sparkGrad.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.85f, 0.4f),
                    new GradientAlphaKey(0f, 1f)
                });
            fc.color = new ParticleSystem.MinMaxGradient(sparkGrad);

            sparkPs.Play();
            outList.Add(sparkPs);
        }

        private static void EnsureParticleAssets()
        {
            if (smokeSoftTexture == null)
            {
                const int size = 64;
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
                        float r = Mathf.Sqrt((dx * dx) + (dy * dy));
                        float alpha = r >= 1f ? 0f : Mathf.Pow(1f - r, 2.2f);
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

            if (mat.HasProperty("_ColorMode"))
            {
                mat.SetFloat("_ColorMode", 0f);
            }

            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
            }

            if (mat.HasProperty("_Blend"))
            {
                mat.SetFloat("_Blend", additive ? 2f : 0f);
            }

            if (mat.HasProperty("_AlphaClip"))
            {
                mat.SetFloat("_AlphaClip", 0f);
            }

            if (mat.HasProperty("_SrcBlend"))
            {
                mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            }

            if (mat.HasProperty("_DstBlend"))
            {
                mat.SetInt("_DstBlend", (int)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            }

            if (mat.HasProperty("_SrcBlendAlpha"))
            {
                mat.SetInt("_SrcBlendAlpha", (int)BlendMode.One);
            }

            if (mat.HasProperty("_DstBlendAlpha"))
            {
                mat.SetInt("_DstBlendAlpha", (int)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            }

            if (mat.HasProperty("_ZWrite"))
            {
                mat.SetInt("_ZWrite", 0);
            }

            if (mat.HasProperty("_Cull"))
            {
                mat.SetInt("_Cull", (int)CullMode.Off);
            }

            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            if (additive)
            {
                mat.EnableKeyword("_BLENDMODE_ADD");
            }
            else
            {
                mat.DisableKeyword("_BLENDMODE_ADD");
            }

            return mat;
        }
    }
}
