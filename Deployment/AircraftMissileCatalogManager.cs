using System;
using System.Collections.Generic;
using System.Reflection;
using MDSFramework.Api;
using MDSFramework.Missiles;
using UnityEngine;

namespace MDSFramework.Deployment
{
    internal sealed class AirMissileCatalogEntry
    {
        internal ContractorPackage Package = null!;
        internal CustomMissileSpec Spec = null!;
        internal MissileDefinition MissileDefinition = null!;
        internal WeaponMount SourceMount = null!;
        internal WeaponMount ClonedMount = null!;
        internal WeaponInfo ClonedInfo = null!;
    }

    internal static class AircraftMissileCatalogManager
    {
        private static readonly object SyncLock = new object();
        private static readonly Dictionary<WeaponMount, AirMissileCatalogEntry> EntriesByMount = new Dictionary<WeaponMount, AirMissileCatalogEntry>();
        private static readonly Dictionary<WeaponInfo, AirMissileCatalogEntry> EntriesByInfo = new Dictionary<WeaponInfo, AirMissileCatalogEntry>();
        private static readonly Dictionary<string, AirMissileCatalogEntry> EntriesByKey = new Dictionary<string, AirMissileCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        private static GameObject? mountPrefabHolder;
        private static bool subscribed;

        internal static int RegisteredMountCount { get { lock (SyncLock) { return EntriesByMount.Count; } } }
        internal static int InjectedHardpointCount { get; private set; }

        internal static void EnsureInitializedSubscription()
        {
            if (subscribed)
            {
                return;
            }

            subscribed = true;
            ContractorRegistry.OnPackageRegistered += OnPackageRegistered;
        }

        internal static void EnsureRegisteredAll()
        {
            EnsureInitializedSubscription();

            if (Encyclopedia.i == null || Encyclopedia.i.weaponMounts == null)
            {
                return;
            }

            Encyclopedia enc = Encyclopedia.i;
            int newlyRegistered = 0;

            lock (SyncLock)
            {
                IReadOnlyCollection<ContractorPackage> packages = ContractorRegistry.GetPackages();
                var sortedPackages = new List<ContractorPackage>(packages);
                sortedPackages.Sort((a, b) => string.Compare(a?.PackageId, b?.PackageId, StringComparison.OrdinalIgnoreCase));

                for (int p = 0; p < sortedPackages.Count; p++)
                {
                    ContractorPackage pkg = sortedPackages[p];
                    if (pkg == null || pkg.Missiles == null)
                    {
                        continue;
                    }

                    for (int m = 0; m < pkg.Missiles.Count; m++)
                    {
                        CustomMissileSpec spec = pkg.Missiles[m];
                        if (spec == null ||
                            !spec.EnableAircraftMounting ||
                            string.IsNullOrWhiteSpace(spec.MissileId) ||
                            spec.AircraftMountCloneKeys == null ||
                            spec.AircraftMountCloneKeys.Length == 0)
                        {
                            continue;
                        }

                        newlyRegistered += RegisterAircraftMountsForMissileLocked(enc, pkg, spec);
                    }
                }
            }

            if (newlyRegistered > 0)
            {
                MdsEncyclopediaCatalog.InvalidateMissionEditorCache();
                MdsLog.Info($"[AirWeapons] Registered {RegisteredMountCount} aircraft missile mounts across {InjectedHardpointCount} hardpoint sets.");
            }
        }

        internal static bool TryResolveMount(WeaponMount? mount, out AirMissileCatalogEntry entry)
        {
            if (mount == null)
            {
                entry = null!;
                return false;
            }

            lock (SyncLock)
            {
                if (EntriesByMount.TryGetValue(mount, out entry!))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(mount.jsonKey) && EntriesByKey.TryGetValue(mount.jsonKey, out entry!))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(mount.name) && EntriesByKey.TryGetValue(mount.name, out entry!))
                {
                    return true;
                }
            }

            entry = null!;
            return false;
        }

        internal static bool TryResolveInfo(WeaponInfo? info, out AirMissileCatalogEntry entry)
        {
            if (info == null)
            {
                entry = null!;
                return false;
            }

            lock (SyncLock)
            {
                return EntriesByInfo.TryGetValue(info, out entry!);
            }
        }

        private static void OnPackageRegistered(ContractorPackage _)
        {
            EnsureRegisteredAll();
        }

        private static int RegisterAircraftMountsForMissileLocked(
            Encyclopedia enc,
            ContractorPackage pkg,
            CustomMissileSpec spec)
        {
            if (!MdsEncyclopediaCatalog.TryGetMissileDefinition(spec.MissileId, out MissileDefinition customMissileDef) ||
                customMissileDef == null)
            {
                MissileDefinition? fallbackDef = GameInteropHelpers.ResolveEncyclopediaMissileDef(
                    spec.BaseMissileCloneKey,
                    preferBallisticOrSam: false);
                if (fallbackDef == null)
                {
                    return 0;
                }

                customMissileDef = fallbackDef;
            }

            var matchedSourceMounts = new List<WeaponMount>();
            for (int i = 0; i < enc.weaponMounts.Count; i++)
            {
                WeaponMount? wm = enc.weaponMounts[i];
                if (wm == null || wm.info == null || EntriesByMount.ContainsKey(wm))
                {
                    continue;
                }

                if (MatchesAnyCloneKey(wm, spec.AircraftMountCloneKeys))
                {
                    matchedSourceMounts.Add(wm);
                }
            }

            if (matchedSourceMounts.Count == 0)
            {
                return 0;
            }

            int added = 0;
            for (int i = 0; i < matchedSourceMounts.Count; i++)
            {
                WeaponMount srcMount = matchedSourceMounts[i];
                string suffix = !string.IsNullOrEmpty(srcMount.jsonKey) ? srcMount.jsonKey : srcMount.name;
                string clonedKey = $"mds_air_{spec.MissileId}_{suffix}";
                if (EntriesByKey.ContainsKey(clonedKey))
                {
                    continue;
                }

                AirMissileCatalogEntry entry = CreateClonedMountEntry(enc, pkg, spec, customMissileDef, srcMount, clonedKey);
                EntriesByMount[entry.ClonedMount] = entry;
                EntriesByInfo[entry.ClonedInfo] = entry;
                EntriesByKey[clonedKey] = entry;
                if (!string.IsNullOrEmpty(entry.ClonedMount.name))
                {
                    EntriesByKey[entry.ClonedMount.name] = entry;
                }

                RegisterWeaponMountInEncyclopedia(enc, entry.ClonedMount, clonedKey);
                InjectIntoCompatibleAircraft(enc, srcMount, entry.ClonedMount);
                added++;
            }

            return added;
        }

        private static bool MatchesAnyCloneKey(WeaponMount wm, string[] cloneKeys)
        {
            string key = wm.jsonKey ?? string.Empty;
            string name = wm.name ?? string.Empty;
            for (int k = 0; k < cloneKeys.Length; k++)
            {
                string pat = cloneKeys[k];
                if (string.IsNullOrWhiteSpace(pat))
                {
                    continue;
                }

                if (key.StartsWith(pat, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(pat, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static AirMissileCatalogEntry CreateClonedMountEntry(
            Encyclopedia enc,
            ContractorPackage pkg,
            CustomMissileSpec spec,
            MissileDefinition customMissileDef,
            WeaponMount srcMount,
            string clonedKey)
        {
            MissileDefinition? vanillaDef = GameInteropHelpers.ResolveEncyclopediaMissileDef(
                spec.BaseMissileCloneKey,
                preferBallisticOrSam: false);

            WeaponInfo clonedInfo = UnityEngine.Object.Instantiate(srcMount.info);
            clonedInfo.hideFlags = HideFlags.DontUnloadUnusedAsset;
            clonedInfo.name = $"mds_air_info_{spec.MissileId}_{srcMount.name}";

            // Force canonical base cruise prefab (e.g. AShM2 OpticalSeekerCruiseMissile) so all air mounts share valid networking and seeker routing.
            if (vanillaDef != null && vanillaDef.unitPrefab != null)
            {
                clonedInfo.weaponPrefab = vanillaDef.unitPrefab;
            }

            if (spec.IsAntiShip)
            {
                RoleIdentity eff = clonedInfo.effectiveness;
                eff.antiAir = 0f;
                eff.antiMissile = 0f;
                eff.antiRadar = 0f;
                eff.antiSurface = 0.96f;
                clonedInfo.effectiveness = eff;
            }

            string shortName = !string.IsNullOrWhiteSpace(spec.AircraftMountShortName)
                ? spec.AircraftMountShortName
                : spec.DisplayName;
            clonedInfo.weaponName = spec.DisplayName;
            clonedInfo.shortName = shortName;
            clonedInfo.description = $"{pkg.DisplayName} | Mach {Mathf.Max(0.90f, spec.TargetMach):0.00} {Mathf.Max(2.5f, spec.SeaSkimAltitudeMeters):0.#}m Sea-Skim AShM | " +
                                     $"Active ECCM/ECM + Evasive | {spec.MaxEngagementRangeKm:0.#} km.";
            clonedInfo.costPerRound = Mathf.Max(0.5f, spec.RearmCostPerRound);
            clonedInfo.massPerRound = Mathf.Max(80f, spec.AircraftMountMassKg);
            clonedInfo.overHorizon = true;
            clonedInfo.muzzleVelocity = spec.TargetMach > 0f ? spec.TargetMach * 340.3f : Mathf.Max(280f, spec.CruiseSpeedMps);
            clonedInfo.maxSpeed = clonedInfo.muzzleVelocity;
            clonedInfo.blastDamage = Mathf.Max(10f, spec.BlastYield);

            TargetRequirements tr = clonedInfo.targetRequirements;
            tr.maxRange = Mathf.Max(10000f, spec.MaxEngagementRangeKm * 1000f);
            clonedInfo.targetRequirements = tr;

            Sprite? customIcon = MdsWeaponStationBuilder.TryLoadWeaponIconSprite(pkg, spec.IconRelativePath);
            if (customIcon != null)
            {
                clonedInfo.weaponIcon = customIcon;
            }

            GameObject? mountPrefab = srcMount.prefab;
            if (srcMount.prefab != null)
            {
                GameObject holder = EnsureMountPrefabHolder();
                GameObject clonedPrefab = UnityEngine.Object.Instantiate(srcMount.prefab, holder.transform);
                clonedPrefab.name = $"mds_air_prefab_{clonedKey}";
                clonedPrefab.SetActive(true);
                clonedPrefab.hideFlags = HideFlags.DontUnloadUnusedAsset;

                MountedMissile[] missiles = clonedPrefab.GetComponentsInChildren<MountedMissile>(true);
                for (int i = 0; i < missiles.Length; i++)
                {
                    if (missiles[i] != null)
                    {
                        missiles[i].info = clonedInfo;
                    }
                }

                mountPrefab = clonedPrefab;
            }

            WeaponMount clonedMount = UnityEngine.Object.Instantiate(srcMount);
            clonedMount.hideFlags = HideFlags.DontUnloadUnusedAsset;
            clonedMount.name = clonedKey;
            clonedMount.jsonKey = clonedKey;
            clonedMount.info = clonedInfo;
            clonedMount.prefab = mountPrefab;
            clonedMount.dontAutomaticallyAddToEncyclopedia = true;

            int ammoCount = Math.Max(1, clonedMount.ammo);
            if (mountPrefab != null)
            {
                MountedMissile[] missiles = mountPrefab.GetComponentsInChildren<MountedMissile>(true);
                if (missiles.Length > 0)
                {
                    ammoCount = missiles.Length;
                }
            }

            clonedMount.ammo = ammoCount;
            clonedMount.mountName = ammoCount > 1 ? $"{shortName} x{ammoCount}" : shortName;
            clonedMount.mass = clonedMount.emptyMass + (clonedInfo.massPerRound * ammoCount);
            clonedMount.RCS = Mathf.Max(0f, clonedMount.emptyRCS + (Mathf.Max(0.02f, spec.StealthRcsOverride) * 0.2f * ammoCount));

            GameInteropHelpers.WeaponMountDisabledField?.SetValue(clonedMount, false);
            GameInteropHelpers.WeaponMountEventField?.SetValue(clonedMount, false);

            if (spec.IsAntiShip)
            {
                ContractorRegistry.RegisterAntiShipWeaponInfo(clonedInfo);
            }

            _ = enc;
            return new AirMissileCatalogEntry
            {
                Package = pkg,
                Spec = spec,
                MissileDefinition = customMissileDef,
                SourceMount = srcMount,
                ClonedMount = clonedMount,
                ClonedInfo = clonedInfo
            };
        }

        private static GameObject EnsureMountPrefabHolder()
        {
            if (mountPrefabHolder == null)
            {
                mountPrefabHolder = new GameObject("MDS_AirWeaponMountPrefabHolder");
                mountPrefabHolder.SetActive(false);
                mountPrefabHolder.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(mountPrefabHolder);
            }

            return mountPrefabHolder;
        }

        private static void RegisterWeaponMountInEncyclopedia(Encyclopedia enc, WeaponMount mount, string key)
        {
            if (enc.weaponMounts != null && !enc.weaponMounts.Contains(mount))
            {
                enc.weaponMounts.Add(mount);
            }

            if (Encyclopedia.WeaponLookup != null)
            {
                Encyclopedia.WeaponLookup[key] = mount;
            }

            if (enc.IndexLookup != null && mount is INetworkDefinition netDef && !enc.IndexLookup.Contains(netDef))
            {
                netDef.LookupIndex = enc.IndexLookup.Count;
                enc.IndexLookup.Add(netDef);
            }
        }

        private static void InjectIntoCompatibleAircraft(Encyclopedia enc, WeaponMount srcMount, WeaponMount clonedMount)
        {
            if (enc.aircraft == null)
            {
                return;
            }

            for (int a = 0; a < enc.aircraft.Count; a++)
            {
                AircraftDefinition? acDef = enc.aircraft[a];
                if (acDef == null || acDef.unitPrefab == null)
                {
                    continue;
                }

                Aircraft? ac = acDef.unitPrefab.GetComponent<Aircraft>();
                if (ac == null || ac.weaponManager == null || ac.weaponManager.hardpointSets == null)
                {
                    continue;
                }

                HardpointSet[] sets = ac.weaponManager.hardpointSets;
                for (int s = 0; s < sets.Length; s++)
                {
                    HardpointSet set = sets[s];
                    if (set == null || set.weaponOptions == null)
                    {
                        continue;
                    }

                    if (set.weaponOptions.Contains(srcMount) && !set.weaponOptions.Contains(clonedMount))
                    {
                        set.weaponOptions.Add(clonedMount);
                        InjectedHardpointCount++;
                    }
                }
            }
        }
    }
}
