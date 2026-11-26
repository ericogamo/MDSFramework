using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Deployment;
using MDSFramework.Missiles;
using UnityEngine;

namespace MDSFramework.Patches
{
    internal static class AirMissileLaunchTracker
    {
        private struct PendingAirLaunch
        {
            internal Unit Owner;
            internal MountedMissile Mounted;
            internal AirMissileCatalogEntry Entry;
            internal float ExpireTime;
        }

        private static readonly List<PendingAirLaunch> Pending = new List<PendingAirLaunch>(16);

        internal static void RecordFire(Unit owner, MountedMissile mounted, AirMissileCatalogEntry entry)
        {
            PurgeExpired();
            Pending.Add(new PendingAirLaunch
            {
                Owner = owner,
                Mounted = mounted,
                Entry = entry,
                ExpireTime = Time.unscaledTime + 12f
            });
        }

        internal static bool TryConsume(Unit? owner, Vector3 spawnPos, out AirMissileCatalogEntry entry)
        {
            PurgeExpired();
            int bestIdx = -1;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < Pending.Count; i++)
            {
                PendingAirLaunch item = Pending[i];
                if (owner != null && !ReferenceEquals(item.Owner, owner))
                {
                    continue;
                }

                if (item.Mounted != null)
                {
                    float dSq = (item.Mounted.transform.position - spawnPos).sqrMagnitude;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestIdx = i;
                    }
                }
                else if (bestIdx < 0)
                {
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0)
            {
                entry = Pending[bestIdx].Entry;
                Pending.RemoveAt(bestIdx);
                return true;
            }

            entry = null!;
            return false;
        }

        private static void PurgeExpired()
        {
            float now = Time.unscaledTime;
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if (Pending[i].ExpireTime < now || Pending[i].Owner == null)
                {
                    Pending.RemoveAt(i);
                }
            }
        }
    }

    [HarmonyPatch(
        typeof(MountedMissile),
        nameof(MountedMissile.AttachToHardpoint),
        new[] { typeof(Aircraft), typeof(Hardpoint), typeof(WeaponMount) })]
    internal static class MountedMissileAttachSeaWraithPatch
    {
        private static void Postfix(MountedMissile __instance, WeaponMount weaponMount)
        {
            if (__instance == null || !MdsConfig.EnableCustomGlbVisuals.Value)
            {
                return;
            }

            WeaponMount? resolvedMount = weaponMount ?? (GameInteropHelpers.MountedMissileMountField?.GetValue(__instance) as WeaponMount);
            AirMissileCatalogEntry entry;
            if (!AircraftMissileCatalogManager.TryResolveMount(resolvedMount, out entry) &&
                !AircraftMissileCatalogManager.TryResolveInfo(__instance.info, out entry))
            {
                return;
            }

            if (__instance.transform.Find($"MDS_Rig_{entry.Spec.MissileId}") != null)
            {
                return;
            }

            MeshRenderer[] existingMeshes = __instance.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < existingMeshes.Length; i++)
            {
                if (existingMeshes[i] != null)
                {
                    existingMeshes[i].enabled = false;
                }
            }

            SkinnedMeshRenderer[] existingSkinned = __instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < existingSkinned.Length; i++)
            {
                if (existingSkinned[i] != null)
                {
                    existingSkinned[i].enabled = false;
                }
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
                entry.Spec.MissileId,
                glbPath,
                entry.Spec.FallbackMeshArchetype,
                decalPath,
                __instance.transform,
                entry.Spec.ModelYawOffsetDeg);

            if (rig != null)
            {
                Collider[] rigCols = rig.RootObject.GetComponentsInChildren<Collider>(true);
                for (int c = 0; c < rigCols.Length; c++)
                {
                    if (rigCols[c] != null)
                    {
                        UnityEngine.Object.Destroy(rigCols[c]);
                    }
                }

                GlbKeyframedAnimator anim = rig.RootObject.GetComponent<GlbKeyframedAnimator>()
                    ?? rig.RootObject.AddComponent<GlbKeyframedAnimator>();
                anim.Initialize(rig, desiredDurationSeconds: 0.75f, startDeployed: false);
            }
        }
    }

    [HarmonyPatch(
        typeof(MountedMissile),
        nameof(MountedMissile.Fire),
        new[] { typeof(Unit), typeof(Unit), typeof(Vector3), typeof(WeaponStation), typeof(GlobalPosition) })]
    internal static class MountedMissileFireSeaWraithPatch
    {
        private static bool Prefix(MountedMissile __instance, Unit owner, Unit target)
        {
            if (__instance == null || owner == null)
            {
                return true;
            }

            WeaponMount? resolvedMount = GameInteropHelpers.MountedMissileMountField?.GetValue(__instance) as WeaponMount;
            if (AircraftMissileCatalogManager.TryResolveMount(resolvedMount, out AirMissileCatalogEntry entry) ||
                AircraftMissileCatalogManager.TryResolveInfo(__instance.info, out entry))
            {
                bool enforceShipOnly = entry.Spec.IsAntiShip &&
                    (owner.IsServer || owner.HasAuthority || GameManager.gameState == GameState.SinglePlayer);
                if (enforceShipOnly && !AntiShipTargetingPatches.IsValidAntiShipTarget(target))
                {
                    string weaponName = string.IsNullOrEmpty(entry.ClonedInfo.shortName)
                        ? entry.Spec.DisplayName
                        : entry.ClonedInfo.shortName;
                    AntiShipTargetingPatches.ReportShipsOnlyIfLocalPlayer(owner, weaponName);
                    return false;
                }

                AirMissileLaunchTracker.RecordFire(owner, __instance, entry);
            }

            return true;
        }
    }

    [HarmonyPatch(
        typeof(Spawner),
        nameof(Spawner.SpawnMissile),
        new[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Unit), typeof(Unit) })]
    internal static class SpawnerSpawnAirMissilePatch
    {
        private static void Prefix(Vector3 launchPosition, Unit owner, out AirMissileCatalogEntry? __state)
        {
            __state = null;
            if (!GameInteropHelpers.IsServerAuthority() || owner == null)
            {
                return;
            }

            if (AirMissileLaunchTracker.TryConsume(owner, launchPosition, out AirMissileCatalogEntry entry))
            {
                PendingMdsSpawnScope.PushMissile(entry.Package, entry.Spec, entry.MissileDefinition);
                __state = entry;
            }
        }

        private static void Postfix(Unit target, Unit owner, Missile __result, AirMissileCatalogEntry? __state)
        {
            if (__state == null || __result == null || !GameInteropHelpers.IsServerAuthority())
            {
                return;
            }

            __result.definition = __state.MissileDefinition;
            __result.NetworkunitName = __state.Spec.DisplayName;

            if (__result.GetComponent<CustomMissileBehavior>() == null)
            {
                FactionAffinity ownerAffinity = GameInteropHelpers.ToFactionAffinity(
                    owner != null ? (owner.NetworkHQ ?? owner.MapHQ) : null);
                CustomMissileBehavior beh = __result.gameObject.AddComponent<CustomMissileBehavior>();
                beh.Initialize(
                    __state.Package,
                    __state.Spec,
                    __result,
                    target,
                    ownerAffinity,
                    startPassiveHoj: false,
                    verticalLaunch: false,
                    isAirDropped: owner is Aircraft);
            }
        }

        private static void Finalizer(AirMissileCatalogEntry? __state)
        {
            if (__state != null)
            {
                PendingMdsSpawnScope.Pop();
            }
        }
    }

    [HarmonyPatch]
    internal static class HardpointPylonMatchesMountPatch
    {
        private static MethodBase? TargetMethod()
        {
            Type? pylonType = typeof(Hardpoint).GetNestedType("HardpointPylon", BindingFlags.NonPublic | BindingFlags.Public);
            return pylonType?.GetMethod(
                "MatchesMount",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(WeaponMount) },
                null);
        }

        private static void Postfix(object __instance, WeaponMount mount, ref bool __result)
        {
            if (__result || __instance == null || mount == null)
            {
                return;
            }

            if (!AircraftMissileCatalogManager.TryResolveMount(mount, out AirMissileCatalogEntry entry))
            {
                return;
            }

            WeaponMount? pylonMount = GameInteropHelpers.HardpointPylonMountField?.GetValue(__instance) as WeaponMount;
            if (pylonMount != null && ReferenceEquals(pylonMount, entry.SourceMount))
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.MountAllowedHQ), new[] { typeof(WeaponMount), typeof(FactionHQ) })]
    internal static class WeaponCheckerAirMissileFilterPatch
    {
        private static void Postfix(WeaponMount mount, FactionHQ hq, ref bool __result)
        {
            if (!__result || mount == null || hq == null)
            {
                return;
            }

            if (!AircraftMissileCatalogManager.TryResolveMount(mount, out AirMissileCatalogEntry entry))
            {
                return;
            }

            FactionAffinity faction = GameInteropHelpers.ToFactionAffinity(hq);
            if (!RtsBridgeHost.Current.Economy.IsPackageAllowedForFaction(entry.Package, faction))
            {
                __result = false;
            }
        }
    }
}
