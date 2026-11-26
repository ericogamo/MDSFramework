using System;
using System.Collections;
using System.Collections.Generic;
using MDSFramework.Api;
using UnityEngine;

namespace MDSFramework.Deployment
{
    internal sealed class MdsUnitCatalogEntry
    {
        internal ContractorPackage Package = null!;
        internal ModularUnitSpec Spec = null!;
        internal UnitDefinition Definition = null!;
        internal string JsonKey = string.Empty;
        internal bool IsCargoContainer;
    }

    internal sealed class MdsMissileCatalogEntry
    {
        internal ContractorPackage Package = null!;
        internal CustomMissileSpec Spec = null!;
        internal MissileDefinition Definition = null!;
        internal string JsonKey = string.Empty;
    }

    internal static class MdsEncyclopediaCatalog
    {
        private static readonly object SyncLock = new object();
        private static readonly Dictionary<UnitDefinition, MdsUnitCatalogEntry> UnitsByDefinition = new Dictionary<UnitDefinition, MdsUnitCatalogEntry>();
        private static readonly Dictionary<string, MdsUnitCatalogEntry> UnitsByKey = new Dictionary<string, MdsUnitCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, MdsUnitCatalogEntry> UnitsByUnitId = new Dictionary<string, MdsUnitCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, MdsUnitCatalogEntry> UnitsByDisplayName = new Dictionary<string, MdsUnitCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<MissileDefinition, MdsMissileCatalogEntry> MissilesByDefinition = new Dictionary<MissileDefinition, MdsMissileCatalogEntry>();
        private static readonly Dictionary<string, MdsMissileCatalogEntry> MissilesByKey = new Dictionary<string, MdsMissileCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, MdsMissileCatalogEntry> MissilesById = new Dictionary<string, MdsMissileCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, MdsMissileCatalogEntry> MissilesByDisplayName = new Dictionary<string, MdsMissileCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        private static bool subscribedToRegistry;

        internal static int RegisteredUnitCount { get; private set; }
        internal static int RegisteredMissileCount { get; private set; }

        internal static void Initialize()
        {
            if (!subscribedToRegistry)
            {
                subscribedToRegistry = true;
                ContractorRegistry.OnPackageRegistered += OnPackageRegistered;
            }
        }

        internal static void Dispose()
        {
            if (subscribedToRegistry)
            {
                subscribedToRegistry = false;
                ContractorRegistry.OnPackageRegistered -= OnPackageRegistered;
            }
        }

        internal static void EnsureRegisteredAll()
        {
            if (!MdsConfig.EnableMissionEditorCatalog.Value || Encyclopedia.i == null)
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
                    if (pkg == null)
                    {
                        continue;
                    }

                    if (pkg.Units != null)
                    {
                        var sortedUnits = new List<ModularUnitSpec>(pkg.Units);
                        sortedUnits.Sort((a, b) => string.Compare(a?.UnitId, b?.UnitId, StringComparison.OrdinalIgnoreCase));

                        for (int i = 0; i < sortedUnits.Count; i++)
                        {
                            ModularUnitSpec spec = sortedUnits[i];
                            if (spec == null || string.IsNullOrWhiteSpace(spec.UnitId))
                            {
                                continue;
                            }

                            if (UnitsByUnitId.TryGetValue(spec.UnitId, out MdsUnitCatalogEntry existing))
                            {
                                EnsureUnitInEncyclopediaLocked(enc, existing.Definition, existing.JsonKey);
                                continue;
                            }

                            if (RegisterUnitEntryLocked(enc, pkg, spec))
                            {
                                newlyRegistered++;
                            }
                        }
                    }

                    if (pkg.Missiles != null)
                    {
                        var sortedMissiles = new List<CustomMissileSpec>(pkg.Missiles);
                        sortedMissiles.Sort((a, b) => string.Compare(a?.MissileId, b?.MissileId, StringComparison.OrdinalIgnoreCase));

                        for (int i = 0; i < sortedMissiles.Count; i++)
                        {
                            CustomMissileSpec mSpec = sortedMissiles[i];
                            if (mSpec == null || string.IsNullOrWhiteSpace(mSpec.MissileId))
                            {
                                continue;
                            }

                            if (MissilesById.TryGetValue(mSpec.MissileId, out MdsMissileCatalogEntry existingM))
                            {
                                EnsureMissileInEncyclopediaLocked(enc, existingM.Definition, existingM.JsonKey);
                                continue;
                            }

                            if (RegisterMissileEntryLocked(enc, pkg, mSpec))
                            {
                                newlyRegistered++;
                            }
                        }
                    }
                }
            }

            if (newlyRegistered > 0)
            {
                InvalidateMissionEditorCache();
                MdsLog.Info(
                    $"[Catalog] Registered {RegisteredUnitCount} modular units and {RegisteredMissileCount} missiles into Encyclopedia & Mission Editor.");
            }
        }

        internal static void InvalidateMissionEditorCache()
        {
            try
            {
                if (GameInteropHelpers.NewUnitPanelUnitProvidersField?.GetValue(null) is IDictionary providers)
                {
                    providers.Clear();
                }

                if (Encyclopedia.i != null)
                {
                    GameInteropHelpers.EncyclopediaAircraftAndVehiclesCacheField?.SetValue(Encyclopedia.i, null);
                }
            }
            catch (Exception ex)
            {
                MdsLog.Debug($"InvalidateMissionEditorCache ignored error: {ex.Message}");
            }
        }

        internal static bool TryGetUnitDefinition(string unitId, out UnitDefinition def)
        {
            if (!string.IsNullOrWhiteSpace(unitId))
            {
                lock (SyncLock)
                {
                    if (UnitsByUnitId.TryGetValue(unitId, out MdsUnitCatalogEntry entry))
                    {
                        def = entry.Definition;
                        return true;
                    }
                }
            }

            def = null!;
            return false;
        }

        internal static bool TryResolveUnitDefinition(
            UnitDefinition? def,
            out ContractorPackage pkg,
            out ModularUnitSpec spec,
            out bool isCargoContainer)
        {
            if (def != null)
            {
                lock (SyncLock)
                {
                    if (UnitsByDefinition.TryGetValue(def, out MdsUnitCatalogEntry entry))
                    {
                        pkg = entry.Package;
                        spec = entry.Spec;
                        isCargoContainer = entry.IsCargoContainer;
                        return true;
                    }

                    if (!string.IsNullOrEmpty(def.jsonKey) && UnitsByKey.TryGetValue(def.jsonKey, out entry))
                    {
                        pkg = entry.Package;
                        spec = entry.Spec;
                        isCargoContainer = entry.IsCargoContainer;
                        return true;
                    }
                }

                if (AirdropCatalogManager.TryResolveCargoDef(def, out AirdropCatalogEntry airdropEntry))
                {
                    pkg = airdropEntry.Package;
                    spec = airdropEntry.Spec;
                    isCargoContainer = true;
                    return true;
                }
            }

            pkg = null!;
            spec = null!;
            isCargoContainer = false;
            return false;
        }

        internal static bool TryResolveUnitKey(
            string? jsonKey,
            out ContractorPackage pkg,
            out ModularUnitSpec spec,
            out UnitDefinition def,
            out bool isCargoContainer)
        {
            if (!string.IsNullOrWhiteSpace(jsonKey))
            {
                lock (SyncLock)
                {
                    if (UnitsByKey.TryGetValue(jsonKey!, out MdsUnitCatalogEntry entry))
                    {
                        pkg = entry.Package;
                        spec = entry.Spec;
                        def = entry.Definition;
                        isCargoContainer = entry.IsCargoContainer;
                        return true;
                    }
                }

                if (AirdropCatalogManager.TryResolveCargoKey(jsonKey, out AirdropCatalogEntry airdropEntry))
                {
                    pkg = airdropEntry.Package;
                    spec = airdropEntry.Spec;
                    def = airdropEntry.CargoDefinition;
                    isCargoContainer = true;
                    return true;
                }
            }

            pkg = null!;
            spec = null!;
            def = null!;
            isCargoContainer = false;
            return false;
        }

        internal static bool TryResolveMissileDefinition(
            MissileDefinition? def,
            out ContractorPackage pkg,
            out CustomMissileSpec spec)
        {
            if (def != null)
            {
                lock (SyncLock)
                {
                    if (MissilesByDefinition.TryGetValue(def, out MdsMissileCatalogEntry entry))
                    {
                        pkg = entry.Package;
                        spec = entry.Spec;
                        return true;
                    }

                    if (!string.IsNullOrEmpty(def.jsonKey) && MissilesByKey.TryGetValue(def.jsonKey, out entry))
                    {
                        pkg = entry.Package;
                        spec = entry.Spec;
                        return true;
                    }
                }
            }

            pkg = null!;
            spec = null!;
            return false;
        }

        internal static bool TryResolveMissileKey(
            string? jsonKey,
            out ContractorPackage pkg,
            out CustomMissileSpec spec,
            out MissileDefinition def)
        {
            if (!string.IsNullOrWhiteSpace(jsonKey))
            {
                lock (SyncLock)
                {
                    if (MissilesByKey.TryGetValue(jsonKey!, out MdsMissileCatalogEntry entry))
                    {
                        pkg = entry.Package;
                        spec = entry.Spec;
                        def = entry.Definition;
                        return true;
                    }
                }
            }

            pkg = null!;
            spec = null!;
            def = null!;
            return false;
        }

        internal static bool TryGetMissileDefinition(string? missileId, out MissileDefinition def)
        {
            if (!string.IsNullOrWhiteSpace(missileId))
            {
                lock (SyncLock)
                {
                    if (MissilesById.TryGetValue(missileId!, out MdsMissileCatalogEntry entry))
                    {
                        def = entry.Definition;
                        return true;
                    }
                }
            }

            def = null!;
            return false;
        }

        internal static bool TryResolveMissileByDisplayName(
            string? displayName,
            out ContractorPackage pkg,
            out CustomMissileSpec spec,
            out MissileDefinition def)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                string trimmed = displayName!.Trim();
                lock (SyncLock)
                {
                    if (MissilesByDisplayName.TryGetValue(trimmed, out MdsMissileCatalogEntry entry))
                    {
                        pkg = entry.Package;
                        spec = entry.Spec;
                        def = entry.Definition;
                        return true;
                    }
                }
            }

            pkg = null!;
            spec = null!;
            def = null!;
            return false;
        }

        internal static bool TryResolveUnitByDisplayName(
            string? displayName,
            out ContractorPackage pkg,
            out ModularUnitSpec spec,
            out UnitDefinition def,
            out bool isCargoContainer)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                string trimmed = displayName!.Trim();
                bool isCargoPrefix = trimmed.StartsWith("[MDS Cargo] ", StringComparison.OrdinalIgnoreCase);
                string lookupKey = isCargoPrefix ? trimmed.Substring("[MDS Cargo] ".Length) : trimmed;

                MdsUnitCatalogEntry? matched = null;
                lock (SyncLock)
                {
                    UnitsByDisplayName.TryGetValue(lookupKey, out matched);
                }

                if (matched != null)
                {
                    if (isCargoPrefix && AirdropCatalogManager.TryGetEntryByUnitId(matched.Spec.UnitId, out AirdropCatalogEntry airdropEntry))
                    {
                        pkg = airdropEntry.Package;
                        spec = airdropEntry.Spec;
                        def = airdropEntry.CargoDefinition;
                        isCargoContainer = true;
                        return true;
                    }

                    pkg = matched.Package;
                    spec = matched.Spec;
                    def = matched.Definition;
                    isCargoContainer = matched.IsCargoContainer;
                    return true;
                }
            }

            pkg = null!;
            spec = null!;
            def = null!;
            isCargoContainer = false;
            return false;
        }

        private static void OnPackageRegistered(ContractorPackage _)
        {
            EnsureRegisteredAll();
        }

        private static bool RegisterUnitEntryLocked(Encyclopedia enc, ContractorPackage pkg, ModularUnitSpec spec)
        {
            UnitDefinition? templateDef = GameInteropHelpers.ResolveEncyclopediaUnitTemplate(spec.BaseDefinitionCloneKey);
            if (templateDef == null || templateDef.unitPrefab == null)
            {
                MdsLog.Warn($"[Catalog] Could not resolve vanilla template '{spec.BaseDefinitionCloneKey}' for unit '{spec.UnitId}'.");
                return false;
            }

            string jsonKey = $"mds_{spec.UnitId}";
            UnitDefinition clonedDef = UnityEngine.Object.Instantiate(templateDef);
            clonedDef.hideFlags = HideFlags.DontUnloadUnusedAsset;
            clonedDef.name = jsonKey;
            clonedDef.jsonKey = jsonKey;
            clonedDef.unitName = $"[MDS] {spec.DisplayName}";
            clonedDef.code = BuildShortCode(spec);
            clonedDef.description = BuildUnitDescription(pkg, spec);
            clonedDef.value = Mathf.Max(0.05f, spec.Logistics?.CreditCost ?? 100f);
            clonedDef.radarSize = Mathf.Max(0.05f, spec.StowedRcs);
            if (!(clonedDef is VehicleDefinition))
            {
                clonedDef.spawnOffset = Vector3.zero;
            }
            clonedDef.dontAutomaticallyAddToEncyclopedia = false;

            if (clonedDef is VehicleDefinition && spec.IsContainerHauler)
            {
                clonedDef.length = 10.73f;
                clonedDef.width = 2.98f;
                clonedDef.height = 3.35f;
            }
            else if (!(clonedDef is VehicleDefinition) &&
                     string.Equals(spec.FallbackMeshArchetype, "container", StringComparison.OrdinalIgnoreCase))
            {
                clonedDef.length = Mathf.Max(1f, spec.ContainerLengthMeters);
                clonedDef.width = Mathf.Max(1f, spec.ContainerWidthMeters);
                clonedDef.height = Mathf.Max(1f, spec.ContainerHeightMeters);
            }

            GameInteropHelpers.UnitDefinitionDisabledField?.SetValue(clonedDef, false);
            GameInteropHelpers.UnitDefinitionEventField?.SetValue(clonedDef, false);

            try
            {
                clonedDef.CacheMass();
            }
            catch (Exception ex)
            {
                MdsLog.Debug($"CacheMass skipped for '{jsonKey}': {ex.Message}");
            }

            EnsureUnitInEncyclopediaLocked(enc, clonedDef, jsonKey);

            var entry = new MdsUnitCatalogEntry
            {
                Package = pkg,
                Spec = spec,
                Definition = clonedDef,
                JsonKey = jsonKey,
                IsCargoContainer = false
            };

            UnitsByUnitId[spec.UnitId] = entry;
            UnitsByDefinition[clonedDef] = entry;
            UnitsByKey[jsonKey] = entry;
            UnitsByDisplayName[spec.DisplayName] = entry;
            UnitsByDisplayName[clonedDef.unitName] = entry;
            RegisteredUnitCount = UnitsByUnitId.Count;
            return true;
        }

        private static bool RegisterMissileEntryLocked(Encyclopedia enc, ContractorPackage pkg, CustomMissileSpec spec)
        {
            MissileDefinition? templateMissile = spec.IsAntiShip
                ? GameInteropHelpers.ResolveEncyclopediaMissileDef(spec.BaseMissileCloneKey, preferBallisticOrSam: false)
                : (GameInteropHelpers.ResolveEncyclopediaArhMissileDef(spec.BaseMissileCloneKey) ??
                   GameInteropHelpers.ResolveEncyclopediaMissileDef(spec.BaseMissileCloneKey, preferBallisticOrSam: true));
            if (templateMissile == null || templateMissile.unitPrefab == null)
            {
                MdsLog.Warn($"[Catalog] Could not resolve vanilla template '{spec.BaseMissileCloneKey}' for missile '{spec.MissileId}'.");
                return false;
            }

            string jsonKey = $"mds_missile_{spec.MissileId}";
            MissileDefinition clonedMissile = UnityEngine.Object.Instantiate(templateMissile);
            clonedMissile.hideFlags = HideFlags.DontUnloadUnusedAsset;
            clonedMissile.name = jsonKey;
            clonedMissile.jsonKey = jsonKey;
            clonedMissile.unitName = spec.DisplayName;
            clonedMissile.description = BuildMissileDescription(pkg, spec);
            clonedMissile.dontAutomaticallyAddToEncyclopedia = false;

            GameInteropHelpers.UnitDefinitionDisabledField?.SetValue(clonedMissile, false);
            GameInteropHelpers.UnitDefinitionEventField?.SetValue(clonedMissile, false);

            EnsureMissileInEncyclopediaLocked(enc, clonedMissile, jsonKey);

            var entry = new MdsMissileCatalogEntry
            {
                Package = pkg,
                Spec = spec,
                Definition = clonedMissile,
                JsonKey = jsonKey
            };

            MissilesById[spec.MissileId] = entry;
            MissilesByDefinition[clonedMissile] = entry;
            MissilesByKey[jsonKey] = entry;
            MissilesByDisplayName[spec.DisplayName] = entry;
            MissilesByDisplayName[$"[MDS] {spec.DisplayName}"] = entry;
            RegisteredMissileCount = MissilesById.Count;
            return true;
        }

        private static void EnsureUnitInEncyclopediaLocked(Encyclopedia enc, UnitDefinition def, string jsonKey)
        {
            if (def is VehicleDefinition vehDef)
            {
                if (enc.vehicles != null && !enc.vehicles.Contains(vehDef))
                {
                    enc.vehicles.Add(vehDef);
                    GameInteropHelpers.EncyclopediaAircraftAndVehiclesCacheField?.SetValue(enc, null);
                }
            }
            else if (def is BuildingDefinition bldDef)
            {
                if (enc.buildings != null && !enc.buildings.Contains(bldDef))
                {
                    enc.buildings.Add(bldDef);
                }
            }
            else
            {
                if (enc.otherUnits != null && !enc.otherUnits.Contains(def))
                {
                    enc.otherUnits.Add(def);
                }
            }

            if (Encyclopedia.Lookup != null)
            {
                Encyclopedia.Lookup[jsonKey] = def;
            }

            if (enc.IndexLookup != null && def is INetworkDefinition netDef && !enc.IndexLookup.Contains(netDef))
            {
                netDef.LookupIndex = enc.IndexLookup.Count;
                enc.IndexLookup.Add(netDef);
            }
        }

        private static void EnsureMissileInEncyclopediaLocked(Encyclopedia enc, MissileDefinition def, string jsonKey)
        {
            if (enc.missiles != null && !enc.missiles.Contains(def))
            {
                enc.missiles.Add(def);
            }

            if (Encyclopedia.Lookup != null)
            {
                Encyclopedia.Lookup[jsonKey] = def;
            }

            if (enc.IndexLookup != null && def is INetworkDefinition netDef && !enc.IndexLookup.Contains(netDef))
            {
                netDef.LookupIndex = enc.IndexLookup.Count;
                enc.IndexLookup.Add(netDef);
            }
        }

        private static string BuildShortCode(ModularUnitSpec spec)
        {
            return spec.Role switch
            {
                WeaponCascadeRole.EarlyWarningRadar => "EWR",
                WeaponCascadeRole.FireControl => "FCU",
                WeaponCascadeRole.CommandControl => "C2",
                WeaponCascadeRole.LaserHel => "HEL",
                WeaponCascadeRole.CloseInGun => "CIWS",
                WeaponCascadeRole.ShortMedSam => "SAM",
                WeaponCascadeRole.LongRangeSam => "LRSAM",
                WeaponCascadeRole.AbmVls => "VLS",
                WeaponCascadeRole.AntiShipCoastal => "ASHM",
                WeaponCascadeRole.LogisticsHauler => "HAUL",
                _ => "MDS"
            };
        }

        private static string BuildUnitDescription(ContractorPackage pkg, ModularUnitSpec spec)
        {
            if (spec.Role == WeaponCascadeRole.LogisticsHauler || spec.IsContainerHauler)
            {
                return $"{pkg.DisplayName} | 8x8 ISO Container Carrier / Flatbed Hauler | " +
                       $"Unarmed Tactical Logistics | Faction: {pkg.DefaultFaction}";
            }

            if (spec.Role == WeaponCascadeRole.CommandControl || spec.IsCommandCenter)
            {
                return $"{pkg.DisplayName} | Skyshield.net C2 Hub | Relay {spec.NetworkRelayRadiusKm:0.#} km | " +
                       $"Coverage-First WTA & CEC | Faction: {pkg.DefaultFaction}";
            }

            if (spec.Role == WeaponCascadeRole.AntiShipCoastal)
            {
                string platformTag = spec.IsMobileVehicle ? "8x8 Mobile Launcher" : "20ft Stealth Container";
                string ammoCoastal = spec.PrimaryMagazineCapacity > 0
                    ? $"{spec.PrimaryMagazineCapacity} rounds"
                    : $"{spec.MagazineCapacity} rounds";
                return $"{pkg.DisplayName} | Anti-Ship Coastal ({platformTag}) | Range {spec.MinEngagementRangeKm:0.#}-{spec.MaxEngagementRangeKm:0.#} km | " +
                       $"{ammoCoastal} | Autonomous | Faction: {pkg.DefaultFaction}";
            }

            string fcuTag = spec.RequiresFcuLink ? "Requires FCU link" : "Autonomous / Network Hub";
            string ammoTag = spec.PrimaryMagazineCapacity > 0 && spec.SecondaryMagazineCapacity > 0
                ? $"{spec.PrimaryMagazineCapacity}+{spec.SecondaryMagazineCapacity} rounds"
                : spec.MagazineCapacity > 0
                    ? $"{spec.MagazineCapacity} rounds"
                    : "Sensor node";

            return $"{pkg.DisplayName} | {spec.Role} | Range {spec.MinEngagementRangeKm:0.#}-{spec.MaxEngagementRangeKm:0.#} km | " +
                   $"{ammoTag} | {fcuTag} | Faction: {pkg.DefaultFaction}";
        }

        private static string BuildMissileDescription(ContractorPackage pkg, CustomMissileSpec spec)
        {
            string abmTag = spec.IsAbmInterceptor ? " | ABM Exo-KKV" : string.Empty;
            string hojTag = spec.SupportsActiveToPassiveHoj ? " | Active+Passive HOJ" : string.Empty;
            string ashmTag = spec.IsAntiShip ? $" | {Mathf.Max(2.5f, spec.SeaSkimAltitudeMeters):0.#}m Sea-Skim | Active ECCM/ECM | Evasive" : string.Empty;
            string speedTag = spec.TargetMach > 0f ? $"Mach {spec.TargetMach:0.00}" : $"Cruise {spec.CruiseSpeedMps:0} m/s";
            return $"{pkg.DisplayName} | Profile: {spec.FlightProfile} | Range {spec.MaxEngagementRangeKm:0.#} km | " +
                   $"{speedTag}{abmTag}{hojTag}{ashmTag}";
        }
    }
}
