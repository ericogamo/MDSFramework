using System;
using System.Collections.Generic;
using System.Globalization;
using MDSFramework.Api;

namespace MDSFramework.Deployment
{
    /// <summary>
    /// Ensures all MDS Encyclopedia, Airdrop, and Air-Launched Missile definitions register
    /// in a single canonical, deterministic order across Host and Clients so Mirage
    /// Encyclopedia.IndexLookup wire indices never diverge.
    /// </summary>
    internal static class MdsCatalogCoordinator
    {
        private static readonly object CoordinatorLock = new object();
        private static bool initialized;
        private static Encyclopedia? lastCanonicalEncyclopedia;
        private static bool canonicalRegistrationCompleted;

        internal static uint CatalogWireHash { get; private set; }
        internal static int MdsIndexEntryCount { get; private set; }

        internal static void Initialize()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            ContractorRegistry.OnPackageRegistered -= OnPackageRegistered;
            ContractorRegistry.OnPackageRegistered += OnPackageRegistered;
        }

        internal static void Dispose()
        {
            if (!initialized)
            {
                return;
            }

            initialized = false;
            lastCanonicalEncyclopedia = null;
            canonicalRegistrationCompleted = false;
            ContractorRegistry.OnPackageRegistered -= OnPackageRegistered;
        }

        private static void OnPackageRegistered(ContractorPackage pkg)
        {
            Encyclopedia? enc = Encyclopedia.i;
            if (enc != null &&
                ReferenceEquals(enc, lastCanonicalEncyclopedia) &&
                canonicalRegistrationCompleted &&
                enc.IndexLookup != null &&
                enc.IndexLookup.Count > 0)
            {
                MdsLog.Warn(
                    $"[Catalog] Rejecting late RegisterPackage('{pkg?.PackageId}') after Encyclopedia.AfterLoad populated IndexLookup ({enc.IndexLookup.Count} entries). " +
                    "Contractor packages must register during Awake() to prevent Mirage IndexLookup desync.");
                return;
            }

            EnsureAllRegisteredCanonical();
        }

        internal static void EnsureAllRegisteredCanonical()
        {
            Encyclopedia? enc = Encyclopedia.i;
            if (enc == null)
            {
                return;
            }

            lock (CoordinatorLock)
            {
                if (!ReferenceEquals(lastCanonicalEncyclopedia, enc))
                {
                    lastCanonicalEncyclopedia = enc;
                    canonicalRegistrationCompleted = false;
                }

                int addedUnitsOrMissiles = MdsEncyclopediaCatalog.EnsureRegisteredAllInternal(enc);
                int addedAirdrop = AirdropCatalogManager.EnsureRegisteredAllInternal(enc);
                int addedAirMissiles = AircraftMissileCatalogManager.EnsureRegisteredAllInternal(enc);

                if (addedUnitsOrMissiles > 0 || addedAirdrop > 0 || addedAirMissiles > 0)
                {
                    MdsEncyclopediaCatalog.InvalidateMissionEditorCache();
                }

                if (enc.IndexLookup != null && enc.IndexLookup.Count > 0)
                {
                    canonicalRegistrationCompleted = true;
                }

                RecomputeWireHash(enc);
            }
        }

        private static void RecomputeWireHash(Encyclopedia enc)
        {
            if (enc.IndexLookup == null)
            {
                CatalogWireHash = 0u;
                MdsIndexEntryCount = 0;
                return;
            }

            uint hash = 2166136261u;
            int count = 0;
            for (int i = 0; i < enc.IndexLookup.Count; i++)
            {
                INetworkDefinition? netDef = enc.IndexLookup[i];
                if (netDef is not UnitDefinition && netDef is not WeaponMount)
                {
                    continue;
                }

                string? key = netDef is UnitDefinition ud
                    ? ud.jsonKey
                    : (netDef as WeaponMount)?.jsonKey;
                if (!GameInteropHelpers.IsMdsSyntheticKey(key))
                {
                    continue;
                }

                count++;
                string token = $"{i.ToString(CultureInfo.InvariantCulture)}:{key}\n";
                for (int c = 0; c < token.Length; c++)
                {
                    hash = (hash ^ token[c]) * 16777619u;
                }
            }

            CatalogWireHash = hash;
            MdsIndexEntryCount = count;
        }
    }
}
