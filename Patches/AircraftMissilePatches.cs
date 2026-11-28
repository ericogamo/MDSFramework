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
            internal string ExpectedPrefabName;
            internal float ExpireTime;
        }

        private static readonly List<PendingAirLaunch> Pending = new List<PendingAirLaunch>(16);

        internal static void Clear()
        {
            Pending.Clear();
        }

        internal static void RecordFire(Unit owner, MountedMissile mounted, AirMissileCatalogEntry entry)
        {
            if (!GameInteropHelpers.IsServerAuthority() ||
                owner == null ||
                mounted == null ||
                !mounted.gameObject.activeInHierarchy)
            {
                return;
            }

            PurgeExpired();
            RemoveForMounted(mounted);
            string expectedPrefab = entry.ClonedInfo?.weaponPrefab != null ? entry.ClonedInfo.weaponPrefab.name : string.Empty;
            Pending.Add(new PendingAirLaunch
            {
                Owner = owner,
                Mounted = mounted,
                Entry = entry,
                ExpectedPrefabName = expectedPrefab,
                ExpireTime = Time.unscaledTime + 12f
            });
        }

        internal static void RemoveForMounted(MountedMissile? mounted)
        {
            if (mounted == null)
            {
                return;
            }

            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(Pending[i].Mounted, mounted))
                {
                    Pending.RemoveAt(i);
                }
            }
        }

        internal static bool TryConsume(Unit? owner, GameObject? missilePrefab, Vector3 spawnPos, out AirMissileCatalogEntry entry)
        {
            PurgeExpired();
            const float maxDistSq = 250f * 250f;
            int bestIdx = -1;
            float bestDistSq = maxDistSq;
            string prefabName = missilePrefab != null ? missilePrefab.name : string.Empty;

            for (int i = 0; i < Pending.Count; i++)
            {
                PendingAirLaunch item = Pending[i];
                if (item.Mounted == null || !item.Mounted.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (owner != null && !ReferenceEquals(item.Owner, owner))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(item.ExpectedPrefabName) &&
                    !string.IsNullOrEmpty(prefabName) &&
                    !string.Equals(item.ExpectedPrefabName, prefabName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Vector3 refPos = item.Mounted.transform.position;
                float dSq = (refPos - spawnPos).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
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
                PendingAirLaunch item = Pending[i];
                if (item.ExpireTime < now ||
                    item.Owner == null ||
                    item.Mounted == null ||
                    !item.Mounted.gameObject.activeInHierarchy)
                {
                    Pending.RemoveAt(i);
                }
            }
        }
    }

    internal sealed class MountedMissileLaunchCleanup : MonoBehaviour
    {
        private MountedMissile? mounted;

        private void Awake()
        {
            mounted = GetComponent<MountedMissile>();
        }

        private void OnDisable()
        {
            AirMissileLaunchTracker.RemoveForMounted(mounted ?? GetComponent<MountedMissile>());
        }

        private void OnDestroy()
        {
            AirMissileLaunchTracker.RemoveForMounted(mounted);
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
        private struct PendingFireState
        {
            internal AirMissileCatalogEntry? Entry;
            internal bool WasFired;
        }

        private static readonly FieldInfo? MountedMissileFiredField = AccessTools.Field(typeof(MountedMissile), "fired");

        private static bool Prefix(MountedMissile __instance, Unit owner, Unit target, out PendingFireState __state)
        {
            __state = default;
            if (__instance == null || owner == null)
            {
                return true;
            }

            WeaponMount? resolvedMount = GameInteropHelpers.MountedMissileMountField?.GetValue(__instance) as WeaponMount;
            if (AircraftMissileCatalogManager.TryResolveMount(resolvedMount, out AirMissileCatalogEntry entry) ||
                AircraftMissileCatalogManager.TryResolveInfo(__instance.info, out entry))
            {
                if (entry.Spec.IsAntiShip && !AntiShipTargetingPatches.IsTargetAllowedForLaunch(owner, target))
                {
                    bool isLocalOrSingleplayer = owner.HasAuthority || GameManager.gameState == GameState.SinglePlayer;
                    if (isLocalOrSingleplayer)
                    {
                        string weaponName = string.IsNullOrEmpty(entry.ClonedInfo.shortName)
                            ? entry.Spec.DisplayName
                            : entry.ClonedInfo.shortName;
                        AntiShipTargetingPatches.ReportShipsOnlyIfLocalPlayer(owner, weaponName);
                    }

                    return false;
                }

                bool wasFired = MountedMissileFiredField?.GetValue(__instance) is bool f && f;
                __state = new PendingFireState
                {
                    Entry = entry,
                    WasFired = wasFired
                };
            }

            return true;
        }

        private static void Postfix(MountedMissile __instance, Unit owner, bool __runOriginal, PendingFireState __state)
        {
            if (!__runOriginal ||
                __state.Entry == null ||
                __state.WasFired ||
                __instance == null ||
                owner == null ||
                !GameInteropHelpers.IsServerAuthority())
            {
                return;
            }

            bool firedNow = MountedMissileFiredField?.GetValue(__instance) is bool f && f;
            if (!firedNow || !__instance.IsAttached() || !__instance.gameObject.activeInHierarchy)
            {
                return;
            }

            if (__instance.GetComponent<MountedMissileLaunchCleanup>() == null)
            {
                __instance.gameObject.AddComponent<MountedMissileLaunchCleanup>();
            }

            AirMissileLaunchTracker.RecordFire(owner, __instance, __state.Entry);
        }
    }

    [HarmonyPatch(typeof(MountedMissile), "OnDestroy")]
    internal static class MountedMissileOnDestroyCleanupPatch
    {
        private static void Prefix(MountedMissile __instance)
        {
            AirMissileLaunchTracker.RemoveForMounted(__instance);
        }
    }

    [HarmonyPatch(
        typeof(Spawner),
        nameof(Spawner.SpawnMissile),
        new[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Unit), typeof(Unit) })]
    internal static class SpawnerSpawnAirMissilePatch
    {
        private static void Prefix(GameObject missile, Vector3 launchPosition, Unit owner, out AirMissileCatalogEntry? __state)
        {
            __state = null;
            if (!GameInteropHelpers.IsServerAuthority() || owner == null)
            {
                return;
            }

            if (AirMissileLaunchTracker.TryConsume(owner, missile, launchPosition, out AirMissileCatalogEntry entry))
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
            using (RtsBridgeHost.EnterHqScope(hq))
            {
                if (!RtsBridgeHost.Current.Economy.IsPackageAllowedForFaction(entry.Package, faction))
                {
                    __result = false;
                }
            }
        }
    }
}
