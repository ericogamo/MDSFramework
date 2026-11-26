using System;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Missiles;
using MDSFramework.Patches;
using MDSFramework.Sensors;
using UnityEngine;

namespace MDSFramework.Deployment
{
    internal sealed class ModularSiteManager
    {
        internal static ModularSiteManager? Instance { get; private set; }

        private IRtsLogisticsBridge? subscribedLogistics;
        private int nextSiteSerial = 1;

        internal ModularSiteManager()
        {
            Instance = this;
            RebindLogisticsBridge();
            RtsBridgeHost.OnBridgeChanged += RebindLogisticsBridge;
        }

        internal void Dispose()
        {
            RtsBridgeHost.OnBridgeChanged -= RebindLogisticsBridge;
            if (subscribedLogistics != null)
            {
                subscribedLogistics.OnPhysicalKitDelivered -= HandlePhysicalKitDelivered;
                subscribedLogistics = null;
            }
        }

        internal void ResetSession()
        {
            nextSiteSerial = 1;
        }

        internal ModularSiteBehavior? SpawnPhysicalSite(string unitId, FactionAffinity faction, Vector3 worldPos, float yawDeg)
        {
            if (!GameInteropHelpers.IsServerAuthority())
            {
                return null;
            }

            if (Spawner.i == null)
            {
                MdsLog.Warn($"Cannot spawn '{unitId}': Spawner.i is null.");
                return null;
            }

            if (!ContractorRegistry.TryGetUnitSpec(unitId, out ContractorPackage package, out ModularUnitSpec spec))
            {
                MdsLog.Warn($"Cannot spawn unknown MDS unitId '{unitId}'.");
                return null;
            }

            if (!GameInteropHelpers.TrySnapToDryTerrain(worldPos, out Vector3 groundedPos))
            {
                MdsLog.Warn($"Cannot spawn '{unitId}' over water at {worldPos}.");
                return null;
            }

            FactionAffinity resolvedReqFaction = GameInteropHelpers.ResolveConcreteFaction(faction, out FactionHQ? hq);
            GameObject? basePrefab = GameInteropHelpers.ResolveEncyclopediaUnitPrefab(spec.BaseDefinitionCloneKey, out bool isBuilding, out bool isVehicle);
            if (basePrefab == null)
            {
                MdsLog.Warn($"Cannot resolve vanilla base prefab '{spec.BaseDefinitionCloneKey}' for '{unitId}'.");
                return null;
            }

            UnitDefinition? customDef = MdsEncyclopediaCatalog.TryGetUnitDefinition(spec.UnitId, out UnitDefinition catalogDef)
                ? catalogDef
                : null;

            GlobalPosition gPos = GameInteropHelpers.WorldToGlobal(groundedPos);
            Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
            string siteId = $"{spec.UnitId}_{nextSiteSerial++}";

            Unit? spawnedUnit = null;
            bool pushedScope = false;
            try
            {
                PendingMdsSpawnScope.PushUnit(package, spec, customDef);
                pushedScope = true;

                if (isVehicle)
                {
                    float vehicleSpawnY = (customDef != null && customDef.spawnOffset.y > 0.01f) ? customDef.spawnOffset.y : 0.954f;
                    GlobalPosition vehPos = GameInteropHelpers.WorldToGlobal(groundedPos + (Vector3.up * vehicleSpawnY));
                    spawnedUnit = Spawner.i.SpawnVehicle(basePrefab, vehPos, rot, Vector3.zero, hq!, siteId, 1f, holdPosition: false, null!);
                }
                else if (isBuilding)
                {
                    Airbase? nearestAirbase = GameInteropHelpers.FindNearestAirbase(hq, groundedPos);
                    var factoryOpts = new NuclearOption.SavedMission.SavedBuilding.FactoryOptions();
                    spawnedUnit = Spawner.i.SpawnBuilding(basePrefab, gPos, rot, hq!, nearestAirbase!, siteId, capturable: false, factoryOpts);
                }
                else
                {
                    spawnedUnit = Spawner.i.SpawnContainer(basePrefab, gPos, rot, hq!, siteId);
                }
            }
            catch (Exception ex)
            {
                MdsLog.Warn($"Spawner failed for '{siteId}': {ex.Message}");
                return null;
            }
            finally
            {
                if (pushedScope)
                {
                    PendingMdsSpawnScope.Pop();
                }
            }

            if (spawnedUnit == null)
            {
                return null;
            }

            FactionAffinity resolvedFaction = resolvedReqFaction != FactionAffinity.Any
                ? resolvedReqFaction
                : GameInteropHelpers.ToFactionAffinity(hq);

            return AttachSiteToExistingUnit(
                spawnedUnit,
                package,
                spec,
                customDef,
                resolvedFaction,
                hq,
                consumedRtsCap: true,
                startDeployed: ShouldStartDeployed(spec));
        }

        internal static bool ShouldStartDeployed(ModularUnitSpec spec)
        {
            if (spec.IsMobileVehicle ||
                spec.Role == WeaponCascadeRole.AntiShipCoastal ||
                spec.Role == WeaponCascadeRole.AntiVehicleDrone)
            {
                return false;
            }

            return spec.Role == WeaponCascadeRole.FireControl ||
                   spec.Role == WeaponCascadeRole.EarlyWarningRadar ||
                   spec.Role == WeaponCascadeRole.CommandControl ||
                   !MdsConfig.EnableAutoPopUp.Value;
        }

        private static void AlignAndBindVehicleRig(GameObject targetRoot, ModularUnitSpec spec, GlbInstantiatedRig rig)
        {
            if (rig.RootObject == null)
            {
                return;
            }

            float targetBottomY = 0f;
            if (spec.IsMobileVehicle && targetRoot.TryGetComponent(out GroundVehicle gv))
            {
                targetBottomY = -0.954f;
                if (gv.definition != null && gv.definition.spawnOffset.y > 0.1f)
                {
                    targetBottomY = -gv.definition.spawnOffset.y;
                }
                else if (GameInteropHelpers.GroundVehicleWheelsField?.GetValue(gv) is GameObject[] vanillaWheels &&
                    vanillaWheels.Length > 0 &&
                    vanillaWheels[0] != null)
                {
                    float vanillaRadius = GameInteropHelpers.GroundVehicleWheelRadiusField?.GetValue(gv) is float r && r > 0.1f
                        ? r
                        : 0.670f;
                    targetBottomY = gv.transform.InverseTransformPoint(vanillaWheels[0].transform.position).y - vanillaRadius;
                }

                if (rig.WheelAxles.Count > 0)
                {
                    var customWheels = new GameObject[rig.WheelAxles.Count];
                    for (int i = 0; i < rig.WheelAxles.Count; i++)
                    {
                        customWheels[i] = rig.WheelAxles[i].gameObject;
                    }

                    GameInteropHelpers.GroundVehicleWheelsField?.SetValue(gv, customWheels);
                }

                if (spec.WheelRadiusMeters > 0.1f)
                {
                    GameInteropHelpers.GroundVehicleWheelRadiusField?.SetValue(gv, spec.WheelRadiusMeters);
                }
            }

            if (Mathf.Abs(targetBottomY - rig.Bounds.min.y) > 0.01f)
            {
                rig.RootObject.transform.localPosition = new Vector3(0f, targetBottomY - rig.Bounds.min.y, 0f);
            }
        }

        internal ModularSiteBehavior? AttachSiteToExistingUnit(
            Unit unit,
            ContractorPackage package,
            ModularUnitSpec spec,
            UnitDefinition? customDef,
            FactionAffinity faction,
            FactionHQ? hq,
            bool consumedRtsCap,
            bool startDeployed)
        {
            if (unit == null || unit.disabled)
            {
                return null;
            }

            ModularSiteBehavior? existing = unit.GetComponent<ModularSiteBehavior>();
            if (existing != null)
            {
                return existing;
            }

            if (customDef != null)
            {
                unit.definition = customDef;
            }

            unit.NetworkunitName = spec.DisplayName;
            DisableVanillaVisualsAndAutoWeapons(unit, spec);

            GlbInstantiatedRig? rig = null;
            GlbKeyframedAnimator? animator = null;
            GlbTurretAimController? turretAim = null;

            if (MdsConfig.EnableCustomGlbVisuals.Value)
            {
                string? glbPath = CustomMissileBehavior.ResolveAssetPath(package, spec.GlbRelativePath, spec.FallbackGlbRelativePaths);
                string? decalPath = CustomMissileBehavior.ResolveAssetPath(package, spec.DecalRelativePath, Array.Empty<string>());

                rig = GlbRuntimeLoader.Instantiate(
                    spec.UnitId,
                    glbPath,
                    spec.FallbackMeshArchetype,
                    decalPath,
                    unit.transform,
                    spec.ModelYawOffsetDeg);

                if (rig != null)
                {
                    AlignAndBindVehicleRig(unit.gameObject, spec, rig);

                    animator = rig.RootObject.GetComponent<GlbKeyframedAnimator>() ?? rig.RootObject.AddComponent<GlbKeyframedAnimator>();
                    animator.Initialize(rig, spec.DeployDurationSeconds, startDeployed: startDeployed);

                    Color beamColor = spec.Role == WeaponCascadeRole.LaserHel
                        ? new Color(0.12f, 0.95f, 0.72f, 1f)
                        : spec.Role == WeaponCascadeRole.CloseInGun
                            ? new Color(1.0f, 0.72f, 0.20f, 1f)
                            : new Color(0.25f, 0.85f, 1.0f, 1f);

                    turretAim = rig.RootObject.GetComponent<GlbTurretAimController>() ?? rig.RootObject.AddComponent<GlbTurretAimController>();
                    turretAim.Initialize(
                        rig,
                        animator,
                        spec.TurretTraverseDegPerSec,
                        spec.TurretElevationDegPerSec,
                        spec.MinElevationDeg,
                        spec.MaxElevationDeg,
                        spec.SpinRadarDish,
                        spec.RadarSpinDegPerSec,
                        beamColor);
                }
            }

            EnsureSelectableUnitCollider(unit, rig?.RootObject != null ? rig.RootObject.transform : null, spec);

            FactionAffinity resolvedFaction = faction != FactionAffinity.Any
                ? faction
                : GameInteropHelpers.ToFactionAffinity(hq);
            string siteId = !string.IsNullOrEmpty(unit.UniqueName)
                ? unit.UniqueName
                : $"{spec.UnitId}_{nextSiteSerial++}";

            ModularSiteBehavior behavior = unit.gameObject.AddComponent<ModularSiteBehavior>();
            behavior.ConsumedRtsCap = consumedRtsCap;
            behavior.Initialize(siteId, package, spec, resolvedFaction, hq, unit, rig, animator, turretAim);
            MdsDestructionManager.SuppressVanillaWreckage(unit);
            ConfigureRepairabilityAndMapIcon(unit, behavior);
            MdsWeaponStationBuilder.ApplyWeaponStations(unit, package, spec, behavior);
            behavior.SyncWeaponStationsAmmo();
            SensorNetworkManager.Instance?.RegisterSite(behavior);

            RtsBridgeHost.Current.AiThreats.ReportDeployedModularSite(
                siteId,
                resolvedFaction,
                spec.Role,
                unit.transform.position,
                behavior.IsRadarEmitting);

            MdsLog.Info($"Deployed modular site '{siteId}' ({spec.DisplayName}, faction={resolvedFaction}) at {unit.transform.position}.");
            return behavior;
        }

        internal static void AttachVisualPreviewRig(
            GameObject targetRoot,
            ContractorPackage package,
            ModularUnitSpec spec,
            bool startDeployed,
            bool attachClientRadarSpin = false)
        {
            if (targetRoot == null || package == null || spec == null)
            {
                return;
            }

            if (targetRoot.transform.Find($"MDS_Rig_{spec.UnitId}") != null)
            {
                return;
            }

            HideVanillaRenderers(targetRoot, preserveCargoChute: true);
            DisableVanillaAutoWeapons(targetRoot, spec.Role);

            if (!MdsConfig.EnableCustomGlbVisuals.Value)
            {
                return;
            }

            string? glbPath = CustomMissileBehavior.ResolveAssetPath(package, spec.GlbRelativePath, spec.FallbackGlbRelativePaths);
            string? decalPath = CustomMissileBehavior.ResolveAssetPath(package, spec.DecalRelativePath, Array.Empty<string>());

            GlbInstantiatedRig? rig = GlbRuntimeLoader.Instantiate(
                spec.UnitId,
                glbPath,
                spec.FallbackMeshArchetype,
                decalPath,
                targetRoot.transform,
                spec.ModelYawOffsetDeg);

            if (rig != null)
            {
                AlignAndBindVehicleRig(targetRoot, spec, rig);

                GlbKeyframedAnimator animator = rig.RootObject.GetComponent<GlbKeyframedAnimator>() ?? rig.RootObject.AddComponent<GlbKeyframedAnimator>();
                animator.Initialize(rig, spec.DeployDurationSeconds, startDeployed: startDeployed);

                if (attachClientRadarSpin && spec.SpinRadarDish && rig.RadarDish != null)
                {
                    MdsClientRadarSpinTicker ticker = rig.RootObject.GetComponent<MdsClientRadarSpinTicker>() ??
                                                      rig.RootObject.AddComponent<MdsClientRadarSpinTicker>();
                    ticker.Configure(rig.RadarDish, spec.RadarSpinDegPerSec);
                }
            }

            if (GameManager.gameState != GameState.Editor &&
                GameManager.gameState != GameState.Encyclopedia &&
                targetRoot.TryGetComponent(out Unit previewUnit))
            {
                MdsDestructionManager.SuppressVanillaWreckage(previewUnit);
                if (previewUnit.GetComponent<ModularSiteBehavior>() == null)
                {
                    MdsClientDestructionWatcher watcher = previewUnit.GetComponent<MdsClientDestructionWatcher>()
                        ?? previewUnit.gameObject.AddComponent<MdsClientDestructionWatcher>();
                    watcher.Configure(previewUnit, package, spec);
                }
            }
        }

        internal static void AttachMissileVisualPreview(
            GameObject targetRoot,
            ContractorPackage package,
            CustomMissileSpec spec)
        {
            if (targetRoot == null || package == null || spec == null)
            {
                return;
            }

            if (targetRoot.transform.Find($"MDS_Rig_{spec.MissileId}") != null)
            {
                return;
            }

            HideVanillaRenderers(targetRoot, preserveCargoChute: false);

            if (!MdsConfig.EnableCustomGlbVisuals.Value)
            {
                return;
            }

            string? resolvedGlb = CustomMissileBehavior.ResolveAssetPath(package, spec.GlbRelativePath, spec.FallbackGlbRelativePaths);
            string? resolvedDecal = CustomMissileBehavior.ResolveAssetPath(package, spec.DecalRelativePath, Array.Empty<string>());

            GlbInstantiatedRig? rig = GlbRuntimeLoader.Instantiate(
                spec.MissileId,
                resolvedGlb,
                spec.FallbackMeshArchetype,
                resolvedDecal,
                targetRoot.transform,
                spec.ModelYawOffsetDeg);

            if (rig != null)
            {
                GlbKeyframedAnimator animator = rig.RootObject.GetComponent<GlbKeyframedAnimator>() ?? rig.RootObject.AddComponent<GlbKeyframedAnimator>();
                animator.Initialize(rig, desiredDurationSeconds: 0.75f, startDeployed: true);
            }
        }

        internal ModularSiteBehavior? ActivateLandedContainerSite(AirdropContainerBehavior airdrop)
        {
            if (airdrop == null || airdrop.Container == null || airdrop.Container.disabled)
            {
                return null;
            }

            ModularSiteBehavior? existing = airdrop.Container.GetComponent<ModularSiteBehavior>();
            if (existing != null)
            {
                return existing;
            }

            string siteId = $"{airdrop.Spec.UnitId}_{nextSiteSerial++}";
            FactionHQ? hq = airdrop.Container.NetworkHQ ?? airdrop.Container.MapHQ ?? GameInteropHelpers.TryResolveFactionHQ(airdrop.Faction);
            FactionAffinity resolvedFaction = airdrop.Faction != FactionAffinity.Any
                ? airdrop.Faction
                : GameInteropHelpers.ToFactionAffinity(hq);

            if (airdrop.Rig != null && airdrop.Rig.RootObject != null)
            {
                BoxCollider? box = airdrop.Container.GetComponent<BoxCollider>();
                float boxBottomY = box != null ? box.center.y - (box.size.y * 0.5f) : 0f;
                airdrop.Rig.RootObject.transform.localPosition = new Vector3(0f, boxBottomY - airdrop.Rig.Bounds.min.y, 0f);
            }

            ModularSiteBehavior behavior = airdrop.Container.gameObject.AddComponent<ModularSiteBehavior>();
            behavior.ConsumedRtsCap = airdrop.ConsumedRtsCap;
            behavior.Initialize(
                siteId,
                airdrop.Package,
                airdrop.Spec,
                resolvedFaction,
                hq,
                airdrop.Container,
                airdrop.Rig,
                airdrop.Animator,
                airdrop.TurretAim);

            Rearmer[] strayRearmers = airdrop.Container.GetComponentsInChildren<Rearmer>(true);
            for (int rIdx = 0; rIdx < strayRearmers.Length; rIdx++)
            {
                Rearmer stray = strayRearmers[rIdx];
                if (stray == null)
                {
                    continue;
                }

                stray.Capacity = 0f;
                stray.AvailableForMission = false;
                stray.enabled = false;
                hq?.RearmMissionController?.DeregisterRearmer(stray);
                UnityEngine.Object.Destroy(stray);
            }

            airdrop.Container.radarAlt = 0f;
            airdrop.Container.speed = 0f;

            MdsWeaponStationBuilder.ApplyWeaponStations(airdrop.Container, airdrop.Package, airdrop.Spec, behavior);
            behavior.SyncWeaponStationsAmmo();
            SensorNetworkManager.Instance?.RegisterSite(behavior);

            Vector3 pos = airdrop.Container.transform.position;
            RtsBridgeHost.Current.AiThreats.ReportDeployedModularSite(
                siteId,
                resolvedFaction,
                airdrop.Spec.Role,
                pos,
                behavior.IsRadarEmitting);

            MdsLog.Info($"[Airdrop] Touchdown complete -> activated '{siteId}' ({airdrop.Spec.DisplayName}, faction={resolvedFaction}) at {pos}.");
            return behavior;
        }

        internal static void ReapplyHiddenVanillaVisuals(Unit unit)
        {
            if (unit != null)
            {
                HideVanillaRenderers(unit.gameObject, preserveCargoChute: true);
            }
        }

        private static void ConfigureRepairabilityAndMapIcon(Unit unit, ModularSiteBehavior behavior)
        {
            if (unit is Building building)
            {
                if (GameInteropHelpers.BuildingRepairTimeField != null)
                {
                    object? rawTime = GameInteropHelpers.BuildingRepairTimeField.GetValue(building);
                    float currentRepairTime = rawTime is float ft ? ft : 1000000f;
                    if (currentRepairTime >= 999999f)
                    {
                        GameInteropHelpers.BuildingRepairTimeField.SetValue(building, 25f);
                    }
                }

                if (GameInteropHelpers.BuildingHideOnMapField != null)
                {
                    GameInteropHelpers.BuildingHideOnMapField.SetValue(building, false);
                }

                if (!building.disabled && !UnitRegistry.allUnits.Contains(building))
                {
                    building.RegisterUnit(null);
                }

                if (building.partLookup != null)
                {
                    for (int i = 0; i < building.partLookup.Count; i++)
                    {
                        UnitPart? part = building.partLookup[i];
                        if (part != null)
                        {
                            part.onApplyDamage -= building.Building_OnPartDamage;
                            part.onApplyDamage += building.Building_OnPartDamage;
                        }
                    }
                }

                UnitPart[] allParts = building.GetComponentsInChildren<UnitPart>(true);
                for (int i = 0; i < allParts.Length; i++)
                {
                    UnitPart? part = allParts[i];
                    if (part != null)
                    {
                        part.onApplyDamage -= building.Building_OnPartDamage;
                        part.onApplyDamage += building.Building_OnPartDamage;
                    }
                }

                _ = behavior;
            }
        }

        private static void EnsureSelectableUnitCollider(Unit unit, Transform? rigRoot, ModularUnitSpec spec)
        {
            Collider[] cols = unit.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                Collider? col = cols[i];
                if (col != null && !col.isTrigger && col.enabled && (rigRoot == null || !col.transform.IsChildOf(rigRoot)))
                {
                    return;
                }
            }

            BoxCollider? existingRootBox = unit.GetComponent<BoxCollider>();
            if (existingRootBox != null)
            {
                existingRootBox.isTrigger = false;
                existingRootBox.enabled = true;
                return;
            }

            BoxCollider fallbackBox = unit.gameObject.AddComponent<BoxCollider>();
            float boxW = Mathf.Max(2.6f, spec.ContainerWidthMeters + 0.15f);
            float boxH = Mathf.Max(2.7f, spec.ContainerHeightMeters + 0.10f);
            float boxL = Mathf.Max(6.2f, spec.ContainerLengthMeters + 0.15f);
            fallbackBox.center = new Vector3(0f, boxH * 0.5f, 0f);
            fallbackBox.size = new Vector3(boxW, boxH, boxL);
            fallbackBox.isTrigger = false;
        }

        private static void DisableVanillaVisualsAndAutoWeapons(Unit unit, ModularUnitSpec spec)
        {
            HideVanillaRenderers(unit.gameObject, preserveCargoChute: true);
            DisableVanillaAutoWeapons(unit.gameObject, spec.Role);

            // Cloned HLT-L templates carry a vanilla Rearmer component; remove it so MDS trucks don't act as ammo trucks.
            Rearmer[] strayRearmers = unit.GetComponentsInChildren<Rearmer>(true);
            for (int i = 0; i < strayRearmers.Length; i++)
            {
                Rearmer stray = strayRearmers[i];
                if (stray == null)
                {
                    continue;
                }

                stray.enabled = false;
                unit.NetworkHQ?.RearmMissionController?.DeregisterRearmer(stray);
                UnityEngine.Object.Destroy(stray);
            }
        }

        private static void HideVanillaRenderers(GameObject root, bool preserveCargoChute)
        {
            MeshRenderer[] meshes = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i] != null && !IsPreservedVisual(meshes[i].transform, preserveCargoChute))
                {
                    meshes[i].enabled = false;
                }
            }

            SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                if (skinned[i] != null && !IsPreservedVisual(skinned[i].transform, preserveCargoChute))
                {
                    skinned[i].enabled = false;
                }
            }
        }

        private static bool IsPreservedVisual(Transform tf, bool preserveCargoChute)
        {
            if (preserveCargoChute &&
                (tf.GetComponentInParent<Parachute>(true) != null ||
                 tf.GetComponentInParent<CargoDeploymentSystem>(true) != null))
            {
                return true;
            }

            Transform? cursor = tf;
            while (cursor != null)
            {
                string n = cursor.name;
                if (n.StartsWith("MDS_Rig_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (preserveCargoChute &&
                    (n.IndexOf("Parachute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     n.IndexOf("Canopy", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }

                cursor = cursor.parent;
            }

            return false;
        }

        private static void DisableVanillaAutoWeapons(GameObject root, WeaponCascadeRole role)
        {
            Gun[] guns = root.GetComponentsInChildren<Gun>(true);
            for (int i = 0; i < guns.Length; i++)
            {
                guns[i].enabled = false;
            }

            MissileLauncher[] launchers = root.GetComponentsInChildren<MissileLauncher>(true);
            for (int i = 0; i < launchers.Length; i++)
            {
                launchers[i].enabled = false;
            }

            Laser[] lasers = root.GetComponentsInChildren<Laser>(true);
            for (int i = 0; i < lasers.Length; i++)
            {
                lasers[i].enabled = false;
            }

            if (role == WeaponCascadeRole.EarlyWarningRadar)
            {
                return;
            }

            Turret[] turrets = root.GetComponentsInChildren<Turret>(true);
            for (int i = 0; i < turrets.Length; i++)
            {
                turrets[i].enabled = false;
            }

            FireControl[] fcs = root.GetComponentsInChildren<FireControl>(true);
            for (int i = 0; i < fcs.Length; i++)
            {
                fcs[i].enabled = false;
            }
        }

        private void RebindLogisticsBridge()
        {
            if (subscribedLogistics != null)
            {
                subscribedLogistics.OnPhysicalKitDelivered -= HandlePhysicalKitDelivered;
            }

            subscribedLogistics = RtsBridgeHost.Current.Logistics;
            if (subscribedLogistics != null)
            {
                subscribedLogistics.OnPhysicalKitDelivered += HandlePhysicalKitDelivered;
            }
        }

        private void HandlePhysicalKitDelivered(string unitId, FactionAffinity faction, Vector3 worldPos, float yawDeg)
        {
            SpawnPhysicalSite(unitId, faction, worldPos, yawDeg);
        }
    }

    internal sealed class MdsClientRadarSpinTicker : MonoBehaviour
    {
        private Transform? radarDish;
        private float spinRateDegPerSec = 90f;

        internal void Configure(Transform dish, float rateDegPerSec)
        {
            radarDish = dish;
            spinRateDegPerSec = rateDegPerSec;
        }

        private void Update()
        {
            if (radarDish != null)
            {
                radarDish.Rotate(0f, spinRateDegPerSec * Time.deltaTime, 0f, Space.Self);
            }
        }
    }
}
