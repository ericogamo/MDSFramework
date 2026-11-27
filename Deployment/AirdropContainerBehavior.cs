using System;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Missiles;
using MDSFramework.Networking;
using MDSFramework.Sensors;
using UnityEngine;

namespace MDSFramework.Deployment
{
    internal sealed class AirdropContainerBehavior : MonoBehaviour
    {
        private const float SettleRequiredSeconds = 1.2f;
        private const float WaterSplashDespawnSeconds = 6.0f;

        internal ContractorPackage Package { get; private set; } = null!;
        internal ModularUnitSpec Spec { get; private set; } = null!;
        internal Container Container { get; private set; } = null!;
        internal FactionAffinity Faction { get; private set; }
        internal FactionHQ? Hq { get; private set; }
        internal GlbInstantiatedRig? Rig { get; private set; }
        internal GlbKeyframedAnimator? Animator { get; private set; }
        internal GlbTurretAimController? TurretAim { get; private set; }
        internal ModularSiteBehavior? ActivatedSite { get; private set; }
        internal bool ConsumedRtsCap = true;

        private float settledTimer;
        private float waterStrandedTimer;
        private bool wasSlungLastFrame;
        private bool initialized;
        private bool capReleased;

        internal void Initialize(
            ContractorPackage package,
            ModularUnitSpec spec,
            Container container,
            FactionAffinity faction,
            bool ensureParachute)
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            Package = package;
            Spec = spec;
            Container = container;
            Hq = container.NetworkHQ ?? container.MapHQ ?? GameInteropHelpers.TryResolveFactionHQ(faction);
            Faction = faction != FactionAffinity.Any
                ? faction
                : GameInteropHelpers.ToFactionAffinity(Hq);

            string wireCargoName = $"[MDS Cargo] {spec.DisplayName}";
            if (!string.Equals(container.unitName, wireCargoName, StringComparison.Ordinal))
            {
                container.NetworkunitName = wireCargoName;
            }

            container.RCS = Mathf.Max(0.02f, spec.StowedRcs);
            container.onDisableUnit += OnContainerDisabled;

            DisableConflictingVanillaComponents(container);
            AttachCustomStowedRig(container);
            HideVanillaContainerMeshes(container);

            if (ensureParachute)
            {
                EnsureParachuteAttached(container);
            }
        }

        private void Update()
        {
            if (!initialized || Container == null || Container.disabled)
            {
                return;
            }

            float dt = Time.deltaTime;
            bool isSlung = Container.IsSlung();

            if (ActivatedSite == null)
            {
                Animator?.TickAnimation(dt, allowTurretOverrides: false);
                if (!isSlung && IsDryGroundSettled(dt))
                {
                    waterStrandedTimer = 0f;
                    ActivateAsModularSite();
                }
                else if (isSlung)
                {
                    settledTimer = 0f;
                    waterStrandedTimer = 0f;
                }
                else if (GameInteropHelpers.IsServerAuthority() && IsStrandedInWater())
                {
                    waterStrandedTimer += dt;
                    if (waterStrandedTimer >= WaterSplashDespawnSeconds)
                    {
                        DespawnWaterStrandedContainer();
                        return;
                    }
                }
                else
                {
                    waterStrandedTimer = 0f;
                }

                wasSlungLastFrame = isSlung;
                return;
            }

            // Support helicopter sling-load relocation of an already deployed site.
            if (isSlung && !wasSlungLastFrame)
            {
                ActivatedSite.SuspendedInTransit = true;
                ActivatedSite.RequestDeployState(false);
                settledTimer = 0f;
                MdsLog.Info($"[Airdrop] '{ActivatedSite.SiteId}' picked up by sling-load -> stowed for transit.");
            }
            else if (ActivatedSite.SuspendedInTransit && !isSlung && IsDryGroundSettled(dt))
            {
                Vector3 euler = Container.transform.eulerAngles;
                Container.transform.rotation = Quaternion.Euler(0f, euler.y, 0f);
                Physics.SyncTransforms();

                ActivatedSite.SuspendedInTransit = false;
                ActivatedSite.LastThreatSeenTime = Time.time;
                RtsBridgeHost.Current.AiThreats.ReportDeployedModularSite(
                    ActivatedSite.SiteId,
                    ActivatedSite.Faction,
                    Spec.Role,
                    Container.transform.position,
                    ActivatedSite.IsRadarEmitting);
                MdsNetworkSync.BroadcastContainerLanded(Container.persistentID.Id, euler.y, Spec.ContainerLengthMeters);
                MdsLog.Info($"[Airdrop] '{ActivatedSite.SiteId}' resettled after sling-load relocation.");
            }

            wasSlungLastFrame = isSlung;
        }

        private bool IsStrandedInWater()
        {
            Vector3 pos = Container.transform.position;
            if (pos.y > Datum.LocalSeaY + 0.55f)
            {
                return false;
            }

            if (Physics.Raycast(
                    pos + Vector3.up * 1.5f,
                    Vector3.down,
                    out RaycastHit hit,
                    3.0f,
                    (int)PhysicsLayers.StaticsMask,
                    QueryTriggerInteraction.Ignore) &&
                hit.point.y > Datum.LocalSeaY + 0.20f)
            {
                return false;
            }

            return true;
        }

        private void DespawnWaterStrandedContainer()
        {
            ReleaseUnactivatedCapOnce();
            if (Rig != null && Rig.RootObject != null)
            {
                Rig.RootObject.SetActive(false);
            }

            MdsLog.Info($"[Airdrop] Container '{Spec?.UnitId}' stranded in water for {WaterSplashDespawnSeconds:0.#}s; despawning and releasing RTS cap.");
            Container.DisableUnit();
            if (GameManager.gameState == GameState.SinglePlayer && Container != null)
            {
                UnityEngine.Object.Destroy(Container.gameObject, 2f);
            }
        }

        private bool IsDryGroundSettled(float dt)
        {
            Vector3 pos = Container.transform.position;
            if (pos.y <= Datum.LocalSeaY + 0.35f)
            {
                settledTimer = 0f;
                return false;
            }

            if (Container.transform.parent != null && Container.transform.parent.GetComponentInParent<Ship>() != null)
            {
                settledTimer = 0f;
                return false;
            }

            if (Physics.Raycast(
                    pos + Vector3.up * 1.5f,
                    Vector3.down,
                    out RaycastHit shipHit,
                    4.5f,
                    (int)PhysicsLayers.ShipsMask,
                    QueryTriggerInteraction.Ignore) &&
                shipHit.collider != null &&
                shipHit.collider.GetComponentInParent<Ship>() != null)
            {
                settledTimer = 0f;
                return false;
            }

            bool grounded = Container.Networkkinematic;
            if (!grounded)
            {
                float speedSq = Container.rb != null ? Container.rb.velocity.sqrMagnitude : 99f;
                int groundMask = (int)PhysicsLayers.StaticsMask;
                if (speedSq < 0.36f &&
                    Physics.Raycast(pos + Vector3.up * 1.5f, Vector3.down, 4.5f, groundMask, QueryTriggerInteraction.Ignore))
                {
                    grounded = true;
                }
            }

            if (grounded)
            {
                settledTimer += dt;
                return settledTimer >= SettleRequiredSeconds;
            }

            settledTimer = 0f;
            return false;
        }

        private void ActivateAsModularSite()
        {
            if (ModularSiteManager.Instance == null)
            {
                return;
            }

            CargoDeploymentSystem[] crates = Container.GetComponentsInChildren<CargoDeploymentSystem>(true);
            for (int i = 0; i < crates.Length; i++)
            {
                if (crates[i] != null && crates[i].gameObject != Container.gameObject)
                {
                    UnityEngine.Object.Destroy(crates[i].gameObject);
                }
            }

            BoxCollider? box = Container.GetComponent<BoxCollider>();
            if (box != null)
            {
                box.enabled = true;
                if (Spec.ContainerLengthMeters > 6.5f)
                {
                    float oldBottomY = box.center.y - (box.size.y * 0.5f);
                    float w = Mathf.Max(2.438f, Spec.ContainerWidthMeters);
                    float h = Mathf.Max(2.591f, Spec.ContainerHeightMeters);
                    float l = Mathf.Max(6.058f, Spec.ContainerLengthMeters);
                    box.size = new Vector3(w, h, l);
                    box.center = new Vector3(box.center.x, oldBottomY + (h * 0.5f), box.center.z);
                }
            }

            // Level out slight pitch/roll on uneven drop terrain once stationary.
            Vector3 euler = Container.transform.eulerAngles;
            Container.transform.rotation = Quaternion.Euler(0f, euler.y, 0f);

            ActivatedSite = ModularSiteManager.Instance.ActivateLandedContainerSite(this);
            MdsNetworkSync.BroadcastContainerLanded(Container.persistentID.Id, euler.y, Spec.ContainerLengthMeters);
            if (ActivatedSite != null && MdsConfig.AutoDeployBrieflyOnTouchdown.Value)
            {
                ActivatedSite.LastThreatSeenTime = Time.time;
                ActivatedSite.RequestDeployState(true);
            }
        }

        private void AttachCustomStowedRig(Container container)
        {
            if (!MdsConfig.EnableCustomGlbVisuals.Value)
            {
                return;
            }

            string? glbPath = CustomMissileBehavior.ResolveAssetPath(Package, Spec.GlbRelativePath, Spec.FallbackGlbRelativePaths);
            string? decalPath = CustomMissileBehavior.ResolveAssetPath(Package, Spec.DecalRelativePath, Array.Empty<string>());

            Rig = GlbRuntimeLoader.Instantiate(
                Spec.UnitId,
                glbPath,
                Spec.FallbackMeshArchetype,
                decalPath,
                container.transform,
                Spec.ModelYawOffsetDeg);

            if (Rig == null)
            {
                return;
            }

            BoxCollider? box = container.GetComponent<BoxCollider>();
            float boxBottomY = box != null ? box.center.y - (box.size.y * 0.5f) : 0f;
            if (Rig.RootObject != null)
            {
                Rig.RootObject.transform.localPosition = new Vector3(0f, boxBottomY - Rig.Bounds.min.y, 0f);
            }

            Animator = Rig.RootObject!.AddComponent<GlbKeyframedAnimator>();
            Animator.Initialize(Rig, Spec.DeployDurationSeconds, startDeployed: false);

            Color beamColor = Spec.Role == WeaponCascadeRole.LaserHel
                ? new Color(0.12f, 0.95f, 0.72f, 1f)
                : Spec.Role == WeaponCascadeRole.CloseInGun
                    ? new Color(1.0f, 0.72f, 0.20f, 1f)
                    : new Color(0.25f, 0.85f, 1.0f, 1f);

            TurretAim = Rig.RootObject.AddComponent<GlbTurretAimController>();
            TurretAim.Initialize(
                Rig,
                Animator,
                Spec.TurretTraverseDegPerSec,
                Spec.TurretElevationDegPerSec,
                Spec.MinElevationDeg,
                Spec.MaxElevationDeg,
                Spec.SpinRadarDish,
                Spec.RadarSpinDegPerSec,
                beamColor);
        }

        private static void HideVanillaContainerMeshes(Container container)
        {
            Renderer[] renderers = container.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || r is ParticleSystemRenderer)
                {
                    continue;
                }

                if (IsPreservedRenderer(r))
                {
                    continue;
                }

                r.enabled = false;
            }
        }

        private static bool IsPreservedRenderer(Renderer r)
        {
            if (r.GetComponentInParent<Parachute>(true) != null ||
                r.GetComponentInParent<CargoDeploymentSystem>(true) != null)
            {
                return true;
            }

            Transform? tf = r.transform;
            while (tf != null)
            {
                string n = tf.name;
                if (n.StartsWith("MDS_Rig_", StringComparison.OrdinalIgnoreCase) ||
                    n.IndexOf("Parachute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Canopy", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                tf = tf.parent;
            }

            return false;
        }

        private void DisableConflictingVanillaComponents(Container container)
        {
            Rearmer[] rearmers = container.GetComponentsInChildren<Rearmer>(true);
            for (int i = 0; i < rearmers.Length; i++)
            {
                Rearmer r = rearmers[i];
                if (r == null)
                {
                    continue;
                }

                r.Capacity = 0f;
                r.AvailableForMission = false;
                r.enabled = false;
                container.NetworkHQ?.RearmMissionController?.DeregisterRearmer(r);
                UnityEngine.Object.Destroy(r);
            }

            Turret[] turrets = container.GetComponentsInChildren<Turret>(true);
            for (int i = 0; i < turrets.Length; i++)
            {
                turrets[i].enabled = false;
            }

            FireControl[] fcs = container.GetComponentsInChildren<FireControl>(true);
            for (int i = 0; i < fcs.Length; i++)
            {
                fcs[i].enabled = false;
            }

            if (Spec.Role != WeaponCascadeRole.EarlyWarningRadar)
            {
                Radar[] radars = container.GetComponentsInChildren<Radar>(true);
                for (int i = 0; i < radars.Length; i++)
                {
                    radars[i].activated = false;
                    if (!Spec.SensorProfile.IsFireControlUnit)
                    {
                        radars[i].enabled = false;
                    }
                }
            }
        }

        private static void EnsureParachuteAttached(Container container)
        {
            if (container.GetComponentInChildren<CargoDeploymentSystem>(true) != null)
            {
                return;
            }

            if (Physics.Linecast(
                    container.transform.position,
                    container.transform.position - (Vector3.up * 10f),
                    PhysicsLayers.StaticsMask))
            {
                return;
            }

            if (GameInteropHelpers.ContainerParachuteSystemField?.GetValue(container) is GameObject chutePrefab &&
                chutePrefab != null)
            {
                GameObject chuteInstance = UnityEngine.Object.Instantiate(chutePrefab, container.transform);
                CargoDeploymentSystem? cds = chuteInstance.GetComponent<CargoDeploymentSystem>();
                cds?.Initialize(container);
            }
        }

        private void OnContainerDisabled(Unit _)
        {
            if (ActivatedSite == null && Rig != null && Rig.RootObject != null)
            {
                Rig.RootObject.SetActive(false);
            }

            ReleaseUnactivatedCapOnce();
        }

        private void OnDestroy()
        {
            if (Container != null)
            {
                Container.onDisableUnit -= OnContainerDisabled;
            }

            ReleaseUnactivatedCapOnce();
        }

        private void ReleaseUnactivatedCapOnce()
        {
            if (capReleased ||
                ActivatedSite != null ||
                !ConsumedRtsCap ||
                MdsRuntimeController.IsResettingScene ||
                !GameInteropHelpers.IsServerAuthority())
            {
                return;
            }

            capReleased = true;
            Hq ??= Container != null ? (Container.NetworkHQ ?? Container.MapHQ) : null;
            if (Faction == FactionAffinity.Any && Hq != null)
            {
                Faction = GameInteropHelpers.ToFactionAffinity(Hq);
            }

            if (Spec?.Logistics != null)
            {
                RtsBridgeHost.Current.Caps.ReleaseCap(Faction, Spec.Logistics.CapCategory, Spec.Logistics.CapWeight);
            }
        }
    }
}
