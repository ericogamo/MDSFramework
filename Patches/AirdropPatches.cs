using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Deployment;
using MDSFramework.Missiles;
using MDSFramework.Networking;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace MDSFramework.Patches
{
    [HarmonyPatch(typeof(Encyclopedia), "AfterLoad", new[] { typeof(Encyclopedia) })]
    internal static class EncyclopediaAfterLoadAirdropPatch
    {
        private static void Postfix()
        {
            MdsCatalogCoordinator.EnsureAllRegisteredCanonical();
        }
    }

    [HarmonyPatch(typeof(Encyclopedia), nameof(Encyclopedia.SortByValue))]
    internal static class EncyclopediaSortByValueMdsGuardPatch
    {
        private static void Prefix(Encyclopedia __instance, out List<VehicleDefinition>? __state)
        {
            __state = null;
            if (__instance == null || __instance.vehicles == null || __instance.vehicles.Count == 0)
            {
                return;
            }

            for (int i = __instance.vehicles.Count - 1; i >= 0; i--)
            {
                VehicleDefinition? v = __instance.vehicles[i];
                if (v != null && GameInteropHelpers.IsMdsSyntheticKey(v.jsonKey))
                {
                    __state ??= new List<VehicleDefinition>();
                    __state.Add(v);
                    __instance.vehicles.RemoveAt(i);
                }
            }

            if (__state != null && __state.Count > 1)
            {
                __state.Sort((a, b) => string.Compare(a.jsonKey, b.jsonKey, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static void Finalizer(Encyclopedia __instance, List<VehicleDefinition>? __state)
        {
            if (__instance == null || __instance.vehicles == null || __state == null || __state.Count == 0)
            {
                return;
            }

            for (int i = 0; i < __state.Count; i++)
            {
                VehicleDefinition v = __state[i];
                if (v != null && !__instance.vehicles.Contains(v))
                {
                    __instance.vehicles.Add(v);
                }
            }
        }
    }

    [HarmonyPatch(typeof(MountedCargo), nameof(MountedCargo.AttachToHardpoint), new[] { typeof(Aircraft), typeof(Hardpoint), typeof(WeaponMount) })]
    internal static class MountedCargoAttachAirdropPatch
    {
        private static void Prefix(MountedCargo __instance, WeaponMount mount)
        {
            if (__instance == null || mount == null)
            {
                return;
            }

            if (!AirdropCatalogManager.TryResolveMount(mount, out AirdropCatalogEntry entry))
            {
                return;
            }

            __instance.cargo = entry.CargoDefinition;

            if (!MdsConfig.EnableCustomGlbVisuals.Value)
            {
                return;
            }

            // Replace vanilla cargo bay mesh with contractor's stowed 20ft ISO stealth container.
            if (__instance.transform.Find($"MDS_Rig_{entry.Spec.UnitId}") == null)
            {
                Bounds vanillaBounds = ComputeVanillaLocalBounds(__instance.transform);
                MeshRenderer[] existing = __instance.GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < existing.Length; i++)
                {
                    existing[i].enabled = false;
                }

                string? glbPath = CustomMissileBehavior.ResolveAssetPath(
                    entry.Package,
                    entry.Spec.GlbRelativePath,
                    entry.Spec.FallbackGlbRelativePaths);
                string? decalPath = CustomMissileBehavior.ResolveAssetPath(
                    entry.Package,
                    entry.Spec.DecalRelativePath,
                    Array.Empty<string>());

                GlbInstantiatedRig? rig = GlbRuntimeLoader.Instantiate(
                    entry.Spec.UnitId,
                    glbPath,
                    entry.Spec.FallbackMeshArchetype,
                    decalPath,
                    __instance.transform,
                    entry.Spec.ModelYawOffsetDeg);

                if (rig != null)
                {
                    DisableOrDestroyRigColliders(rig.RootObject);
                    FitRigInsideBayBounds(rig, vanillaBounds, entry.Spec.ModelYawOffsetDeg);

                    GlbKeyframedAnimator anim = rig.RootObject.AddComponent<GlbKeyframedAnimator>();
                    anim.Initialize(rig, entry.Spec.DeployDurationSeconds, startDeployed: false);
                }
            }
        }

        private static Bounds ComputeVanillaLocalBounds(Transform root)
        {
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            bool hasBounds = false;
            Bounds localBounds = default;
            Matrix4x4 worldToRoot = root.worldToLocalMatrix;

            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter? mf = filters[i];
                if (mf == null || mf.sharedMesh == null)
                {
                    continue;
                }

                Bounds meshBounds = mf.sharedMesh.bounds;
                Matrix4x4 m = worldToRoot * mf.transform.localToWorldMatrix;
                EncapsulateTransformedBounds(ref localBounds, ref hasBounds, m, meshBounds);
            }

            if (!hasBounds || localBounds.size.sqrMagnitude < 0.05f)
            {
                return new Bounds(Vector3.zero, new Vector3(2.2f, 2.0f, 4.5f));
            }

            return localBounds;
        }

        private static void DisableOrDestroyRigColliders(GameObject rootObject)
        {
            Collider[] cols = rootObject.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null)
                {
                    cols[i].enabled = false;
                    UnityEngine.Object.Destroy(cols[i]);
                }
            }
        }

        private static void FitRigInsideBayBounds(GlbInstantiatedRig rig, Bounds bayBounds, float yawOffsetDeg)
        {
            Bounds src = rig.Bounds;
            if (src.size.sqrMagnitude < 0.01f)
            {
                return;
            }

            bool hasRot = false;
            Bounds rotated = default;
            Matrix4x4 rotMat = Matrix4x4.Rotate(Quaternion.Euler(0f, yawOffsetDeg, 0f));
            EncapsulateTransformedBounds(ref rotated, ref hasRot, rotMat, src);

            Vector3 rotSize = rotated.size;
            float sx = rotSize.x > 0.01f ? bayBounds.size.x / rotSize.x : 1f;
            float sy = rotSize.y > 0.01f ? bayBounds.size.y / rotSize.y : 1f;
            float sz = rotSize.z > 0.01f ? bayBounds.size.z / rotSize.z : 1f;
            float uniformScale = Mathf.Clamp(Mathf.Min(sx, Mathf.Min(sy, sz)), 0.1f, 1.0f);

            rig.RootObject.transform.localScale = Vector3.one * uniformScale;
            rig.RootObject.transform.localPosition = bayBounds.center - (rotated.center * uniformScale);
        }

        private static void EncapsulateTransformedBounds(ref Bounds target, ref bool initialized, Matrix4x4 matrix, Bounds source)
        {
            Vector3 c = source.center;
            Vector3 e = source.extents;
            for (int xi = -1; xi <= 1; xi += 2)
            {
                for (int yi = -1; yi <= 1; yi += 2)
                {
                    for (int zi = -1; zi <= 1; zi += 2)
                    {
                        Vector3 pt = matrix.MultiplyPoint3x4(new Vector3(c.x + (e.x * xi), c.y + (e.y * yi), c.z + (e.z * zi)));
                        if (!initialized)
                        {
                            target = new Bounds(pt, Vector3.zero);
                            initialized = true;
                        }
                        else
                        {
                            target.Encapsulate(pt);
                        }
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(AIHeloTakeoffState), "Takeoff_OnTakeDamage", new[] { typeof(UnitPart.OnApplyDamage) })]
    internal static class AIHeloTakeoffSpawnBumpGuardPatch
    {
        private static readonly FieldInfo? SpawnTimeField = AccessTools.Field(typeof(AIHeloTakeoffState), "spawnTime");
        private static readonly FieldInfo? AircraftField = AccessTools.Field(typeof(PilotBaseState), "aircraft");
        private static readonly FieldInfo? PilotField = AccessTools.Field(typeof(PilotBaseState), "pilot");

        private static bool Prefix(AIHeloTakeoffState __instance, UnitPart.OnApplyDamage _)
        {
            if (__instance == null)
            {
                return true;
            }

            float spawnTime = SpawnTimeField?.GetValue(__instance) is float st ? st : 99f;
            Aircraft? aircraft = AircraftField?.GetValue(__instance) as Aircraft;
            Pilot? pilot = PilotField?.GetValue(__instance) as Pilot;
            bool notTakenOff = pilot == null || pilot.flightInfo == null || !pilot.flightInfo.HasTakenOff;

            if (aircraft != null &&
                !aircraft.disabled &&
                spawnTime < 4.0f &&
                aircraft.radarAlt < 2.5f &&
                notTakenOff &&
                _.pierceDamage <= 0f &&
                _.blastDamage <= 0f &&
                _.fireDamage <= 0f &&
                !_.detached &&
                _.hitPoints > 50f)
            {
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(
        typeof(Spawner),
        nameof(Spawner.SpawnUnit),
        new[]
        {
            typeof(UnitDefinition),
            typeof(Vector3),
            typeof(Quaternion),
            typeof(Vector3),
            typeof(Unit),
            typeof(NuclearOption.Networking.Player)
        })]
    internal static class SpawnerAirdropContainerPatch
    {
        private static void Prefix(UnitDefinition unit, out bool __state)
        {
            __state = false;
            if (!GameInteropHelpers.IsServerAuthority() || unit == null)
            {
                return;
            }

            if (AirdropCatalogManager.TryResolveCargoDef(unit, out AirdropCatalogEntry entry))
            {
                PendingMdsSpawnScope.PushUnit(
                    entry.Package,
                    entry.Spec,
                    entry.CargoDefinition,
                    isCargoContainer: true);
                __state = true;
            }
        }

        private static void Postfix(UnitDefinition unit, Unit owner, ref Unit __result)
        {
            if (!GameInteropHelpers.IsServerAuthority() ||
                __result is not Container container ||
                unit == null ||
                container.disabled)
            {
                return;
            }

            if (!AirdropCatalogManager.TryResolveCargoDef(unit, out AirdropCatalogEntry entry))
            {
                return;
            }

            if (owner != null && owner.disabled)
            {
                MdsLog.Warn($"[Airdrop] Carrier disabled before cargo spawn for '{entry.Spec.UnitId}'; rejecting container.");
                CapRejectedContainerCleaner.RejectAndDestroy(container);
                return;
            }

            if (container.GetComponent<AirdropContainerBehavior>() != null)
            {
                return;
            }

            FactionHQ? hq = container.NetworkHQ ?? container.MapHQ ?? (owner != null ? owner.NetworkHQ ?? owner.MapHQ : null);
            FactionAffinity faction = GameInteropHelpers.ToFactionAffinity(hq);

            if (AirdropDetachmentScope.IsActive)
            {
                MdsLog.Debug($"[Airdrop] Ignoring detached cargo spawn for '{entry.Spec.UnitId}' during part detachment.");
                return;
            }

            bool consumedCap;
            using (RtsBridgeHost.EnterHqScope(hq))
            {
                consumedCap = entry.Spec.Logistics == null ||
                              RtsBridgeHost.Current.Caps.TryConsumeCap(
                                  faction,
                                  entry.Spec.Logistics.CapCategory,
                                  entry.Spec.Logistics.CapWeight);
            }
            if (!consumedCap)
            {
                float refunded = 0f;
                float refund = entry.Spec.Logistics != null ? Mathf.Max(0f, entry.Spec.Logistics.CreditCost) : 0f;
                if (refund > 0f && owner is Aircraft ac && ac.Player != null)
                {
                    // Only player aircraft paid out of Player.Allocation; AI aircraft do not pay faction credits for airdrops.
                    ac.Player.AddAllocation(refund);
                    refunded = refund;
                }

                MdsLog.Warn($"[Airdrop] Unit cap full for '{entry.Spec.UnitId}' ({faction}); rejecting & removing container (refunded {refunded:0}).");
                CapRejectedContainerCleaner.RejectAndDestroy(container);
                return;
            }

            AirdropContainerBehavior behavior = container.gameObject.AddComponent<AirdropContainerBehavior>();
            behavior.ConsumedRtsCap = entry.Spec.Logistics != null;
            behavior.Initialize(entry.Package, entry.Spec, container, faction, ensureParachute: true);
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
            {
                PendingMdsSpawnScope.Pop();
            }
        }
    }

    internal sealed class CapRejectedContainerCleaner : MonoBehaviour
    {
        private Container? targetContainer;
        private bool destroyRequested;

        internal static void RejectAndDestroy(Container container)
        {
            if (container == null)
            {
                return;
            }

            Renderer[] renderers = container.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].enabled = false;
                }
            }

            Collider[] colliders = container.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                {
                    colliders[i].enabled = false;
                }
            }

            if (container.rb != null)
            {
                container.rb.detectCollisions = false;
                container.rb.isKinematic = true;
            }

            if (container.GetComponent<CapRejectedContainerCleaner>() == null)
            {
                CapRejectedContainerCleaner cleaner = container.gameObject.AddComponent<CapRejectedContainerCleaner>();
                cleaner.targetContainer = container;
            }
        }

        private void LateUpdate()
        {
            if (destroyRequested)
            {
                return;
            }

            destroyRequested = true;
            Container container = targetContainer ?? GetComponent<Container>();
            if (container == null)
            {
                Destroy(gameObject);
                return;
            }

            NetworkManagerNuclearOption? net = NetworkManagerNuclearOption.i;
            Mirage.ServerObjectManager? som = net != null ? net.ServerObjectManager : null;
            if (som != null && container.Identity != null && container.Identity.IsSpawned)
            {
                som.Destroy(container.Identity, !container.Identity.IsSceneObject);
            }
            else
            {
                Destroy(container.gameObject);
            }
        }
    }

    [HarmonyPatch(typeof(MountedCargo), "PushCargoOut", new[] { typeof(Rigidbody) })]
    internal static class MountedCargoPushCargoOutRejectedGuardPatch
    {
        private static bool Prefix(Rigidbody cargoRB)
        {
            if (cargoRB == null || cargoRB.GetComponent<CapRejectedContainerCleaner>() != null)
            {
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.MountAllowedHQ), new[] { typeof(WeaponMount), typeof(FactionHQ) })]
    internal static class WeaponCheckerAirdropFilterPatch
    {
        private static void Postfix(WeaponMount mount, FactionHQ hq, ref bool __result)
        {
            if (!__result || mount == null || hq == null)
            {
                return;
            }

            if (!AirdropCatalogManager.TryResolveMount(mount, out AirdropCatalogEntry entry))
            {
                return;
            }

            if (!MdsNetworkSync.GetEffectiveEnableNativeAircraftAirdrop())
            {
                __result = false;
                return;
            }

            FactionAffinity faction = GameInteropHelpers.ToFactionAffinity(hq);
            using (RtsBridgeHost.EnterHqScope(hq))
            {
                if (!RtsBridgeHost.Current.Economy.IsPackageAllowedForFaction(entry.Package, faction))
                {
                    __result = false;
                    return;
                }

                if (entry.Spec.Logistics != null)
                {
                    int tier = RtsBridgeHost.Current.Economy.QueryFactionTechTier(faction);
                    if (tier < entry.Spec.Logistics.RequiredTechTier)
                    {
                        __result = false;
                        return;
                    }

                    if (!RtsBridgeHost.Current.Caps.CanConsumeCap(faction, entry.Spec.Logistics.CapCategory, entry.Spec.Logistics.CapWeight, out _))
                    {
                        __result = false;
                    }
                }
            }
        }
    }

    internal static class AirdropDetachmentScope
    {
        [ThreadStatic]
        private static int depth;

        internal static bool IsActive => depth > 0;

        internal static void Enter() => depth++;

        internal static void Exit()
        {
            if (depth > 0)
            {
                depth--;
            }
        }
    }

    [HarmonyPatch(typeof(MountedCargo), "MountedCargo_OnPartDetached", new[] { typeof(UnitPart) })]
    internal static class MountedCargoPartDetachedScopePatch
    {
        private static void Prefix() => AirdropDetachmentScope.Enter();
        private static void Finalizer() => AirdropDetachmentScope.Exit();
    }

    [HarmonyPatch(typeof(WeaponManager), "StationExistsForWeaponInfo", new[] { typeof(WeaponInfo), typeof(bool), typeof(WeaponStation) }, new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
    internal static class WeaponManagerSeparateCargoStationsPatch
    {
        private static readonly FieldInfo? WeaponManagerAircraftField = AccessTools.Field(typeof(WeaponManager), "aircraft");

        private static bool Prefix(WeaponManager __instance, WeaponInfo? weaponInfo, bool cargo, out WeaponStation? existingStation, ref bool __result)
        {
            existingStation = null;
            if (!cargo || weaponInfo == null || __instance == null)
            {
                return true;
            }

            Aircraft? aircraft = WeaponManagerAircraftField?.GetValue(__instance) as Aircraft;
            if (aircraft == null || aircraft.weaponStations == null)
            {
                return true;
            }

            List<WeaponStation> stations = aircraft.weaponStations;
            for (int i = 0; i < stations.Count; i++)
            {
                WeaponStation? station = stations[i];
                if (station != null && station.Cargo && ReferenceEquals(station.WeaponInfo, weaponInfo))
                {
                    existingStation = station;
                    __result = true;
                    return false;
                }
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.VetLoadout), new[] { typeof(AircraftDefinition), typeof(Loadout), typeof(Player), typeof(Airbase), typeof(INetworkPlayer) })]
    internal static class WeaponCheckerVetLoadoutCapPatch
    {
        private static void Postfix(AircraftDefinition? definition, Loadout? requestedLoadout, Airbase? airbase, INetworkPlayer? sender)
        {
            if (!MdsNetworkSync.GetEffectiveEnableNativeAircraftAirdrop() ||
                definition == null ||
                definition.unitPrefab == null ||
                requestedLoadout == null ||
                requestedLoadout.weapons == null ||
                airbase == null)
            {
                return;
            }

            FactionHQ? hq = airbase.CurrentHQ;
            if (hq == null)
            {
                return;
            }

            Aircraft? acPrefab = definition.unitPrefab.GetComponent<Aircraft>();
            WeaponManager? wm = acPrefab != null ? acPrefab.weaponManager : null;
            if (wm == null || wm.hardpointSets == null)
            {
                return;
            }

            FactionAffinity faction = GameInteropHelpers.ToFactionAffinity(hq);
            var cumulativeByCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int limit = Math.Min(requestedLoadout.weapons.Count, wm.hardpointSets.Length);

            using (RtsBridgeHost.EnterHqScope(hq))
            {
                for (int i = 0; i < limit; i++)
                {
                    WeaponMount? mount = requestedLoadout.weapons[i];
                    if (mount == null || !AirdropCatalogManager.TryResolveMount(mount, out AirdropCatalogEntry entry))
                    {
                        continue;
                    }

                    if (entry.Spec.Logistics == null || entry.Spec.Logistics.CapWeight <= 0)
                    {
                        continue;
                    }

                    int hpCount = wm.hardpointSets[i]?.hardpoints != null
                        ? Math.Max(1, wm.hardpointSets[i].hardpoints.Count)
                        : 1;
                    int addedWeight = entry.Spec.Logistics.CapWeight * hpCount;
                    string cat = string.IsNullOrEmpty(entry.Spec.Logistics.CapCategory) ? "Ground" : entry.Spec.Logistics.CapCategory;
                    cumulativeByCategory.TryGetValue(cat, out int priorWeight);
                    int nextTotal = priorWeight + addedWeight;

                    if (!RtsBridgeHost.Current.Caps.CanConsumeCap(faction, cat, nextTotal, out _))
                    {
                        requestedLoadout.weapons[i] = null;
                        sender?.SetError(1, NuclearOptionPlayerErrorFlags.InvalidLoadout);
                    }
                    else
                    {
                        cumulativeByCategory[cat] = nextTotal;
                    }
                }
            }
        }
    }
}
