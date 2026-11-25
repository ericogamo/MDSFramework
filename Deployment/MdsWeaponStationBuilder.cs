using System;
using System.Collections.Generic;
using System.IO;
using MDSFramework.Api;
using MDSFramework.Missiles;
using MDSFramework.Sensors;
using UnityEngine;

namespace MDSFramework.Deployment
{
    internal sealed class MdsWeaponStationProxy : Weapon
    {
        private ModularSiteBehavior? site;
        private bool isSecondarySlot;
        private int maxCapacity;
        private int fallbackAmmo;
        private int lastSyncedAmmo;
        private bool insideExternalAbsorb;

        internal void Configure(
            Unit ownerUnit,
            WeaponInfo weaponInfo,
            WeaponStation station,
            ModularSiteBehavior? ownerSite,
            bool secondarySlot,
            int fullCapacity,
            bool rearmable)
        {
            attachedUnit = ownerUnit;
            info = weaponInfo;
            weaponStation = station;
            site = ownerSite;
            isSecondarySlot = secondarySlot;
            maxCapacity = Mathf.Max(0, fullCapacity);
            fallbackAmmo = maxCapacity;
            Rearmable = rearmable;
            RequestRearmLevel = rearmable ? 0.5f : 0f;
            lastSyncedAmmo = Mathf.Clamp(
                site == null ? fallbackAmmo : (isSecondarySlot ? site.SecondaryAmmoRemaining : site.PrimaryAmmoRemaining),
                0,
                maxCapacity);
            ammo = lastSyncedAmmo;
            SyncToStation();
        }

        private void AbsorbExternalAmmoAdditions()
        {
            if (insideExternalAbsorb || ammo <= lastSyncedAmmo)
            {
                return;
            }

            insideExternalAbsorb = true;
            try
            {
                int targetAmmo = Math.Min(maxCapacity, ammo);
                int delta = targetAmmo - lastSyncedAmmo;
                if (delta > 0)
                {
                    if (site != null)
                    {
                        if (isSecondarySlot)
                        {
                            site.SecondaryAmmoRemaining = Math.Min(maxCapacity, Math.Max(0, site.SecondaryAmmoRemaining) + delta);
                        }
                        else
                        {
                            site.PrimaryAmmoRemaining = Math.Min(maxCapacity, Math.Max(0, site.PrimaryAmmoRemaining) + delta);
                        }
                    }
                    else
                    {
                        fallbackAmmo = Math.Min(maxCapacity, fallbackAmmo + delta);
                    }
                }

                int synced = Math.Min(
                    maxCapacity,
                    site == null ? fallbackAmmo : Math.Max(0, isSecondarySlot ? site.SecondaryAmmoRemaining : site.PrimaryAmmoRemaining));
                lastSyncedAmmo = synced;
                ammo = synced;
                site?.RequestRearmIfNeeded();
            }
            finally
            {
                insideExternalAbsorb = false;
            }
        }

        public override int GetAmmoLoaded()
        {
            return GetAmmoTotal();
        }

        public override int GetAmmoTotal()
        {
            AbsorbExternalAmmoAdditions();
            if (site == null)
            {
                return Mathf.Clamp(fallbackAmmo, 0, maxCapacity);
            }

            int remaining = isSecondarySlot
                ? site.SecondaryAmmoRemaining
                : site.PrimaryAmmoRemaining;
            return Mathf.Clamp(remaining, 0, maxCapacity);
        }

        public override int GetFullAmmo()
        {
            return maxCapacity;
        }

        public override float GetReloadProgress()
        {
            return 0f;
        }

        public override void Rearm(int ammoToRearm, WeaponStation station)
        {
            weaponStation = station;
            int added = Math.Max(0, ammoToRearm);
            if (site != null)
            {
                if (isSecondarySlot)
                {
                    site.SecondaryAmmoRemaining = Math.Min(maxCapacity, Math.Max(0, site.SecondaryAmmoRemaining) + added);
                }
                else
                {
                    site.PrimaryAmmoRemaining = Math.Min(maxCapacity, Math.Max(0, site.PrimaryAmmoRemaining) + added);
                }
            }
            else
            {
                fallbackAmmo = Math.Min(maxCapacity, fallbackAmmo + added);
            }

            SyncToStation();
            site?.RequestRearmIfNeeded();
            weaponStation?.Updated();
        }

        internal void SyncToStation()
        {
            AbsorbExternalAmmoAdditions();
            int current = GetAmmoTotal();
            ammo = current;
            lastSyncedAmmo = current;
            if (weaponStation != null)
            {
                weaponStation.Ammo = current;
                weaponStation.FullAmmo = maxCapacity;
            }
        }
    }

    internal static class MdsWeaponStationBuilder
    {
        private static readonly Dictionary<string, WeaponInfo> WeaponInfoCache =
            new Dictionary<string, WeaponInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Sprite?> WeaponIconCache =
            new Dictionary<string, Sprite?>(StringComparer.OrdinalIgnoreCase);

        internal static void ApplyWeaponStations(
            Unit unit,
            ContractorPackage package,
            ModularUnitSpec spec,
            ModularSiteBehavior? site)
        {
            if (unit == null)
            {
                return;
            }

            DisableVanillaWeapons(unit);
            RemoveExistingProxies(unit);

            if (unit.weaponStations == null)
            {
                unit.weaponStations = new List<WeaponStation>(2);
            }
            else
            {
                unit.weaponStations.Clear();
            }

            site?.WeaponProxies.Clear();

            if (spec.Role == WeaponCascadeRole.FireControl ||
                spec.Role == WeaponCascadeRole.EarlyWarningRadar ||
                spec.Role == WeaponCascadeRole.CommandControl)
            {
                return;
            }

            int primaryCap = spec.PrimaryMagazineCapacity >= 0
                ? spec.PrimaryMagazineCapacity
                : Math.Max(0, spec.MagazineCapacity);
            int secondaryCap = Math.Max(0, spec.SecondaryMagazineCapacity);

            if (spec.Role == WeaponCascadeRole.CloseInGun)
            {
                WeaponInfo info = GetOrCreateGunWeaponInfo(package, spec);
                AddStation(unit, info, site, secondarySlot: false, capacity: Math.Max(1, primaryCap), rearmable: true);
                return;
            }

            if (spec.Role == WeaponCascadeRole.LaserHel)
            {
                WeaponInfo info = GetOrCreateHelWeaponInfo(package, spec);
                AddStation(unit, info, site, secondarySlot: false, capacity: 100, rearmable: false);
                return;
            }

            if (!string.IsNullOrEmpty(spec.PrimaryMissileId) &&
                ContractorRegistry.TryGetMissileSpec(spec.PrimaryMissileId, out ContractorPackage primaryPkg, out CustomMissileSpec primaryMissile))
            {
                WeaponInfo info = GetOrCreateMissileWeaponInfo(primaryPkg, primaryMissile, spec);
                AddStation(unit, info, site, secondarySlot: false, capacity: Math.Max(1, primaryCap), rearmable: true);
            }

            if (secondaryCap > 0 &&
                !string.IsNullOrEmpty(spec.SecondaryMissileId) &&
                ContractorRegistry.TryGetMissileSpec(spec.SecondaryMissileId, out ContractorPackage secondaryPkg, out CustomMissileSpec secondaryMissile))
            {
                WeaponInfo info = GetOrCreateMissileWeaponInfo(secondaryPkg, secondaryMissile, spec);
                AddStation(unit, info, site, secondarySlot: true, capacity: secondaryCap, rearmable: true);
            }
        }

        internal static void DisableVanillaWeapons(Unit unit)
        {
            Gun[] guns = unit.GetComponentsInChildren<Gun>(true);
            for (int i = 0; i < guns.Length; i++)
            {
                if (guns[i] != null)
                {
                    guns[i].enabled = false;
                }
            }

            MissileLauncher[] launchers = unit.GetComponentsInChildren<MissileLauncher>(true);
            for (int i = 0; i < launchers.Length; i++)
            {
                if (launchers[i] != null)
                {
                    launchers[i].enabled = false;
                }
            }

            Laser[] lasers = unit.GetComponentsInChildren<Laser>(true);
            for (int i = 0; i < lasers.Length; i++)
            {
                if (lasers[i] != null)
                {
                    lasers[i].enabled = false;
                }
            }

            Turret[] turrets = unit.GetComponentsInChildren<Turret>(true);
            for (int i = 0; i < turrets.Length; i++)
            {
                if (turrets[i] != null)
                {
                    turrets[i].enabled = false;
                }
            }

            FireControl[] fcs = unit.GetComponentsInChildren<FireControl>(true);
            for (int i = 0; i < fcs.Length; i++)
            {
                if (fcs[i] != null)
                {
                    fcs[i].enabled = false;
                }
            }
        }

        private static void RemoveExistingProxies(Unit unit)
        {
            MdsWeaponStationProxy[] existing = unit.GetComponents<MdsWeaponStationProxy>();
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] != null)
                {
                    UnityEngine.Object.Destroy(existing[i]);
                }
            }
        }

        private static void AddStation(
            Unit unit,
            WeaponInfo info,
            ModularSiteBehavior? site,
            bool secondarySlot,
            int capacity,
            bool rearmable)
        {
            var station = new WeaponStation(unit, cargo: false, gearSafety: false, groundSafety: false, sortWeapons: false)
            {
                WeaponInfo = info,
                Ammo = capacity,
                FullAmmo = capacity
            };

            MdsWeaponStationProxy proxy = unit.gameObject.AddComponent<MdsWeaponStationProxy>();
            proxy.Configure(unit, info, station, site, secondarySlot, capacity, rearmable);

            station.Weapons.Add(proxy);
            unit.weaponStations.Add(station);
            site?.WeaponProxies.Add(proxy);
        }

        private static WeaponInfo GetOrCreateGunWeaponInfo(ContractorPackage pkg, ModularUnitSpec spec)
        {
            string cacheKey = $"mds_wi_gun_{spec.UnitId}";
            if (!WeaponInfoCache.TryGetValue(cacheKey, out WeaponInfo? info) || info == null)
            {
                info = ScriptableObject.CreateInstance<WeaponInfo>();
                info.hideFlags = HideFlags.DontUnloadUnusedAsset;
                info.name = cacheKey;
                WeaponInfoCache[cacheKey] = info;
            }

            info.weaponName = "35mm AHEAD Airburst";
            info.shortName = "35mm AHEAD";
            info.description = $"{pkg.DisplayName} 35x228mm KETF programmable tungsten airburst CIWS.";
            info.gun = true;
            info.missile = false;
            info.energy = false;
            info.cargo = false;
            info.muzzleVelocity = Mathf.Max(400f, spec.GunMuzzleVelocityMps);
            info.maxSpeed = info.muzzleVelocity;
            info.fireInterval = Mathf.Max(0.15f, spec.FireCooldownSeconds);
            info.pierceDamage = 28f;
            info.blastDamage = Mathf.Max(15f, spec.GunDamagePerBurst);
            info.costPerRound = Mathf.Max(0.01f, spec.GunRearmCostPerRound);
            info.massPerRound = 0.75f;
            info.weaponIcon = TryLoadWeaponIconSprite(pkg, spec.WeaponIconRelativePath);
            info.visibilityWhenFired = 150f;
            info.targetRequirements = new TargetRequirements
            {
                lineOfSight = true,
                minRange = Mathf.Max(20f, spec.MinEngagementRangeKm * 1000f),
                maxRange = Mathf.Max(1000f, spec.MaxEngagementRangeKm * 1000f),
                minAltitude = 0f,
                maxAltitude = 4500f,
                maxSpeed = 1600f
            };
            info.effectiveness = new RoleIdentity
            {
                antiAir = 0.85f,
                antiMissile = 0.92f,
                antiSurface = 0.35f,
                antiRadar = 0f
            };
            return info;
        }

        private static WeaponInfo GetOrCreateHelWeaponInfo(ContractorPackage pkg, ModularUnitSpec spec)
        {
            string cacheKey = $"mds_wi_hel_{spec.UnitId}";
            if (!WeaponInfoCache.TryGetValue(cacheKey, out WeaponInfo? info) || info == null)
            {
                info = ScriptableObject.CreateInstance<WeaponInfo>();
                info.hideFlags = HideFlags.DontUnloadUnusedAsset;
                info.name = cacheKey;
                WeaponInfoCache[cacheKey] = info;
            }

            info.weaponName = "Skyshield HEL Laser";
            info.shortName = "HEL Laser";
            info.description = $"{pkg.DisplayName} solid-state high-energy directed laser effector.";
            info.energy = true;
            info.gun = false;
            info.missile = false;
            info.cargo = false;
            info.muzzleVelocity = 299792458f;
            info.maxSpeed = info.muzzleVelocity;
            info.fireInterval = Mathf.Max(0.2f, spec.FireCooldownSeconds);
            info.pierceDamage = 0f;
            info.blastDamage = 140f * Mathf.Max(0.5f, spec.LaserPowerFactor);
            info.costPerRound = 0f;
            info.massPerRound = 0f;
            info.weaponIcon = TryLoadWeaponIconSprite(pkg, spec.WeaponIconRelativePath);
            info.visibilityWhenFired = 60f;
            info.targetRequirements = new TargetRequirements
            {
                lineOfSight = true,
                minRange = Mathf.Max(20f, spec.MinEngagementRangeKm * 1000f),
                maxRange = Mathf.Max(1000f, spec.MaxEngagementRangeKm * 1000f),
                minAltitude = 0f,
                maxAltitude = 5500f,
                maxSpeed = 2500f
            };
            info.effectiveness = new RoleIdentity
            {
                antiAir = 0.75f,
                antiMissile = 0.98f,
                antiSurface = 0.20f,
                antiRadar = 0f
            };
            return info;
        }

        private static WeaponInfo GetOrCreateMissileWeaponInfo(
            ContractorPackage pkg,
            CustomMissileSpec missileSpec,
            ModularUnitSpec siteSpec)
        {
            string cacheKey = $"mds_wi_msl_{missileSpec.MissileId}_{siteSpec.UnitId}";
            if (!WeaponInfoCache.TryGetValue(cacheKey, out WeaponInfo? info) || info == null)
            {
                info = ScriptableObject.CreateInstance<WeaponInfo>();
                info.hideFlags = HideFlags.DontUnloadUnusedAsset;
                info.name = cacheKey;
                WeaponInfoCache[cacheKey] = info;
            }

            float effRangeKm = Mathf.Max(missileSpec.MaxEngagementRangeKm, siteSpec.MaxEngagementRangeKm);
            bool isAntiShip = missileSpec.IsAntiShip || siteSpec.Role == WeaponCascadeRole.AntiShipCoastal;
            info.weaponName = missileSpec.DisplayName;
            info.shortName = string.IsNullOrEmpty(missileSpec.AircraftMountShortName)
                ? missileSpec.DisplayName
                : missileSpec.AircraftMountShortName;
            info.description = isAntiShip
                ? $"{pkg.DisplayName} | {missileSpec.FlightProfile} AShM ({effRangeKm:0.#} km)."
                : $"{pkg.DisplayName} | {missileSpec.FlightProfile} SAM ({effRangeKm:0.#} km).";
            info.missile = true;
            info.gun = false;
            info.energy = false;
            info.cargo = false;
            info.overHorizon = isAntiShip || effRangeKm >= 25f;
            info.muzzleVelocity = isAntiShip ? 306f : Mathf.Max(400f, missileSpec.CruiseSpeedMps);
            info.maxSpeed = isAntiShip ? 306f : Mathf.Max(400f, missileSpec.CruiseSpeedMps);
            info.fireInterval = Mathf.Max(0.5f, siteSpec.FireCooldownSeconds);
            info.blastDamage = Mathf.Max(10f, missileSpec.BlastYield);
            info.costPerRound = Mathf.Max(0.5f, missileSpec.RearmCostPerRound);
            info.massPerRound = isAntiShip
                ? Mathf.Clamp(missileSpec.AircraftMountMassKg > 0f ? missileSpec.AircraftMountMassKg : 416f, 120f, 450f)
                : 120f;
            info.weaponIcon = TryLoadWeaponIconSprite(pkg, missileSpec.IconRelativePath);
            info.visibilityWhenFired = isAntiShip ? 120f : 240f;
            info.targetRequirements = new TargetRequirements
            {
                lineOfSight = !info.overHorizon,
                minRange = Mathf.Max(200f, siteSpec.MinEngagementRangeKm * 1000f),
                maxRange = effRangeKm * 1000f,
                minAltitude = 0f,
                maxAltitude = isAntiShip ? 120f : (missileSpec.IsAbmInterceptor ? 60000f : 25000f),
                maxSpeed = isAntiShip ? 60f : (missileSpec.IsAbmInterceptor ? 5000f : 2200f)
            };
            info.effectiveness = isAntiShip
                ? new RoleIdentity
                {
                    antiAir = 0f,
                    antiMissile = 0f,
                    antiSurface = 0.96f,
                    antiRadar = 0f
                }
                : new RoleIdentity
                {
                    antiAir = 0.92f,
                    antiMissile = missileSpec.IsAbmInterceptor ? 1f : 0.85f,
                    antiSurface = 0f,
                    antiRadar = missileSpec.SupportsActiveToPassiveHoj ? 0.95f : 0f
                };

            if (isAntiShip)
            {
                ContractorRegistry.RegisterAntiShipWeaponInfo(info);
            }

            return info;
        }

        internal static Sprite? TryLoadWeaponIconSprite(ContractorPackage? pkg, string? relativePath)
        {
            if (pkg == null || string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(pkg.AssetRootDirectory))
            {
                return null;
            }

            string cacheKey = $"{pkg.AssetRootDirectory}|{relativePath}";
            if (WeaponIconCache.TryGetValue(cacheKey, out Sprite? cached))
            {
                return cached;
            }

            string? fullPath = CustomMissileBehavior.ResolveAssetPath(
                pkg,
                relativePath!,
                new[]
                {
                    Path.Combine("Assets", relativePath!),
                    Path.GetFileName(relativePath!)
                });

            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                WeaponIconCache[cacheKey] = null;
                return null;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
                {
                    name = $"MDS_Icon_{Path.GetFileNameWithoutExtension(fullPath)}",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontUnloadUnusedAsset
                };

                if (!tex.LoadImage(bytes, markNonReadable: true) || tex.width <= 0 || tex.height <= 0)
                {
                    UnityEngine.Object.Destroy(tex);
                    WeaponIconCache[cacheKey] = null;
                    return null;
                }

                Sprite sprite = Sprite.Create(
                    tex,
                    new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
                WeaponIconCache[cacheKey] = sprite;
                return sprite;
            }
            catch (Exception ex)
            {
                MdsLog.Warn($"[Icons] Failed to load weapon icon '{fullPath}': {ex.Message}");
                WeaponIconCache[cacheKey] = null;
                return null;
            }
        }
    }
}
