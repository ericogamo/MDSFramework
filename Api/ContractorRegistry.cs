using System;
using System.Collections.Generic;
using MDSFramework.Deployment;

namespace MDSFramework.Api
{
    public static class ContractorRegistry
    {
        private static readonly object SyncLock = new object();
        private static readonly Dictionary<string, ContractorPackage> PackagesById = new Dictionary<string, ContractorPackage>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (ContractorPackage Package, ModularUnitSpec Unit)> UnitsById = new Dictionary<string, (ContractorPackage, ModularUnitSpec)>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (ContractorPackage Package, CustomMissileSpec Missile)> MissilesById = new Dictionary<string, (ContractorPackage, CustomMissileSpec)>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<WeaponInfo> AntiShipWeaponInfos = new HashSet<WeaponInfo>();

        public static event Action<ContractorPackage>? OnPackageRegistered;

        public static void RegisterPackage(ContractorPackage package)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            if (string.IsNullOrWhiteSpace(package.PackageId))
            {
                throw new ArgumentException("ContractorPackage.PackageId must be non-empty.", nameof(package));
            }

            lock (SyncLock)
            {
                PackagesById[package.PackageId] = package;

                if (package.Units != null)
                {
                    for (int i = 0; i < package.Units.Count; i++)
                    {
                        ModularUnitSpec unit = package.Units[i];
                        if (unit != null && !string.IsNullOrWhiteSpace(unit.UnitId))
                        {
                            UnitsById[unit.UnitId] = (package, unit);
                        }
                    }
                }

                if (package.Missiles != null)
                {
                    for (int i = 0; i < package.Missiles.Count; i++)
                    {
                        CustomMissileSpec missile = package.Missiles[i];
                        if (missile != null && !string.IsNullOrWhiteSpace(missile.MissileId))
                        {
                            MissilesById[missile.MissileId] = (package, missile);
                        }
                    }
                }
            }

            MdsLog.Info(
                $"Registered contractor '{package.DisplayName}' ({package.PackageId}): " +
                $"{package.Units?.Count ?? 0} units, {package.Missiles?.Count ?? 0} missiles, faction={package.DefaultFaction}.");

            OnPackageRegistered?.Invoke(package);
        }

        public static IReadOnlyCollection<ContractorPackage> GetPackages()
        {
            lock (SyncLock)
            {
                return new List<ContractorPackage>(PackagesById.Values).AsReadOnly();
            }
        }

        public static bool TryGetPackage(string packageId, out ContractorPackage package)
        {
            lock (SyncLock)
            {
                return PackagesById.TryGetValue(packageId, out package!);
            }
        }

        public static bool TryGetUnitSpec(string unitId, out ContractorPackage package, out ModularUnitSpec unitSpec)
        {
            lock (SyncLock)
            {
                if (UnitsById.TryGetValue(unitId, out (ContractorPackage Package, ModularUnitSpec Unit) entry))
                {
                    package = entry.Package;
                    unitSpec = entry.Unit;
                    return true;
                }
            }

            package = null!;
            unitSpec = null!;
            return false;
        }

        public static bool TryGetMissileSpec(string missileId, out ContractorPackage package, out CustomMissileSpec missileSpec)
        {
            lock (SyncLock)
            {
                if (MissilesById.TryGetValue(missileId, out (ContractorPackage Package, CustomMissileSpec Missile) entry))
                {
                    package = entry.Package;
                    missileSpec = entry.Missile;
                    return true;
                }
            }

            package = null!;
            missileSpec = null!;
            return false;
        }

        internal static void RegisterAntiShipWeaponInfo(WeaponInfo? info)
        {
            if (info == null)
            {
                return;
            }

            lock (SyncLock)
            {
                AntiShipWeaponInfos.Add(info);
            }
        }

        public static bool IsAntiShipWeaponInfo(WeaponInfo? info)
        {
            if (info == null)
            {
                return false;
            }

            lock (SyncLock)
            {
                if (AntiShipWeaponInfos.Contains(info))
                {
                    return true;
                }
            }

            return AircraftMissileCatalogManager.TryResolveInfo(info, out AirMissileCatalogEntry entry) &&
                   entry.Spec.IsAntiShip;
        }
    }
}
