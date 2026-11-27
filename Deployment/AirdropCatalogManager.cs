using System;
using System.Collections.Generic;
using MDSFramework.Api;
using UnityEngine;

namespace MDSFramework.Deployment
{
    internal sealed class AirdropCatalogEntry
    {
        internal ContractorPackage Package = null!;
        internal ModularUnitSpec Spec = null!;
        internal UnitDefinition CargoDefinition = null!;
        internal WeaponMount WeaponMount = null!;
        internal WeaponInfo WeaponInfo = null!;
    }

    internal static class AirdropCatalogManager
    {
        private static readonly object SyncLock = new object();
        private static readonly Dictionary<WeaponMount, AirdropCatalogEntry> EntriesByMount = new Dictionary<WeaponMount, AirdropCatalogEntry>();
        private static readonly Dictionary<UnitDefinition, AirdropCatalogEntry> EntriesByCargoDef = new Dictionary<UnitDefinition, AirdropCatalogEntry>();
        private static readonly Dictionary<string, AirdropCatalogEntry> EntriesByUnitId = new Dictionary<string, AirdropCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AirdropCatalogEntry> EntriesByKey = new Dictionary<string, AirdropCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        private static WeaponMount? cachedTemplateMount;
        private static UnitDefinition? cachedTemplateCargoDef;
        private static GameObject? mountPrefabHolder;

        internal static int RegisteredMountCount { get; private set; }
        internal static int InjectedAircraftCount { get; private set; }
        internal static bool NoTemplateAvailable { get; private set; }

        internal static void Initialize()
        {
            MdsCatalogCoordinator.Initialize();
        }

        internal static void Dispose()
        {
        }

        internal static void EnsureRegisteredAll()
        {
            MdsCatalogCoordinator.EnsureAllRegisteredCanonical();
        }

        internal static int EnsureRegisteredAllInternal(Encyclopedia enc)
        {
            if (enc == null)
            {
                return 0;
            }

            lock (SyncLock)
            {
                if (!TryResolveContainerTemplate(enc, out WeaponMount? templateMount, out UnitDefinition? templateCargoDef) ||
                    templateMount == null || templateCargoDef == null)
                {
                    NoTemplateAvailable = true;
                    return 0;
                }

                NoTemplateAvailable = false;
                IReadOnlyCollection<ContractorPackage> packages = ContractorRegistry.GetPackages();
                var sortedPackages = new List<ContractorPackage>(packages);
                sortedPackages.Sort((a, b) => string.Compare(a?.PackageId, b?.PackageId, StringComparison.OrdinalIgnoreCase));

                int newlyRegistered = 0;

                for (int p = 0; p < sortedPackages.Count; p++)
                {
                    ContractorPackage pkg = sortedPackages[p];
                    if (pkg == null || pkg.Units == null)
                    {
                        continue;
                    }

                    var sortedUnits = new List<ModularUnitSpec>(pkg.Units);
                    sortedUnits.Sort((a, b) => string.Compare(a?.UnitId, b?.UnitId, StringComparison.OrdinalIgnoreCase));

                    for (int i = 0; i < sortedUnits.Count; i++)
                    {
                        ModularUnitSpec spec = sortedUnits[i];
                        if (spec == null || !spec.Logistics.SupportsAircraftAirdrop)
                        {
                            continue;
                        }

                        if (EntriesByUnitId.TryGetValue(spec.UnitId, out AirdropCatalogEntry existing))
                        {
                            RegisterDefinitionInEncyclopedia(enc, existing.CargoDefinition, $"mds_cargo_{spec.UnitId}");
                            RegisterMountInEncyclopedia(enc, existing.WeaponMount, $"mds_airdrop_{spec.UnitId}");
                            continue;
                        }

                        if (RegisterUnitEntryLocked(enc, pkg, spec, templateMount, templateCargoDef))
                        {
                            newlyRegistered++;
                        }
                    }
                }

                InjectedAircraftCount = InjectAllEntriesIntoAircraftLocked(enc);

                if (newlyRegistered > 0)
                {
                    MdsLog.Info(
                        $"[Airdrop] Registered {newlyRegistered} airdrop container mounts (total={RegisteredMountCount}, aircraft={InjectedAircraftCount}).");
                }

                return newlyRegistered;
            }
        }

        internal static bool TryGetEntryByUnitId(string unitId, out AirdropCatalogEntry entry)
        {
            lock (SyncLock)
            {
                return EntriesByUnitId.TryGetValue(unitId, out entry!);
            }
        }

        internal static bool TryResolveMount(WeaponMount? mount, out AirdropCatalogEntry entry)
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

        internal static bool TryResolveCargoDef(UnitDefinition? unitDef, out AirdropCatalogEntry entry)
        {
            if (unitDef == null)
            {
                entry = null!;
                return false;
            }

            lock (SyncLock)
            {
                if (EntriesByCargoDef.TryGetValue(unitDef, out entry!))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(unitDef.jsonKey) && EntriesByKey.TryGetValue(unitDef.jsonKey, out entry!))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(unitDef.name) && EntriesByKey.TryGetValue(unitDef.name, out entry!))
                {
                    return true;
                }
            }

            entry = null!;
            return false;
        }

        internal static bool TryResolveCargoKey(string? key, out AirdropCatalogEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(key) &&
                key!.StartsWith("mds_cargo_", StringComparison.OrdinalIgnoreCase))
            {
                lock (SyncLock)
                {
                    return EntriesByKey.TryGetValue(key, out entry!);
                }
            }

            entry = null!;
            return false;
        }

        internal static UnitDefinition? GetTemplateCargoDefinition()
        {
            if (cachedTemplateCargoDef != null)
            {
                return cachedTemplateCargoDef;
            }

            if (Encyclopedia.i != null && TryResolveContainerTemplate(Encyclopedia.i, out _, out UnitDefinition? tpl))
            {
                return tpl;
            }

            return null;
        }

        private static void OnPackageRegistered(ContractorPackage _)
        {
            MdsCatalogCoordinator.EnsureAllRegisteredCanonical();
        }

        private static bool RegisterUnitEntryLocked(
            Encyclopedia enc,
            ContractorPackage pkg,
            ModularUnitSpec spec,
            WeaponMount templateMount,
            UnitDefinition templateCargoDef)
        {
            string cargoKey = $"mds_cargo_{spec.UnitId}";
            string mountKey = $"mds_airdrop_{spec.UnitId}";

            Unit? tplUnit = templateCargoDef.unitPrefab != null ? templateCargoDef.unitPrefab.GetComponent<Unit>() : null;
            float matchedMass = (tplUnit != null && tplUnit.GetPrefabMass() > 0f)
                ? tplUnit.GetPrefabMass()
                : Mathf.Max(500f, spec.Logistics.SlingKitMassKg);
            float fullCost = Mathf.Max(0f, spec.Logistics.CreditCost);

            UnitDefinition cargoDef = UnityEngine.Object.Instantiate(templateCargoDef);
            cargoDef.hideFlags = HideFlags.DontUnloadUnusedAsset;
            cargoDef.name = cargoKey;
            cargoDef.jsonKey = cargoKey;
            cargoDef.unitName = spec.DisplayName;
            cargoDef.bogeyName = "Container";
            cargoDef.description = $"{pkg.DisplayName} airdroppable modular defence container ({spec.Role}).";
            cargoDef.code = "MDS";
            cargoDef.mass = matchedMass;
            cargoDef.value = Mathf.Max(1f, fullCost);
            cargoDef.radarSize = Mathf.Max(0.05f, spec.StowedRcs);
            cargoDef.CanSlingLoad = spec.Logistics.SupportsTarantulaSlingKit;
            cargoDef.length = Mathf.Max(1f, spec.ContainerLengthMeters);
            cargoDef.width = Mathf.Max(1f, spec.ContainerWidthMeters);
            cargoDef.height = Mathf.Max(1f, spec.ContainerHeightMeters);
            cargoDef.spawnOffset = Vector3.zero;
            cargoDef.dontAutomaticallyAddToEncyclopedia = true;

            GameInteropHelpers.UnitDefinitionDisabledField?.SetValue(cargoDef, false);
            GameInteropHelpers.UnitDefinitionEventField?.SetValue(cargoDef, false);

            RegisterDefinitionInEncyclopedia(enc, cargoDef, cargoKey);

            WeaponInfo clonedInfo = UnityEngine.Object.Instantiate(templateMount.info);
            clonedInfo.hideFlags = HideFlags.DontUnloadUnusedAsset;
            clonedInfo.name = $"mds_info_{spec.UnitId}";
            clonedInfo.weaponName = $"{pkg.DisplayName}: {spec.DisplayName}";
            clonedInfo.shortName = spec.DisplayName;
            string containerClass = spec.ContainerLengthMeters > 6.5f ? "Heavy ISO-HC" : "20ft ISO";
            clonedInfo.description = $"Air-droppable {containerClass} stealth container ({spec.Role}, {spec.MaxEngagementRangeKm:0}km).";
            clonedInfo.cargo = true;
            clonedInfo.nuclear = false;
            clonedInfo.strategic = false;
            clonedInfo.rearmGround = false;
            clonedInfo.rearmShip = false;
            clonedInfo.SetMassPerRound(matchedMass);
            clonedInfo.SetCostPerRound(fullCost);

            GameObject? mountPrefab = templateMount.prefab;
            if (templateMount.prefab != null)
            {
                GameObject holder = EnsureMountPrefabHolder();
                GameObject clonedPrefab = UnityEngine.Object.Instantiate(templateMount.prefab, holder.transform);
                clonedPrefab.name = $"mds_mount_prefab_{spec.UnitId}";
                clonedPrefab.SetActive(true);
                clonedPrefab.hideFlags = HideFlags.DontUnloadUnusedAsset;

                MountedCargo[] cargos = clonedPrefab.GetComponentsInChildren<MountedCargo>(true);
                for (int c = 0; c < cargos.Length; c++)
                {
                    if (cargos[c] != null)
                    {
                        cargos[c].cargo = cargoDef;
                        cargos[c].info = clonedInfo;
                    }
                }

                Unit[] childUnits = clonedPrefab.GetComponentsInChildren<Unit>(true);
                for (int u = 0; u < childUnits.Length; u++)
                {
                    if (childUnits[u] != null)
                    {
                        childUnits[u].definition = cargoDef;
                    }
                }

                mountPrefab = clonedPrefab;
            }

            WeaponMount clonedMount = UnityEngine.Object.Instantiate(templateMount);
            clonedMount.hideFlags = HideFlags.DontUnloadUnusedAsset;
            clonedMount.name = mountKey;
            clonedMount.jsonKey = mountKey;
            clonedMount.mountName = spec.DisplayName;
            clonedMount.info = clonedInfo;
            clonedMount.prefab = mountPrefab;
            clonedMount.Cargo = true;
            clonedMount.radar = false;
            clonedMount.turret = false;
            clonedMount.countermeasure = false;
            clonedMount.slingloadHook = false;
            clonedMount.missileBay = false;
            clonedMount.Troops = false;
            clonedMount.ammo = 1;
            clonedMount.mass = 0f;
            clonedMount.emptyMass = 0f;
            clonedMount.emptyCost = 0f;
            clonedMount.RCS = spec.StowedRcs;
            clonedMount.emptyRCS = 0f;
            clonedMount.dontAutomaticallyAddToEncyclopedia = true;

            GameInteropHelpers.WeaponMountDisabledField?.SetValue(clonedMount, false);
            GameInteropHelpers.WeaponMountEventField?.SetValue(clonedMount, false);
            clonedMount.Initialize();
            if (clonedMount.mass <= 0f)
            {
                clonedMount.mass = matchedMass;
            }

            RegisterMountInEncyclopedia(enc, clonedMount, mountKey);

            var entry = new AirdropCatalogEntry
            {
                Package = pkg,
                Spec = spec,
                CargoDefinition = cargoDef,
                WeaponMount = clonedMount,
                WeaponInfo = clonedInfo
            };

            EntriesByUnitId[spec.UnitId] = entry;
            EntriesByMount[clonedMount] = entry;
            EntriesByCargoDef[cargoDef] = entry;
            EntriesByKey[mountKey] = entry;
            EntriesByKey[cargoKey] = entry;
            RegisteredMountCount = EntriesByUnitId.Count;
            return true;
        }

        private static void RegisterDefinitionInEncyclopedia(Encyclopedia enc, UnitDefinition def, string key)
        {
            if (enc.otherUnits != null && !enc.otherUnits.Contains(def))
            {
                enc.otherUnits.Add(def);
            }

            if (Encyclopedia.Lookup != null)
            {
                Encyclopedia.Lookup[key] = def;
            }

            if (enc.IndexLookup != null && def is INetworkDefinition netDef && !enc.IndexLookup.Contains(netDef))
            {
                netDef.LookupIndex = enc.IndexLookup.Count;
                enc.IndexLookup.Add(netDef);
            }
        }

        private static void RegisterMountInEncyclopedia(Encyclopedia enc, WeaponMount mount, string key)
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

        private static GameObject EnsureMountPrefabHolder()
        {
            if (mountPrefabHolder == null)
            {
                mountPrefabHolder = new GameObject("MDS_MountPrefabHolder");
                mountPrefabHolder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(mountPrefabHolder);
                mountPrefabHolder.hideFlags = HideFlags.HideAndDontSave;
            }

            return mountPrefabHolder;
        }

        private static int InjectAllEntriesIntoAircraftLocked(Encyclopedia enc)
        {
            var candidates = new List<AircraftDefinition>();
            var seen = new HashSet<AircraftDefinition>();

            if (enc.aircraft != null)
            {
                for (int i = 0; i < enc.aircraft.Count; i++)
                {
                    AircraftDefinition? ac = enc.aircraft[i];
                    if (ac != null && seen.Add(ac))
                    {
                        candidates.Add(ac);
                    }
                }
            }

            AircraftDefinition[] allResourceAircraft = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
            for (int i = 0; i < allResourceAircraft.Length; i++)
            {
                AircraftDefinition? ac = allResourceAircraft[i];
                if (ac != null && seen.Add(ac))
                {
                    candidates.Add(ac);
                }
            }

            var sortedEntries = new List<AirdropCatalogEntry>(EntriesByUnitId.Values);
            sortedEntries.Sort((a, b) => string.Compare(a.Spec.UnitId, b.Spec.UnitId, StringComparison.OrdinalIgnoreCase));

            int equippedAircraft = 0;
            for (int a = 0; a < candidates.Count; a++)
            {
                AircraftDefinition acDef = candidates[a];
                if (acDef.unitPrefab == null)
                {
                    continue;
                }

                Aircraft? ac = acDef.unitPrefab.GetComponent<Aircraft>();
                WeaponManager? wm = ac != null ? ac.weaponManager : null;
                wm ??= acDef.unitPrefab.GetComponentInChildren<WeaponManager>(true);
                if (wm == null || wm.hardpointSets == null)
                {
                    continue;
                }

                bool acHasMdsMounts = false;
                for (int h = 0; h < wm.hardpointSets.Length; h++)
                {
                    HardpointSet? hs = wm.hardpointSets[h];
                    if (hs == null || hs.weaponOptions == null || hs.weaponOptions.Count == 0)
                    {
                        continue;
                    }

                    if (!ClassifyCargoBay(hs, out bool isContainerSized))
                    {
                        continue;
                    }

                    for (int e = 0; e < sortedEntries.Count; e++)
                    {
                        AirdropCatalogEntry entry = sortedEntries[e];
                        bool allowBay = isContainerSized ||
                                        MdsConfig.AllowAllCargoBays.Value ||
                                        entry.Spec.Logistics.AllowLightCargoBays;
                        if (!allowBay)
                        {
                            continue;
                        }

                        if (!hs.weaponOptions.Contains(entry.WeaponMount))
                        {
                            hs.weaponOptions.Add(entry.WeaponMount);
                        }

                        acHasMdsMounts = true;
                    }
                }

                if (acHasMdsMounts)
                {
                    equippedAircraft++;
                }
            }

            return equippedAircraft;
        }

        private static bool ClassifyCargoBay(HardpointSet hs, out bool hasFullContainerOption)
        {
            hasFullContainerOption = false;
            bool hasAnyContainerSpawner = false;
            bool isCombinedName = !string.IsNullOrEmpty(hs.name) &&
                                  hs.name.IndexOf("Combined", StringComparison.OrdinalIgnoreCase) >= 0;

            for (int i = 0; i < hs.weaponOptions.Count; i++)
            {
                WeaponMount? opt = hs.weaponOptions[i];
                if (opt == null || !opt.Cargo || opt.prefab == null || GameInteropHelpers.IsMdsSyntheticKey(opt.jsonKey))
                {
                    continue;
                }

                MountedCargo? mc = opt.prefab.GetComponentInChildren<MountedCargo>(true);
                if (mc == null || mc.cargo == null || mc.cargo.unitPrefab == null)
                {
                    continue;
                }

                if (mc.cargo.unitPrefab.GetComponent<Container>() == null)
                {
                    continue;
                }

                hasAnyContainerSpawner = true;
                string key = $"{opt.jsonKey} {opt.mountName} {mc.cargo.jsonKey} {mc.cargo.unitName}";
                bool isSmallFuelDrum =
                    key.IndexOf("Fuel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    key.IndexOf("1500L", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    key.IndexOf("6000L", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    key.IndexOf("6,000L", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isSmallFuelDrum)
                {
                    continue;
                }

                if (isCombinedName ||
                    key.IndexOf("Container", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    key.IndexOf("RAM45", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hasFullContainerOption = true;
                }
            }

            return hasAnyContainerSpawner && hasFullContainerOption;
        }

        private static bool TryResolveContainerTemplate(
            Encyclopedia enc,
            out WeaponMount? bestMount,
            out UnitDefinition? bestCargoDef)
        {
            if (cachedTemplateMount != null && cachedTemplateCargoDef != null)
            {
                bestMount = cachedTemplateMount;
                bestCargoDef = cachedTemplateCargoDef;
                return true;
            }

            bestMount = null;
            bestCargoDef = null;
            int bestRank = -1;

            if (enc.weaponMounts == null)
            {
                return false;
            }

            for (int i = 0; i < enc.weaponMounts.Count; i++)
            {
                WeaponMount? wm = enc.weaponMounts[i];
                if (wm == null || !wm.Cargo || wm.prefab == null || wm.info == null)
                {
                    continue;
                }

                MountedCargo? mc = wm.prefab.GetComponentInChildren<MountedCargo>(true);
                if (mc == null || mc.cargo == null || mc.cargo.unitPrefab == null)
                {
                    continue;
                }

                if (mc.cargo.unitPrefab.GetComponent<Container>() == null)
                {
                    continue;
                }

                string combined = $"{wm.jsonKey} {wm.mountName} {mc.cargo.jsonKey}";
                int rank = 1;
                if (combined.IndexOf("RadarContainer", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    rank = 100;
                }
                else if (combined.IndexOf("MunitionsContainer", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    rank = 80;
                }
                else if (combined.IndexOf("Container", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    rank = 50;
                }

                if (rank > bestRank)
                {
                    bestRank = rank;
                    bestMount = wm;
                    bestCargoDef = mc.cargo;
                }
            }

            cachedTemplateMount = bestMount;
            cachedTemplateCargoDef = bestCargoDef;
            return bestMount != null && bestCargoDef != null;
        }
    }
}
