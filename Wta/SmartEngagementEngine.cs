using System;
using System.Collections.Generic;
using MDSFramework.Api;
using MDSFramework.Missiles;
using MDSFramework.Sensors;
using UnityEngine;

namespace MDSFramework.Wta
{
    internal sealed class SmartEngagementEngine
    {
        private const int MaxThreats = 192;
        private const int MaxEffectors = 64;
        private const int MaxOrders = 64;

        internal static SmartEngagementEngine? Instance { get; private set; }

        private readonly ThreatSnapshot[] threatBuffer = new ThreatSnapshot[MaxThreats];
        private readonly Unit?[] threatUnitBuffer = new Unit?[MaxThreats];
        private readonly EffectorSnapshot[] effectorBuffer = new EffectorSnapshot[MaxEffectors];
        private readonly ModularSiteBehavior?[] effectorSiteBuffer = new ModularSiteBehavior?[MaxEffectors];
        private readonly EngagementOrder[] orderBuffer = new EngagementOrder[MaxOrders];

        private readonly Dictionary<uint, int> seenUnitToThreatIndex = new Dictionary<uint, int>(256);
        private readonly Dictionary<uint, int> activeCustomMissilesByTarget = new Dictionary<uint, int>(64);
        private readonly Dictionary<uint, int> activeHojMissilesByTarget = new Dictionary<uint, int>(32);
        private readonly Dictionary<FactionAffinity, float> nextTickTimeByFaction = new Dictionary<FactionAffinity, float>(4);
        private readonly Dictionary<FactionAffinity, float> lastTickTimeByFaction = new Dictionary<FactionAffinity, float>(4);
        private readonly Dictionary<FactionAffinity, bool> activeStateByFaction = new Dictionary<FactionAffinity, bool>(4);
        private readonly WeaponCascadeRole[] effectiveCascadeScratch = new WeaponCascadeRole[12];
        private readonly bool[] aimedThisTick = new bool[MaxEffectors];
        private readonly bool[] effectorUsedThisTick = new bool[MaxEffectors];
        private readonly int[] threatOrderScratch = new int[MaxThreats];
        private readonly int[] closeInAssignedThisTick = new int[MaxThreats];
        private readonly ModularSiteBehavior?[] firstNetShooterScratch = new ModularSiteBehavior?[MaxThreats];

        internal int LastThreatCount { get; private set; }
        internal int LastOrderCount { get; private set; }
        internal bool AnyFactionActive { get; private set; }

        internal SmartEngagementEngine()
        {
            Instance = this;
        }

        internal void ResetSession()
        {
            Array.Clear(threatUnitBuffer, 0, threatUnitBuffer.Length);
            Array.Clear(effectorSiteBuffer, 0, effectorSiteBuffer.Length);
            seenUnitToThreatIndex.Clear();
            activeCustomMissilesByTarget.Clear();
            activeHojMissilesByTarget.Clear();
            nextTickTimeByFaction.Clear();
            lastTickTimeByFaction.Clear();
            activeStateByFaction.Clear();
            LastThreatCount = 0;
            LastOrderCount = 0;
            AnyFactionActive = false;
        }

        internal void Tick(float now)
        {
            if (!MdsConfig.EnableSmartWta.Value ||
                !GameInteropHelpers.IsGameplayActive() ||
                !GameInteropHelpers.IsServerAuthority())
            {
                return;
            }

            if (SensorNetworkManager.Instance == null || SensorNetworkManager.Instance.AllSites.Count == 0)
            {
                LastThreatCount = 0;
                LastOrderCount = 0;
                AnyFactionActive = false;
                return;
            }

            bool anyActive = false;
            bool anyEvaluated = false;
            int totalThreats = 0;
            int totalOrders = 0;

            EvaluateFactionIfDue(FactionAffinity.Boscali, now, ref totalThreats, ref totalOrders, ref anyActive, ref anyEvaluated);
            EvaluateFactionIfDue(FactionAffinity.Primeva, now, ref totalThreats, ref totalOrders, ref anyActive, ref anyEvaluated);
            EvaluateFactionIfDue(FactionAffinity.Any, now, ref totalThreats, ref totalOrders, ref anyActive, ref anyEvaluated);

            if (anyEvaluated)
            {
                LastThreatCount = totalThreats;
                LastOrderCount = totalOrders;
            }

            AnyFactionActive = anyActive;
        }

        private void EvaluateFactionIfDue(
            FactionAffinity faction,
            float now,
            ref int totalThreats,
            ref int totalOrders,
            ref bool anyActive,
            ref bool anyEvaluated)
        {
            IReadOnlyList<ModularSiteBehavior> allSites = SensorNetworkManager.Instance!.AllSites;
            int effectorCount = 0;
            SmartEngagementProfile? profile = null;

            for (int i = 0; i < allSites.Count && effectorCount < MaxEffectors; i++)
            {
                ModularSiteBehavior site = allSites[i];
                if (!site.IsAlive || site.SuspendedInTransit || site.Faction != faction || site.Unit == null)
                {
                    continue;
                }

                if (site.IsHoldFireActive)
                {
                    site.TurretAim?.ClearAim();
                    site.TurretAim?.HideBeam();
                    site.ActiveHelTargetId = PersistentID.None;
                    site.ActiveHelDwellSeconds = 0f;
                    continue;
                }

                profile ??= site.Package?.DefaultWtaProfile;

                if (site.Spec.Role == WeaponCascadeRole.FireControl ||
                    site.Spec.Role == WeaponCascadeRole.EarlyWarningRadar ||
                    site.Spec.Role == WeaponCascadeRole.CommandControl)
                {
                    continue;
                }

                float minRange = Mathf.Max(0f, site.Spec.MinEngagementRangeKm * 1000f);
                float fullMaxRange = Mathf.Max(1000f, site.Spec.MaxEngagementRangeKm * 1000f);
                float maxRange = fullMaxRange;
                float eoirBackup = site.Spec.SensorProfile != null
                    ? Mathf.Max(0f, site.Spec.SensorProfile.EoIrEmergencyRangeKm * 1000f)
                    : 0f;

                if (site.EmconState == FcuEmconState.EoIrEmergencyOnly && eoirBackup > 0f)
                {
                    maxRange = Mathf.Min(maxRange, eoirBackup);
                }

                int totalCap = site.PrimaryMagazineSize + site.SecondaryMagazineSize;
                float ammoRatio = totalCap > 0 ? Mathf.Clamp01((float)site.AmmoRemaining / totalCap) : 1f;

                effectorSiteBuffer[effectorCount] = site;
                effectorBuffer[effectorCount] = new EffectorSnapshot
                {
                    SiteIndex = i,
                    Role = site.Spec.Role,
                    WorldPos = site.Unit.transform.position,
                    MinRangeMeters = minRange,
                    MaxRangeMeters = maxRange,
                    FullMaxRangeMeters = fullMaxRange,
                    IsDeployed = site.IsDeployed,
                    IsReadyToFire = site.IsDeployed && !site.IsVehicleMoving && site.CooldownRemaining <= 0.01f &&
                                    (site.Spec.Role == WeaponCascadeRole.LaserHel || site.AmmoRemaining > 0),
                    HasFcuGuidance = site.HasFireControlGuidance,
                    HasPassiveHojMissile = SiteHasReadyPassiveHojMissile(site),
                    EoIrBackupRangeMeters = eoirBackup,
                    BandMask = site.SupportedBandMask,
                    JamLevel = site.CurrentJamLevel,
                    JamTolerance = site.Spec.SensorProfile != null
                        ? Mathf.Clamp(site.Spec.SensorProfile.JamTolerance + site.EffectiveNetworkEccmBonus, 0f, 0.92f)
                        : 0.5f,
                    IsInCommandNet = site.IsInCommandNet && site.CommandNetId != 0,
                    CommandNetId = site.CommandNetId,
                    AmmoRatio = ammoRatio
                };
                effectorCount++;
            }

            if (effectorCount == 0 && !HasRadarOnlySitesForFaction(allSites, faction, out profile))
            {
                return;
            }

            profile ??= SmartEngagementProfile.CreateDefault();

            if (!nextTickTimeByFaction.TryGetValue(faction, out float nextDue))
            {
                nextDue = 0f;
            }

            if (now < nextDue)
            {
                if (activeStateByFaction.TryGetValue(faction, out bool wasActive) && wasActive)
                {
                    anyActive = true;
                }

                return;
            }

            FactionHQ? myHq = GameInteropHelpers.TryResolveFactionHQ(faction);
            int threatCount = GatherThreatsForFaction(faction, myHq, allSites, now);

            bool hasNearThreats = UpdateAutoPopUpAndStow(allSites, faction, profile, threatCount, now);
            bool activeMode = threatCount > 0 && hasNearThreats;
            activeStateByFaction[faction] = activeMode;
            if (activeMode)
            {
                anyActive = true;
            }

            float hz = activeMode
                ? (MdsConfig.ActiveTickHzOverride.Value > 0.05f ? MdsConfig.ActiveTickHzOverride.Value : Mathf.Max(0.5f, profile.ActiveTickHz))
                : (MdsConfig.IdleTickHzOverride.Value > 0.05f ? MdsConfig.IdleTickHzOverride.Value : Mathf.Max(0.25f, profile.IdleTickHz));

            float dtStep = 1f / hz;
            float actualDt = lastTickTimeByFaction.TryGetValue(faction, out float prevTick)
                ? Mathf.Clamp(now - prevTick, 0.01f, 0.5f)
                : dtStep;
            lastTickTimeByFaction[faction] = now;
            nextTickTimeByFaction[faction] = now + dtStep;
            anyEvaluated = true;

            int orderCount = 0;
            if (threatCount > 0 && effectorCount > 0)
            {
                orderCount = AssignTargetsByCascade(profile, effectorCount, threatCount);
                ExecuteEngagementOrders(allSites, profile, orderCount, actualDt, now);
            }
            else
            {
                ClearIdleEffectors(effectorCount);
            }

            totalThreats += threatCount;
            totalOrders += orderCount;

            // Clear only used slots to avoid stale unit references without allocating.
            for (int i = 0; i < threatCount; i++)
            {
                threatUnitBuffer[i] = null;
            }

            for (int i = 0; i < effectorCount; i++)
            {
                effectorSiteBuffer[i] = null;
            }
        }

        private static bool HasRadarOnlySitesForFaction(
            IReadOnlyList<ModularSiteBehavior> allSites,
            FactionAffinity faction,
            out SmartEngagementProfile? profile)
        {
            profile = null;
            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior s = allSites[i];
                if (s.IsAlive && s.Faction == faction)
                {
                    profile ??= s.Package?.DefaultWtaProfile;
                    return true;
                }
            }

            return false;
        }

        private int GatherThreatsForFaction(
            FactionAffinity faction,
            FactionHQ? myHq,
            IReadOnlyList<ModularSiteBehavior> allSites,
            float now)
        {
            seenUnitToThreatIndex.Clear();
            PopulateActiveCustomMissilesByTarget(faction, myHq);
            int count = 0;
            List<Unit>? allUnits = UnitRegistry.allUnits;

            // Pass 1: Prioritize known ballistic & nuclear missiles before MaxThreats can saturate.
            if (myHq != null && myHq.trackingDatabase != null)
            {
                foreach (KeyValuePair<PersistentID, TrackingInfo> kv in myHq.trackingDatabase)
                {
                    if (count >= MaxThreats)
                    {
                        break;
                    }

                    TrackingInfo info = kv.Value;
                    if (info == null || !info.TryGetUnit(out Unit target) || target == null || target.disabled || target is not Missile m)
                    {
                        continue;
                    }

                    if (!GameInteropHelpers.IsHostileToAffinity(m, faction, myHq) || !IsBallisticOrNuclearThreat(m))
                    {
                        continue;
                    }

                    AddOrUpdateThreat(m, info.missileAttacks, ref count);
                }
            }

            if (allUnits != null)
            {
                for (int i = 0; i < allUnits.Count && count < MaxThreats; i++)
                {
                    if (allUnits[i] is not Missile m || m.disabled || !GameInteropHelpers.IsHostileToAffinity(m, faction, myHq))
                    {
                        continue;
                    }

                    if (AbmEarlyWarningService.Instance != null && AbmEarlyWarningService.Instance.IsKnownBallisticOrNuclear(m))
                    {
                        AddOrUpdateThreat(m, GetExistingMissileAttacks(myHq, m), ref count);
                    }
                }
            }

            // Pass 2: Remaining hostile missiles (tracked by HQ or detected with LOS by friendly sites).
            if (myHq != null && myHq.trackingDatabase != null)
            {
                foreach (KeyValuePair<PersistentID, TrackingInfo> kv in myHq.trackingDatabase)
                {
                    if (count >= MaxThreats)
                    {
                        break;
                    }

                    TrackingInfo info = kv.Value;
                    if (info == null || !info.TryGetUnit(out Unit target) || target == null || target.disabled || target is not Missile)
                    {
                        continue;
                    }

                    if (!GameInteropHelpers.IsHostileToAffinity(target, faction, myHq))
                    {
                        continue;
                    }

                    AddOrUpdateThreat(target, info.missileAttacks, ref count);
                }
            }

            if (allUnits != null)
            {
                for (int i = 0; i < allUnits.Count && count < MaxThreats; i++)
                {
                    if (allUnits[i] is not Missile m || m.disabled || !GameInteropHelpers.IsHostileToAffinity(m, faction, myHq))
                    {
                        continue;
                    }

                    if (IsWithinAnyFriendlySiteSensor(m, allSites, faction))
                    {
                        AddOrUpdateThreat(m, GetExistingMissileAttacks(myHq, m), ref count);
                    }
                }
            }

            // Pass 3: Hostile aircraft (tracked by HQ or within friendly site sensors + LOS, excluding splashed wrecks/ejected).
            if (myHq != null && myHq.trackingDatabase != null)
            {
                foreach (KeyValuePair<PersistentID, TrackingInfo> kv in myHq.trackingDatabase)
                {
                    if (count >= MaxThreats)
                    {
                        break;
                    }

                    TrackingInfo info = kv.Value;
                    if (info == null || !info.TryGetUnit(out Unit target) || target == null || target.disabled || target is not Aircraft)
                    {
                        continue;
                    }

                    if (GameInteropHelpers.IsAircraftSplashed(target) ||
                        !GameInteropHelpers.IsHostileToAffinity(target, faction, myHq))
                    {
                        continue;
                    }

                    AddOrUpdateThreat(target, info.missileAttacks, ref count);
                }
            }

            if (allUnits != null)
            {
                for (int i = 0; i < allUnits.Count && count < MaxThreats; i++)
                {
                    if (allUnits[i] is not Aircraft ac ||
                        ac.disabled ||
                        GameInteropHelpers.IsAircraftSplashed(ac) ||
                        !GameInteropHelpers.IsHostileToAffinity(ac, faction, myHq))
                    {
                        continue;
                    }

                    if (IsWithinAnyFriendlySiteSensor(ac, allSites, faction))
                    {
                        AddOrUpdateThreat(ac, GetExistingMissileAttacks(myHq, ac), ref count);
                    }
                }
            }

            // Pass 4: Inject triangulated hostile Home-on-Jam tracks from SensorNetworkManager.
            if (SensorNetworkManager.Instance != null)
            {
                IReadOnlyList<HojTrackEntry> hojTracks = SensorNetworkManager.Instance.ActiveHojTracks;
                for (int i = 0; i < hojTracks.Count && count < MaxThreats; i++)
                {
                    HojTrackEntry entry = hojTracks[i];
                    if (entry.Faction != faction ||
                        entry.JammerUnit == null ||
                        entry.JammerUnit.disabled ||
                        GameInteropHelpers.IsAircraftSplashed(entry.JammerUnit))
                    {
                        continue;
                    }

                    if (!GameInteropHelpers.IsHostileToAffinity(entry.JammerUnit, faction, myHq))
                    {
                        continue;
                    }

                    uint idKey = entry.JammerId.IsValid
                        ? entry.JammerId.Id
                        : (uint)entry.JammerUnit.GetInstanceID();

                    if (seenUnitToThreatIndex.TryGetValue(idKey, out int idx))
                    {
                        ThreatSnapshot existing = threatBuffer[idx];
                        existing.IsJammer = true;
                        existing.IsActivelyJamming = true;
                        existing.IsJammingFriendlyFcu |= entry.VictimIsRadarOrFcu;
                        existing.WorldPos = entry.TriangulatedWorldPos;
                        existing.Velocity = entry.Velocity;
                        existing.PriorityScore += entry.VictimIsRadarOrFcu ? 85f : 45f;
                        threatBuffer[idx] = existing;
                    }
                    else
                    {
                        AddOrUpdateThreat(
                            entry.JammerUnit,
                            GetExistingMissileAttacks(myHq, entry.JammerUnit),
                            ref count,
                            forceJammer: true,
                            isActivelyJamming: true,
                            isJammingFriendlyFcu: entry.VictimIsRadarOrFcu,
                            overridePos: entry.TriangulatedWorldPos,
                            overrideVel: entry.Velocity);
                    }
                }
            }

            // Pass 5: Hostile naval surface ships (tracked by HQ or within coastal ASuW launcher / friendly sensor range).
            if (myHq != null && myHq.trackingDatabase != null)
            {
                foreach (KeyValuePair<PersistentID, TrackingInfo> kv in myHq.trackingDatabase)
                {
                    if (count >= MaxThreats)
                    {
                        break;
                    }

                    TrackingInfo info = kv.Value;
                    if (info == null || !info.TryGetUnit(out Unit target) || target == null || target.disabled || target is not Ship ship)
                    {
                        continue;
                    }

                    if (!GameInteropHelpers.IsHostileToAffinity(ship, faction, myHq))
                    {
                        continue;
                    }

                    AddOrUpdateThreat(ship, info.missileAttacks, ref count);
                }
            }

            if (allUnits != null)
            {
                for (int i = 0; i < allUnits.Count && count < MaxThreats; i++)
                {
                    if (allUnits[i] is not Ship ship ||
                        ship.disabled ||
                        !GameInteropHelpers.IsHostileToAffinity(ship, faction, myHq))
                    {
                        continue;
                    }

                    if (IsWithinAnyAntiShipOrFriendlySensor(ship, allSites, faction))
                    {
                        AddOrUpdateThreat(ship, GetExistingMissileAttacks(myHq, ship), ref count);
                    }
                }
            }

            _ = now;
            return count;
        }

        private static bool IsWithinAnyAntiShipOrFriendlySensor(Ship ship, IReadOnlyList<ModularSiteBehavior> allSites, FactionAffinity faction)
        {
            if (IsWithinAnyFriendlySiteSensor(ship, allSites, faction))
            {
                return true;
            }

            Vector3 shipPos = ship.transform.position;
            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior site = allSites[i];
                if (!site.IsAlive || site.SuspendedInTransit || site.Faction != faction || site.Unit == null)
                {
                    continue;
                }

                if (site.Spec.Role == WeaponCascadeRole.AntiShipCoastal && site.AmmoRemaining > 0)
                {
                    float maxSq = Mathf.Pow(Mathf.Max(1000f, site.Spec.MaxEngagementRangeKm * 1000f), 2f);
                    if ((site.Unit.transform.position - shipPos).sqrMagnitude <= maxSq)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal void RequestImmediateEvaluate(FactionAffinity faction)
        {
            nextTickTimeByFaction[faction] = 0f;
        }

        private static bool SiteHasReadyPassiveHojMissile(ModularSiteBehavior site)
        {
            if (site == null)
            {
                return false;
            }

            if (site.PrimaryAmmoRemaining > 0 &&
                !string.IsNullOrEmpty(site.Spec.PrimaryMissileId) &&
                ContractorRegistry.TryGetMissileSpec(site.Spec.PrimaryMissileId, out _, out CustomMissileSpec s1) &&
                s1.SupportsActiveToPassiveHoj)
            {
                return true;
            }

            if (site.SecondaryAmmoRemaining > 0 &&
                !string.IsNullOrEmpty(site.Spec.SecondaryMissileId) &&
                ContractorRegistry.TryGetMissileSpec(site.Spec.SecondaryMissileId, out _, out CustomMissileSpec s2) &&
                s2.SupportsActiveToPassiveHoj)
            {
                return true;
            }

            return false;
        }

        private void PopulateActiveCustomMissilesByTarget(FactionAffinity faction, FactionHQ? myHq)
        {
            activeCustomMissilesByTarget.Clear();
            activeHojMissilesByTarget.Clear();
            foreach (CustomMissileBehavior beh in CustomMissileTracker.ActiveByUnitId.Values)
            {
                if (beh == null || beh.Missile == null || beh.Missile.disabled)
                {
                    continue;
                }

                bool sameFaction = beh.OwnerFaction == faction ||
                                   (myHq != null && ReferenceEquals(beh.Missile.NetworkHQ ?? beh.Missile.MapHQ, myHq));
                if (!sameFaction)
                {
                    continue;
                }

                PersistentID tid = beh.TargetUnit != null && !beh.TargetUnit.disabled
                    ? beh.TargetUnit.persistentID
                    : beh.Missile.targetID;
                if (!tid.IsValid)
                {
                    continue;
                }

                activeCustomMissilesByTarget.TryGetValue(tid.Id, out int inFlightCount);
                activeCustomMissilesByTarget[tid.Id] = inFlightCount + 1;

                if (beh.Spec != null && beh.Spec.SupportsActiveToPassiveHoj)
                {
                    activeHojMissilesByTarget.TryGetValue(tid.Id, out int hojInFlightCount);
                    activeHojMissilesByTarget[tid.Id] = hojInFlightCount + 1;
                }
            }
        }

        private static bool IsBallisticOrNuclearThreat(Missile m)
        {
            if (AbmEarlyWarningService.Instance != null && AbmEarlyWarningService.Instance.IsKnownBallisticOrNuclear(m))
            {
                return true;
            }

            return m.GetComponent<BallisticMissileGuidance>() != null ||
                   GameInteropHelpers.SafeGetBlastYield(m) > MdsConfig.AbmBlastYieldThreshold.Value;
        }

        private static int GetExistingMissileAttacks(FactionHQ? myHq, Unit u)
        {
            if (myHq != null && u.persistentID.IsValid)
            {
                TrackingInfo? ti = myHq.GetTrackingData(u.persistentID);
                if (ti != null)
                {
                    return ti.missileAttacks;
                }
            }

            return 0;
        }

        private void AddOrUpdateThreat(
            Unit target,
            int existingAttacks,
            ref int count,
            bool forceJammer = false,
            bool isActivelyJamming = false,
            bool isJammingFriendlyFcu = false,
            Vector3? overridePos = null,
            Vector3? overrideVel = null)
        {
            if (count >= MaxThreats)
            {
                return;
            }

            uint idVal = target.persistentID.IsValid ? target.persistentID.Id : (uint)target.GetInstanceID();
            if (seenUnitToThreatIndex.ContainsKey(idVal))
            {
                return;
            }

            Vector3 pos = overridePos ?? target.transform.position;
            Vector3 vel = overrideVel ?? (target.rb != null ? target.rb.velocity : Vector3.zero);
            float alt = pos.y - Datum.LocalSeaY;
            bool isMissile = target is Missile;
            bool isAircraft = target is Aircraft;
            bool isShip = target is Ship;
            bool isBallistic = false;
            bool isNuclear = false;
            bool isArad = false;
            float yield = 0f;
            float thermalHardening = 1.0f;
            bool isDecoy = false;
            bool isArtilleryShell = false;
            bool isShellOrSmallMunition = false;

            if (target is Missile m)
            {
                yield = GameInteropHelpers.SafeGetBlastYield(m);
                isNuclear = yield > MdsConfig.AbmBlastYieldThreshold.Value;
                isBallistic = AbmEarlyWarningService.Instance != null
                    ? AbmEarlyWarningService.Instance.IsKnownBallisticOrNuclear(m)
                    : (m.GetComponent<BallisticMissileGuidance>() != null || isNuclear);
                isArad = m.GetComponent<ARMSeeker>() != null;

                if (isBallistic || isNuclear)
                {
                    AbmEarlyWarningService.Instance?.NotifyPotentialBallisticLaunch(m, isBallistic);
                }

                if (CustomMissileTracker.TryGet(m, out CustomMissileBehavior custom))
                {
                    thermalHardening = custom.ThermalHardening;
                    isDecoy = custom.IsDecoy;
                }

                ClassifySmallMunition(m, yield, isNuclear, isBallistic, isArad, out isArtilleryShell, out isShellOrSmallMunition);
            }

            bool isEmittingRadar = target.HasRadarEmission();
            bool isJammer = forceJammer || isActivelyJamming || target.GetComponentInChildren<JammingPod>(true) != null;

            float shipSizeMetric = isShip
                ? (target.definition != null
                    ? Mathf.Max(target.definition.value, target.maxRadius * 15f)
                    : (target.maxRadius * 15f))
                : 0f;

            float priority = 20f;
            if (isNuclear) priority += 200f;
            if (isBallistic) priority += 120f;
            if (isArad) priority += 90f;
            if (isMissile) priority += 55f;
            if (isJammer) priority += 40f;
            if (isJammingFriendlyFcu) priority += 85f;
            else if (isActivelyJamming) priority += 45f;
            if (isDecoy) priority -= 35f;
            if (isShip)
            {
                priority = 45f + Mathf.Clamp(shipSizeMetric * 0.04f, 0f, 90f) + (isEmittingRadar ? 15f : 0f);
            }

            seenUnitToThreatIndex[idVal] = count;
            threatUnitBuffer[count] = target;
            int effectiveExistingAttacks = existingAttacks;
            if (target.persistentID.IsValid &&
                activeCustomMissilesByTarget.TryGetValue(target.persistentID.Id, out int customInFlight) &&
                customInFlight > effectiveExistingAttacks)
            {
                effectiveExistingAttacks = customInFlight;
            }

            int activeHojInFlight = 0;
            if (target.persistentID.IsValid)
            {
                activeHojMissilesByTarget.TryGetValue(target.persistentID.Id, out activeHojInFlight);
            }

            threatBuffer[count] = new ThreatSnapshot
            {
                UnitIndex = count,
                UnitId = target.persistentID,
                WorldPos = pos,
                Velocity = vel,
                AltitudeMeters = alt,
                Rcs = Mathf.Max(0.02f, target.RCS),
                BlastYield = yield,
                ThermalHardening = thermalHardening,
                IsMissile = isMissile,
                IsAircraft = isAircraft,
                IsBallistic = isBallistic,
                IsNuclear = isNuclear,
                IsArad = isArad,
                IsJammer = isJammer,
                IsActivelyJamming = isActivelyJamming,
                IsJammingFriendlyFcu = isJammingFriendlyFcu,
                IsEmittingRadar = isEmittingRadar,
                IsDecoy = isDecoy,
                IsShellOrSmallMunition = isShellOrSmallMunition,
                IsArtilleryShell = isArtilleryShell,
                IsShip = isShip,
                TargetMaxHp = shipSizeMetric,
                ExistingMissileAttacks = Math.Max(0, effectiveExistingAttacks),
                ActiveHojMissileAttacks = Math.Max(0, activeHojInFlight),
                AssignedThisTick = 0,
                AssignedHojThisTick = 0,
                PriorityScore = priority
            };
            count++;
        }

        private static void ClassifySmallMunition(
            Missile m,
            float yield,
            bool isNuclear,
            bool isBallistic,
            bool isArad,
            out bool isArtilleryShell,
            out bool isShellOrSmallMunition)
        {
            isArtilleryShell = false;
            isShellOrSmallMunition = false;

            if (isNuclear || isBallistic || isArad)
            {
                return;
            }

            object? seekerObj = GameInteropHelpers.MissileSeekerField?.GetValue(m);
            if (seekerObj != null &&
                seekerObj.GetType().Name.IndexOf("Shell", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isArtilleryShell = true;
                isShellOrSmallMunition = true;
                return;
            }

            Component[] components = m.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component? comp = components[i];
                if (comp != null &&
                    comp.GetType().Name.IndexOf("Shell", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    isArtilleryShell = true;
                    isShellOrSmallMunition = true;
                    return;
                }
            }

            string key = m.definition != null ? m.definition.jsonKey : string.Empty;
            string unitName = m.definition != null ? m.definition.unitName : string.Empty;
            string combined = $"{key} {unitName} {m.name}";

            if (combined.IndexOf("shell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("155mm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("120mm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("105mm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("artillery", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("mortar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("howitzer", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isArtilleryShell = true;
                isShellOrSmallMunition = true;
                return;
            }

            if (combined.IndexOf("pab-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("gpo-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("gbm-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("bomb", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("agr-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("rocket", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("lynchpin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("kingpin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("glide", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isShellOrSmallMunition = true;
                return;
            }

            float mass = (m.definition != null && m.definition.mass > 0f)
                ? m.definition.mass
                : (m.rb != null ? m.rb.mass : 0f);
            if (yield < 25f && mass > 0f && mass < 95f)
            {
                isShellOrSmallMunition = true;
            }
        }

        private static bool IsWithinAnyFriendlySiteSensor(Unit target, IReadOnlyList<ModularSiteBehavior> allSites, FactionAffinity faction)
        {
            Vector3 targetPos = target.transform.position;
            float speed = target.rb != null ? target.rb.velocity.magnitude : 0f;
            bool emitsRadar = target.HasRadarEmission();
            bool targetIsJamming = SensorNetworkManager.Instance != null &&
                                   SensorNetworkManager.Instance.HasActiveHojTrack(faction, target);

            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior site = allSites[i];
                if (!site.IsAlive ||
                    (site.IsCommandCenter && !SensorBandPhysics.HasBand(site.SupportedBandMask, SensorBand.PassiveElint)) ||
                    site.Faction != faction ||
                    site.Unit == null ||
                    site.Spec.SensorProfile == null)
                {
                    continue;
                }

                float detectRange = SensorBandPhysics.ComputeEffectiveDetectionRangeMeters(
                    site.Spec.SensorProfile,
                    site.IsRadarEmitting,
                    Mathf.Max(0.02f, target.RCS),
                    speed,
                    emitsRadar,
                    targetIsJamming: targetIsJamming,
                    site.CurrentJamLevel,
                    out _,
                    out _);
                detectRange *= Mathf.Max(1f, site.EffectiveNetworkDetectionMultiplier);

                float maxDistSq = detectRange * detectRange;
                Vector3 siteEye = site.Unit.transform.position + (Vector3.up * 3f);
                if ((site.Unit.transform.position - targetPos).sqrMagnitude <= maxDistSq &&
                    !Physics.Linecast(siteEye, targetPos, (int)PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                {
                    return true;
                }
            }

            return false;
        }

        private bool UpdateAutoPopUpAndStow(
            IReadOnlyList<ModularSiteBehavior> allSites,
            FactionAffinity faction,
            SmartEngagementProfile profile,
            int threatCount,
            float now)
        {
            float popUpRangeM = Mathf.Max(2000f, profile.AutoPopUpRangeKm * 1000f);
            float popUpSq = popUpRangeM * popUpRangeM;
            bool anyNearThreat = false;

            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior site = allSites[i];
                if (!site.IsAlive || site.SuspendedInTransit || site.Faction != faction || site.Unit == null)
                {
                    continue;
                }

                if (site.IsHoldFireActive || site.IsVehicleMoving)
                {
                    site.TurretAim?.ClearAim();
                    site.TurretAim?.HideBeam();
                    site.ActiveHelTargetId = PersistentID.None;
                    site.ActiveHelDwellSeconds = 0f;
                    if (!site.IsRadarPrimaryHub && (site.IsVehicleMoving || MdsConfig.EnableAutoPopUp.Value))
                    {
                        site.RequestDeployState(false);
                    }

                    continue;
                }

                Vector3 sitePos = site.Unit.transform.position;
                if (site.IsCommandCenter)
                {
                    site.PopUpRequested = true;
                    continue;
                }

                if (site.IsRadarPrimaryHub)
                {
                    site.PopUpRequested = site.EmconState != FcuEmconState.AradShutdown;
                    if (site.Spec.Role == WeaponCascadeRole.FireControl && site.TurretAim != null)
                    {
                        float fcuRangeM = Mathf.Max(8000f, site.Spec.MaxEngagementRangeKm * 1100f);
                        if (threatCount > 0 && TryFindClosestThreatPos(sitePos, threatCount, fcuRangeM, out Vector3 fcuTargetPos))
                        {
                            anyNearThreat = true;
                            site.LastThreatSeenTime = now;
                            site.TurretAim.SetAimWorldPosition(fcuTargetPos);
                        }
                        else
                        {
                            site.TurretAim.ClearAim();
                        }
                    }

                    continue;
                }

                bool nearSite = false;

                // Keep ABM/HoJ VLS deployed whenever an active ballistic/nuclear warning or HoJ jammer track threatens this faction.
                if ((site.Spec.Role == WeaponCascadeRole.AbmVls &&
                     AbmEarlyWarningService.Instance != null &&
                     AbmEarlyWarningService.Instance.HasActiveWarningForFaction(faction)) ||
                    (SiteHasReadyPassiveHojMissile(site) &&
                     SensorNetworkManager.Instance != null &&
                     SensorNetworkManager.Instance.HasActiveHojTrackForFaction(faction)))
                {
                    nearSite = true;
                }
                else if (site.Spec.Role == WeaponCascadeRole.AntiShipCoastal)
                {
                    float ashmWatchSq = Mathf.Pow(Mathf.Max(2000f, site.Spec.MaxEngagementRangeKm * 1050f), 2f);
                    for (int t = 0; t < threatCount; t++)
                    {
                        if (threatBuffer[t].IsShip &&
                            (threatBuffer[t].WorldPos - sitePos).sqrMagnitude <= ashmWatchSq)
                        {
                            nearSite = true;
                            break;
                        }
                    }
                }
                else
                {
                    float siteWatchSq = Mathf.Max(popUpSq, Mathf.Pow(site.Spec.MaxEngagementRangeKm * 1150f, 2f));
                    for (int t = 0; t < threatCount; t++)
                    {
                        if (!threatBuffer[t].IsShip &&
                            (threatBuffer[t].WorldPos - sitePos).sqrMagnitude <= siteWatchSq)
                        {
                            nearSite = true;
                            break;
                        }
                    }
                }

                if (nearSite)
                {
                    anyNearThreat = true;
                    site.LastThreatSeenTime = now;
                    if (MdsConfig.EnableAutoPopUp.Value)
                    {
                        site.RequestDeployState(true);
                    }
                }
                else if (MdsConfig.EnableAutoPopUp.Value &&
                         now - site.LastThreatSeenTime >= Mathf.Max(4f, profile.StowAfterIdleSeconds))
                {
                    site.RequestDeployState(false);
                }
            }

            return anyNearThreat;
        }

        private bool TryFindClosestThreatPos(Vector3 origin, int threatCount, float maxRangeMeters, out Vector3 worldPos)
        {
            float bestDistSq = maxRangeMeters * maxRangeMeters;
            bool found = false;
            worldPos = Vector3.zero;

            for (int t = 0; t < threatCount; t++)
            {
                if (threatBuffer[t].IsShip)
                {
                    continue;
                }

                float dSq = (threatBuffer[t].WorldPos - origin).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    worldPos = threatBuffer[t].WorldPos;
                    found = true;
                }
            }

            return found;
        }

        private int BuildEffectiveCascade(SmartEngagementProfile profile)
        {
            WeaponCascadeRole[] source = profile.CascadePriority != null && profile.CascadePriority.Length > 0
                ? profile.CascadePriority
                : SmartEngagementProfile.CreateDefault().CascadePriority;

            int maxLen = Math.Min(source.Length, effectiveCascadeScratch.Length);
            if (profile.PreferLaserBeforeMissiles)
            {
                for (int i = 0; i < maxLen; i++)
                {
                    effectiveCascadeScratch[i] = source[i];
                }

                return maxLen;
            }

            int count = 0;
            bool hasLaser = false;
            bool insertedLaser = false;
            for (int i = 0; i < maxLen; i++)
            {
                if (source[i] == WeaponCascadeRole.LaserHel)
                {
                    hasLaser = true;
                    break;
                }
            }

            for (int i = 0; i < maxLen && count < effectiveCascadeScratch.Length; i++)
            {
                WeaponCascadeRole r = source[i];
                if (r == WeaponCascadeRole.LaserHel)
                {
                    continue;
                }

                effectiveCascadeScratch[count++] = r;
                if (hasLaser && !insertedLaser && r == WeaponCascadeRole.ShortMedSam && count < effectiveCascadeScratch.Length)
                {
                    effectiveCascadeScratch[count++] = WeaponCascadeRole.LaserHel;
                    insertedLaser = true;
                }
            }

            if (hasLaser && !insertedLaser && count < effectiveCascadeScratch.Length)
            {
                effectiveCascadeScratch[count++] = WeaponCascadeRole.LaserHel;
            }

            return count;
        }

        private int AssignTargetsByCascade(
            SmartEngagementProfile profile,
            int effectorCount,
            int threatCount)
        {
            int cascadeCount = BuildEffectiveCascade(profile);
            int maxSim = Math.Max(1, profile.MaxSimultaneousPerThreat);
            int orderCount = 0;
            Array.Clear(aimedThisTick, 0, effectorCount);
            Array.Clear(effectorUsedThisTick, 0, effectorCount);

            bool hasNetworked = false;
            for (int e = 0; e < effectorCount; e++)
            {
                if (effectorBuffer[e].IsInCommandNet)
                {
                    hasNetworked = true;
                    break;
                }
            }

            if (hasNetworked)
            {
                AssignSkyshieldNetTargets(profile, effectorCount, threatCount, ref orderCount);
            }

            for (int c = 0; c < cascadeCount && orderCount < MaxOrders; c++)
            {
                WeaponCascadeRole role = effectiveCascadeScratch[c];
                for (int e = 0; e < effectorCount && orderCount < MaxOrders; e++)
                {
                    EffectorSnapshot eff = effectorBuffer[e];
                    if (eff.IsInCommandNet || eff.Role != role || !eff.IsDeployed)
                    {
                        continue;
                    }

                    ModularSiteBehavior? site = effectorSiteBuffer[e];
                    if (site == null)
                    {
                        continue;
                    }

                    if (!eff.IsReadyToFire)
                    {
                        if (role == WeaponCascadeRole.LaserHel)
                        {
                            site.TurretAim?.HideBeam();
                        }

                        if (site.Spec.VerticalLaunch || site.Spec.LaunchAlongSocketForward || (role != WeaponCascadeRole.LaserHel && site.AmmoRemaining <= 0))
                        {
                            continue;
                        }
                    }

                    int bestThreatIdx = -1;
                    float bestScore = float.MinValue;
                    bool bestPassiveHoj = false;
                    bool bestIsHojMissile = false;
                    bool bestUseSecondary = false;
                    bool isMissileRole = role == WeaponCascadeRole.ShortMedSam ||
                                         role == WeaponCascadeRole.LongRangeSam ||
                                         role == WeaponCascadeRole.AbmVls ||
                                         role == WeaponCascadeRole.AntiShipCoastal;

                    for (int t = 0; t < threatCount; t++)
                    {
                        ThreatSnapshot th = threatBuffer[t];
                        Unit? thUnit = threatUnitBuffer[t];
                        if (thUnit == null || thUnit.disabled || GameInteropHelpers.IsAircraftSplashed(thUnit))
                        {
                            continue;
                        }

                        if (!IsRoleEligibleForThreat(role, ref eff, ref th, profile, effectorCount))
                        {
                            continue;
                        }

                        bool canBlindHoj = th.IsActivelyJamming && eff.HasPassiveHojMissile;
                        bool blindHojSalvoOverride = canBlindHoj &&
                                                     isMissileRole &&
                                                     (th.ActiveHojMissileAttacks + th.AssignedHojThisTick) == 0;

                        int effectiveMaxSim = role == WeaponCascadeRole.AntiShipCoastal
                            ? (th.TargetMaxHp >= 1200f ? 3 : 2)
                            : maxSim;
                        bool salvoBlocked = profile.UseSalvoDeconfliction &&
                                            role != WeaponCascadeRole.LaserHel &&
                                            role != WeaponCascadeRole.CloseInGun &&
                                            th.ExistingMissileAttacks + th.AssignedThisTick >= effectiveMaxSim;
                        if (!blindHojSalvoOverride && salvoBlocked)
                        {
                            continue;
                        }

                        float allowedMaxRange = canBlindHoj ? eff.FullMaxRangeMeters : eff.MaxRangeMeters;
                        float distSq = (th.WorldPos - eff.WorldPos).sqrMagnitude;
                        if (distSq < eff.MinRangeMeters * eff.MinRangeMeters ||
                            distSq > allowedMaxRange * allowedMaxRange)
                        {
                            continue;
                        }

                        float distKm = Mathf.Sqrt(distSq) * 0.001f;
                        bool slotUseSecondary = false;
                        bool slotIsHojMissile = false;
                        float slotScoreBonus = 0f;

                        if (isMissileRole)
                        {
                            bool requireHojOnly = canBlindHoj &&
                                                  (!eff.HasFcuGuidance ||
                                                   distSq > eff.MaxRangeMeters * eff.MaxRangeMeters ||
                                                   salvoBlocked);
                            if (!TryChooseMissileSlot(
                                site,
                                ref th,
                                distKm,
                                out slotUseSecondary,
                                out _,
                                out CustomMissileSpec? chosenSpec,
                                requirePassiveHojOnly: requireHojOnly))
                            {
                                continue;
                            }

                            if (chosenSpec != null)
                            {
                                slotIsHojMissile = chosenSpec.SupportsActiveToPassiveHoj;
                                if ((th.IsActivelyJamming || th.IsJammer) && chosenSpec.SupportsActiveToPassiveHoj)
                                {
                                    slotScoreBonus += th.IsActivelyJamming ? 85f : 30f;
                                }
                                else if (th.IsAircraft && !th.IsJammer && !chosenSpec.IsAbmInterceptor && !chosenSpec.SupportsActiveToPassiveHoj)
                                {
                                    slotScoreBonus += 25f;
                                }
                            }
                        }

                        float score = th.PriorityScore + slotScoreBonus - (distKm * 1.4f);

                        if (profile.DeprioritizeDetectedDecoys && th.IsDecoy &&
                            (SensorBandPhysics.HasBand(eff.BandMask, SensorBand.XBand) ||
                             SensorBandPhysics.HasBand(eff.BandMask, SensorBand.EoIr)))
                        {
                            score -= 120f;
                        }

                        if (role == WeaponCascadeRole.LaserHel && th.IsMissile)
                        {
                            score += 45f;
                        }
                        else if (role == WeaponCascadeRole.AbmVls && (th.IsBallistic || th.IsNuclear))
                        {
                            score += 140f;
                        }

                        if ((role == WeaponCascadeRole.CloseInGun || role == WeaponCascadeRole.LaserHel) &&
                            (th.IsArtilleryShell || th.IsShellOrSmallMunition))
                        {
                            score += 55f;
                        }

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestThreatIdx = t;
                            bestPassiveHoj = th.IsJammer || th.IsActivelyJamming;
                            bestIsHojMissile = slotIsHojMissile;
                            bestUseSecondary = slotUseSecondary;
                        }
                    }

                    if (bestThreatIdx < 0)
                    {
                        continue;
                    }

                    if (!eff.IsReadyToFire)
                    {
                        site.TurretAim?.SetAimWorldPosition(threatBuffer[bestThreatIdx].WorldPos);
                        aimedThisTick[e] = true;
                        continue;
                    }

                    WtaEngagementKind kind = role switch
                    {
                        WeaponCascadeRole.LaserHel => WtaEngagementKind.LaserHel,
                        WeaponCascadeRole.CloseInGun => WtaEngagementKind.GunCiws,
                        WeaponCascadeRole.AbmVls => WtaEngagementKind.AbmVls,
                        _ => WtaEngagementKind.CustomMissile
                    };

                    if (kind == WtaEngagementKind.CustomMissile || kind == WtaEngagementKind.AbmVls)
                    {
                        if (!site.Spec.VerticalLaunch && !site.Spec.LaunchAlongSocketForward && site.TurretAim != null)
                        {
                            site.TurretAim.SetAimWorldPosition(threatBuffer[bestThreatIdx].WorldPos);
                            aimedThisTick[e] = true;
                            if (!site.TurretAim.IsOnTarget)
                            {
                                continue;
                            }
                        }

                        ThreatSnapshot updated = threatBuffer[bestThreatIdx];
                        updated.AssignedThisTick++;
                        if (bestIsHojMissile)
                        {
                            updated.AssignedHojThisTick++;
                        }

                        threatBuffer[bestThreatIdx] = updated;
                    }

                    aimedThisTick[e] = true;
                    orderBuffer[orderCount++] = new EngagementOrder
                    {
                        EffectorSiteIndex = eff.SiteIndex,
                        ThreatIndex = bestThreatIdx,
                        Kind = kind,
                        UsePassiveHoj = bestPassiveHoj,
                        UseSecondaryMissile = bestUseSecondary
                    };
                }
            }

            // Keep deployed non-VLS turrets tracking approaching hostiles when idle/deconflicted, or watch-sweep.
            for (int e = 0; e < effectorCount; e++)
            {
                if (aimedThisTick[e])
                {
                    continue;
                }

                EffectorSnapshot eff = effectorBuffer[e];
                ModularSiteBehavior? site = effectorSiteBuffer[e];
                if (site == null || !eff.IsDeployed || site.Spec.VerticalLaunch || site.Spec.LaunchAlongSocketForward || site.TurretAim == null)
                {
                    continue;
                }

                if (site.Spec.Role == WeaponCascadeRole.LaserHel)
                {
                    site.TurretAim.HideBeam();
                    site.ActiveHelTargetId = PersistentID.None;
                    site.ActiveHelDwellSeconds = 0f;
                }

                float watchRangeM = Mathf.Max(profile.AutoPopUpRangeKm, site.Spec.MaxEngagementRangeKm * 1.4f) * 1000f;
                if ((site.AmmoRemaining > 0 || site.Spec.Role == WeaponCascadeRole.LaserHel) &&
                    TryFindClosestThreatPos(eff.WorldPos, threatCount, watchRangeM, out Vector3 watchPos))
                {
                    site.TurretAim.SetAimWorldPosition(watchPos);
                }
                else
                {
                    site.TurretAim.ClearAim();
                }
            }

            return orderCount;
        }

        private void AssignSkyshieldNetTargets(
            SmartEngagementProfile profile,
            int effectorCount,
            int threatCount,
            ref int orderCount)
        {
            // Stage 1: Networked close-in defense (HEL Laser + 35mm AHEAD) with spread across nearby inbound munitions.
            Array.Clear(closeInAssignedThisTick, 0, threatCount);
            WeaponCascadeRole firstCloseRole = profile.PreferLaserBeforeMissiles ? WeaponCascadeRole.LaserHel : WeaponCascadeRole.CloseInGun;
            WeaponCascadeRole secondCloseRole = profile.PreferLaserBeforeMissiles ? WeaponCascadeRole.CloseInGun : WeaponCascadeRole.LaserHel;

            AssignNetworkedCloseInRole(firstCloseRole, profile, effectorCount, threatCount, ref orderCount);
            AssignNetworkedCloseInRole(secondCloseRole, profile, effectorCount, threatCount, ref orderCount);

            if (orderCount >= MaxOrders || threatCount == 0)
            {
                return;
            }

            // Stage 2: Sort threats by descending priority (break ties by fewer active missiles, then higher speed).
            for (int i = 0; i < threatCount; i++)
            {
                threatOrderScratch[i] = i;
            }

            for (int i = 1; i < threatCount; i++)
            {
                int keyIdx = threatOrderScratch[i];
                ThreatSnapshot key = threatBuffer[keyIdx];
                float keySpeedSq = key.Velocity.sqrMagnitude;
                int j = i - 1;
                while (j >= 0)
                {
                    ThreatSnapshot cand = threatBuffer[threatOrderScratch[j]];
                    bool moveRight = key.PriorityScore > cand.PriorityScore + 0.01f ||
                                     (Mathf.Abs(key.PriorityScore - cand.PriorityScore) <= 0.01f &&
                                      (key.ExistingMissileAttacks < cand.ExistingMissileAttacks ||
                                       (key.ExistingMissileAttacks == cand.ExistingMissileAttacks && keySpeedSq > cand.Velocity.sqrMagnitude)));
                    if (!moveRight)
                    {
                        break;
                    }

                    threatOrderScratch[j + 1] = threatOrderScratch[j];
                    j--;
                }

                threatOrderScratch[j + 1] = keyIdx;
            }

            Array.Clear(firstNetShooterScratch, 0, threatCount);

            // Pass 1: Coverage-First — at most 1 optimal missile shooter per currently unengaged threat.
            for (int idx = 0; idx < threatCount && orderCount < MaxOrders; idx++)
            {
                int t = threatOrderScratch[idx];
                if (threatBuffer[t].ExistingMissileAttacks + threatBuffer[t].AssignedThisTick >= 1)
                {
                    continue;
                }

                TryAssignNetworkedMissileForThreat(
                    t,
                    effectorCount,
                    threatCount,
                    profile,
                    preferredDifferentFrom: null,
                    ref orderCount,
                    recordFirstShooter: true);
            }

            // Pass 2: Shoot-Look-Shoot — allow 2nd missile ONLY for Nuclear, Ballistic, or ARAD threats.
            for (int idx = 0; idx < threatCount && orderCount < MaxOrders; idx++)
            {
                int t = threatOrderScratch[idx];
                ThreatSnapshot th = threatBuffer[t];
                if (!th.IsNuclear && !th.IsBallistic && !th.IsArad)
                {
                    continue;
                }

                if (th.ExistingMissileAttacks + th.AssignedThisTick >= 2)
                {
                    continue;
                }

                TryAssignNetworkedMissileForThreat(
                    t,
                    effectorCount,
                    threatCount,
                    profile,
                    preferredDifferentFrom: firstNetShooterScratch[t],
                    ref orderCount,
                    recordFirstShooter: false);
            }

            // Pass 2B: Blind HoJ Counter-Jammer Guarantee — ensure at least 1 Passive HoJ missile launches against any active jammer.
            for (int idx = 0; idx < threatCount && orderCount < MaxOrders; idx++)
            {
                int t = threatOrderScratch[idx];
                ThreatSnapshot th = threatBuffer[t];
                if (!th.IsActivelyJamming || th.ActiveHojMissileAttacks + th.AssignedHojThisTick >= 1)
                {
                    continue;
                }

                TryAssignNetworkedMissileForThreat(
                    t,
                    effectorCount,
                    threatCount,
                    profile,
                    preferredDifferentFrom: firstNetShooterScratch[t],
                    ref orderCount,
                    recordFirstShooter: false,
                    forcePassiveHojOnly: true);
            }

            // Stage 3: Networked coastal anti-ship effectors vs surface ships (respecting warship salvo cap).
            AssignNetworkedAntiShip(profile, effectorCount, threatCount, ref orderCount);
        }

        private void AssignNetworkedAntiShip(
            SmartEngagementProfile profile,
            int effectorCount,
            int threatCount,
            ref int orderCount)
        {
            _ = profile;
            for (int e = 0; e < effectorCount && orderCount < MaxOrders; e++)
            {
                EffectorSnapshot eff = effectorBuffer[e];
                if (!eff.IsInCommandNet ||
                    effectorUsedThisTick[e] ||
                    eff.Role != WeaponCascadeRole.AntiShipCoastal ||
                    !eff.IsDeployed ||
                    !eff.IsReadyToFire)
                {
                    continue;
                }

                ModularSiteBehavior? site = effectorSiteBuffer[e];
                if (site == null)
                {
                    continue;
                }

                int bestThreatIdx = -1;
                float bestScore = float.MinValue;
                bool bestUseSecondary = false;
                for (int t = 0; t < threatCount; t++)
                {
                    ThreatSnapshot th = threatBuffer[t];
                    Unit? thUnit = threatUnitBuffer[t];
                    if (thUnit == null || thUnit.disabled || !th.IsShip)
                    {
                        continue;
                    }

                    int shipSalvoCap = th.TargetMaxHp >= 1200f ? 3 : 2;
                    if (th.ExistingMissileAttacks + th.AssignedThisTick >= shipSalvoCap)
                    {
                        continue;
                    }

                    float distSq = (th.WorldPos - eff.WorldPos).sqrMagnitude;
                    if (distSq < eff.MinRangeMeters * eff.MinRangeMeters ||
                        distSq > eff.MaxRangeMeters * eff.MaxRangeMeters)
                    {
                        continue;
                    }

                    float distKm = Mathf.Sqrt(distSq) * 0.001f;
                    if (!TryChooseMissileSlot(site, ref th, distKm, out bool slotUseSecondary, out _, out _))
                    {
                        continue;
                    }

                    float score = th.PriorityScore - (distKm * 0.8f) + (eff.AmmoRatio * 20f);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestThreatIdx = t;
                        bestUseSecondary = slotUseSecondary;
                    }
                }

                if (bestThreatIdx < 0)
                {
                    continue;
                }

                effectorUsedThisTick[e] = true;
                aimedThisTick[e] = true;
                ThreatSnapshot updated = threatBuffer[bestThreatIdx];
                updated.AssignedThisTick++;
                threatBuffer[bestThreatIdx] = updated;

                orderBuffer[orderCount++] = new EngagementOrder
                {
                    EffectorSiteIndex = eff.SiteIndex,
                    ThreatIndex = bestThreatIdx,
                    Kind = WtaEngagementKind.CustomMissile,
                    UsePassiveHoj = false,
                    UseSecondaryMissile = bestUseSecondary
                };
            }
        }

        private void AssignNetworkedCloseInRole(
            WeaponCascadeRole role,
            SmartEngagementProfile profile,
            int effectorCount,
            int threatCount,
            ref int orderCount)
        {
            for (int e = 0; e < effectorCount && orderCount < MaxOrders; e++)
            {
                EffectorSnapshot eff = effectorBuffer[e];
                if (!eff.IsInCommandNet || effectorUsedThisTick[e] || eff.Role != role || !eff.IsDeployed)
                {
                    continue;
                }

                ModularSiteBehavior? site = effectorSiteBuffer[e];
                if (site == null)
                {
                    continue;
                }

                if (!eff.IsReadyToFire)
                {
                    if (role == WeaponCascadeRole.LaserHel)
                    {
                        site.TurretAim?.HideBeam();
                    }

                    if (role != WeaponCascadeRole.LaserHel && site.AmmoRemaining <= 0)
                    {
                        continue;
                    }
                }

                int bestThreatIdx = -1;
                float bestScore = float.MinValue;
                for (int t = 0; t < threatCount; t++)
                {
                    ThreatSnapshot th = threatBuffer[t];
                    Unit? thUnit = threatUnitBuffer[t];
                    if (thUnit == null || thUnit.disabled || GameInteropHelpers.IsAircraftSplashed(thUnit))
                    {
                        continue;
                    }

                    if (!IsRoleEligibleForThreat(role, ref eff, ref th, profile, effectorCount))
                    {
                        continue;
                    }

                    float distSq = (th.WorldPos - eff.WorldPos).sqrMagnitude;
                    if (distSq < eff.MinRangeMeters * eff.MinRangeMeters ||
                        distSq > eff.MaxRangeMeters * eff.MaxRangeMeters)
                    {
                        continue;
                    }

                    float distKm = Mathf.Sqrt(distSq) * 0.001f;
                    float score = th.PriorityScore - (distKm * 2.0f) - (closeInAssignedThisTick[t] * 18f);
                    if (role == WeaponCascadeRole.LaserHel && th.IsMissile)
                    {
                        score += 45f;
                    }

                    if (th.IsArtilleryShell || th.IsShellOrSmallMunition)
                    {
                        score += 55f;
                    }

                    if (profile.DeprioritizeDetectedDecoys && th.IsDecoy &&
                        (SensorBandPhysics.HasBand(eff.BandMask, SensorBand.XBand) ||
                         SensorBandPhysics.HasBand(eff.BandMask, SensorBand.EoIr)))
                    {
                        score -= 120f;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestThreatIdx = t;
                    }
                }

                if (bestThreatIdx < 0)
                {
                    continue;
                }

                if (!eff.IsReadyToFire)
                {
                    site.TurretAim?.SetAimWorldPosition(threatBuffer[bestThreatIdx].WorldPos);
                    aimedThisTick[e] = true;
                    continue;
                }

                effectorUsedThisTick[e] = true;
                aimedThisTick[e] = true;
                closeInAssignedThisTick[bestThreatIdx]++;
                orderBuffer[orderCount++] = new EngagementOrder
                {
                    EffectorSiteIndex = eff.SiteIndex,
                    ThreatIndex = bestThreatIdx,
                    Kind = role == WeaponCascadeRole.LaserHel ? WtaEngagementKind.LaserHel : WtaEngagementKind.GunCiws,
                    UsePassiveHoj = false,
                    UseSecondaryMissile = false
                };
            }
        }

        private void TryAssignNetworkedMissileForThreat(
            int threatIdx,
            int effectorCount,
            int threatCount,
            SmartEngagementProfile profile,
            ModularSiteBehavior? preferredDifferentFrom,
            ref int orderCount,
            bool recordFirstShooter,
            bool forcePassiveHojOnly = false)
        {
            ThreatSnapshot th = threatBuffer[threatIdx];
            Unit? thUnit = threatUnitBuffer[threatIdx];
            if (thUnit == null || thUnit.disabled || GameInteropHelpers.IsAircraftSplashed(thUnit) || (!th.IsMissile && !th.IsAircraft))
            {
                return;
            }

            int bestE = -1;
            float bestScore = float.MinValue;
            bool bestUseSecondary = false;
            bool bestIsHojMissile = false;
            WtaEngagementKind bestKind = WtaEngagementKind.CustomMissile;

            for (int e = 0; e < effectorCount; e++)
            {
                EffectorSnapshot eff = effectorBuffer[e];
                if (!eff.IsInCommandNet || effectorUsedThisTick[e] || !eff.IsDeployed || !eff.IsReadyToFire)
                {
                    continue;
                }

                if (eff.Role != WeaponCascadeRole.ShortMedSam &&
                    eff.Role != WeaponCascadeRole.LongRangeSam &&
                    eff.Role != WeaponCascadeRole.AbmVls)
                {
                    continue;
                }

                if (forcePassiveHojOnly && !eff.HasPassiveHojMissile)
                {
                    continue;
                }

                ModularSiteBehavior? site = effectorSiteBuffer[e];
                if (site == null || !IsRoleEligibleForThreat(eff.Role, ref eff, ref th, profile, effectorCount))
                {
                    continue;
                }

                bool canBlindHoj = th.IsActivelyJamming && eff.HasPassiveHojMissile;
                float allowedMaxRange = canBlindHoj ? eff.FullMaxRangeMeters : eff.MaxRangeMeters;

                Vector3 relPos = th.WorldPos - eff.WorldPos;
                float distSq = relPos.sqrMagnitude;
                if (distSq < eff.MinRangeMeters * eff.MinRangeMeters ||
                    distSq > allowedMaxRange * allowedMaxRange)
                {
                    continue;
                }

                float distMeters = Mathf.Sqrt(distSq);
                float distKm = distMeters * 0.001f;
                bool requireHojOnly = forcePassiveHojOnly ||
                                      (canBlindHoj &&
                                       (!eff.HasFcuGuidance || distSq > eff.MaxRangeMeters * eff.MaxRangeMeters));
                if (!TryChooseMissileSlot(
                    site,
                    ref th,
                    distKm,
                    out bool slotUseSecondary,
                    out _,
                    out CustomMissileSpec? chosenSpec,
                    requirePassiveHojOnly: requireHojOnly))
                {
                    continue;
                }

                bool slotIsHojMissile = chosenSpec != null && chosenSpec.SupportsActiveToPassiveHoj;
                float score = eff.Role switch
                {
                    WeaponCascadeRole.ShortMedSam => 45f,
                    WeaponCascadeRole.LongRangeSam => 22f,
                    WeaponCascadeRole.AbmVls => (th.IsBallistic || th.IsNuclear || th.IsJammer || th.IsActivelyJamming) ? 65f : 5f,
                    _ => 0f
                };

                if (chosenSpec != null)
                {
                    if (th.IsBallistic || th.IsNuclear)
                    {
                        if (chosenSpec.IsAbmInterceptor) score += 140f;
                    }
                    else if ((th.IsActivelyJamming || th.IsJammer) && chosenSpec.SupportsActiveToPassiveHoj)
                    {
                        score += th.IsActivelyJamming ? 85f : 45f;
                    }
                    else if (th.IsAircraft && !th.IsJammer && !chosenSpec.IsAbmInterceptor && !chosenSpec.SupportsActiveToPassiveHoj)
                    {
                        score += 25f;
                    }
                }

                if (profile.DeprioritizeDetectedDecoys && th.IsDecoy &&
                    (SensorBandPhysics.HasBand(eff.BandMask, SensorBand.XBand) ||
                     SensorBandPhysics.HasBand(eff.BandMask, SensorBand.EoIr)))
                {
                    score -= 140f;
                }

                float maxRangeM = Mathf.Max(1000f, allowedMaxRange);
                float rangeRatio = Mathf.Clamp01(distMeters / maxRangeM);
                if (rangeRatio >= 0.15f && rangeRatio <= 0.80f)
                {
                    score += 30f;
                }
                else if (rangeRatio > 0.90f)
                {
                    score -= 25f;
                }
                else if (rangeRatio < 0.15f)
                {
                    score -= 10f;
                }

                if (distMeters > 1f && th.Velocity.sqrMagnitude > 100f)
                {
                    float closingDot = Vector3.Dot(th.Velocity.normalized, (-relPos) / distMeters);
                    score += closingDot * 22f;
                }

                // Balance magazine usage across networked launchers.
                score += eff.AmmoRatio * 25f;

                if (site.Spec.VerticalLaunch)
                {
                    score += 12f;
                }
                else if (site.TurretAim != null && site.TurretAim.IsOnTarget)
                {
                    score += 14f;
                }

                if (preferredDifferentFrom != null && !ReferenceEquals(site, preferredDifferentFrom))
                {
                    score += 35f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestE = e;
                    bestUseSecondary = slotUseSecondary;
                    bestIsHojMissile = slotIsHojMissile;
                    bestKind = eff.Role == WeaponCascadeRole.AbmVls ? WtaEngagementKind.AbmVls : WtaEngagementKind.CustomMissile;
                }
            }

            if (bestE < 0)
            {
                return;
            }

            ModularSiteBehavior? bestSite = effectorSiteBuffer[bestE];
            if (bestSite == null)
            {
                return;
            }

            effectorUsedThisTick[bestE] = true;
            aimedThisTick[bestE] = true;
            if (recordFirstShooter)
            {
                firstNetShooterScratch[threatIdx] = bestSite;
            }

            ThreatSnapshot updated = threatBuffer[threatIdx];
            updated.AssignedThisTick++;
            if (bestIsHojMissile)
            {
                updated.AssignedHojThisTick++;
            }

            threatBuffer[threatIdx] = updated;

            if (!bestSite.Spec.VerticalLaunch && bestSite.TurretAim != null)
            {
                bestSite.TurretAim.SetAimWorldPosition(th.WorldPos);
                if (!bestSite.TurretAim.IsOnTarget)
                {
                    return;
                }
            }

            orderBuffer[orderCount++] = new EngagementOrder
            {
                EffectorSiteIndex = effectorBuffer[bestE].SiteIndex,
                ThreatIndex = threatIdx,
                Kind = bestKind,
                UsePassiveHoj = th.IsJammer || th.IsActivelyJamming,
                UseSecondaryMissile = bestUseSecondary
            };
        }

        private static bool TryChooseMissileSlot(
            ModularSiteBehavior site,
            ref ThreatSnapshot th,
            float distKm,
            out bool useSecondary,
            out ContractorPackage? chosenPkg,
            out CustomMissileSpec? chosenSpec,
            bool requirePassiveHojOnly = false)
        {
            useSecondary = false;
            chosenPkg = null;
            chosenSpec = null;

            float bestSlotScore = float.MinValue;
            bool found = false;

            if (site.PrimaryAmmoRemaining > 0 &&
                !string.IsNullOrEmpty(site.Spec.PrimaryMissileId) &&
                ContractorRegistry.TryGetMissileSpec(site.Spec.PrimaryMissileId, out ContractorPackage p1, out CustomMissileSpec s1) &&
                (!requirePassiveHojOnly || s1.SupportsActiveToPassiveHoj))
            {
                float score1 = ScoreMissileSlotForThreat(s1, ref th, distKm);
                if (score1 > bestSlotScore)
                {
                    bestSlotScore = score1;
                    useSecondary = false;
                    chosenPkg = p1;
                    chosenSpec = s1;
                    found = true;
                }
            }

            if (site.SecondaryAmmoRemaining > 0 &&
                !string.IsNullOrEmpty(site.Spec.SecondaryMissileId) &&
                ContractorRegistry.TryGetMissileSpec(site.Spec.SecondaryMissileId, out ContractorPackage p2, out CustomMissileSpec s2) &&
                (!requirePassiveHojOnly || s2.SupportsActiveToPassiveHoj))
            {
                float score2 = ScoreMissileSlotForThreat(s2, ref th, distKm);
                if (score2 > bestSlotScore)
                {
                    bestSlotScore = score2;
                    useSecondary = true;
                    chosenPkg = p2;
                    chosenSpec = s2;
                    found = true;
                }
            }

            return found;
        }

        private static float ScoreMissileSlotForThreat(CustomMissileSpec spec, ref ThreatSnapshot th, float distKm)
        {
            if (distKm > spec.MaxEngagementRangeKm)
            {
                return float.MinValue;
            }

            if (th.IsShip)
            {
                return spec.IsAntiShip ? 150f : float.MinValue;
            }

            if (spec.IsAntiShip)
            {
                return float.MinValue;
            }

            if (spec.IsAbmInterceptor && !th.IsBallistic && !th.IsNuclear && th.AltitudeMeters < 4500f)
            {
                return float.MinValue;
            }

            float score = 10f;
            if (th.IsBallistic || th.IsNuclear)
            {
                score += spec.IsAbmInterceptor ? 220f : 40f;
            }
            else if (th.IsActivelyJamming)
            {
                score += spec.SupportsActiveToPassiveHoj ? 220f : 25f;
            }
            else if (th.IsJammer)
            {
                score += spec.SupportsActiveToPassiveHoj ? 140f : 35f;
            }
            else if (th.IsAircraft)
            {
                score += !spec.IsAbmInterceptor && !spec.SupportsActiveToPassiveHoj ? 110f : 55f;
            }
            else if (th.IsMissile)
            {
                score += spec.SupportsActiveToPassiveHoj ? 80f : 65f;
            }

            if (distKm > 35f && spec.MaxEngagementRangeKm >= 60f)
            {
                score += 30f;
            }

            return score;
        }

        private bool IsRoleEligibleForThreat(
            WeaponCascadeRole role,
            ref EffectorSnapshot eff,
            ref ThreatSnapshot th,
            SmartEngagementProfile profile,
            int effectorCount)
        {
            if (role == WeaponCascadeRole.AntiShipCoastal)
            {
                return th.IsShip;
            }

            if (th.IsShip || (!th.IsMissile && !th.IsAircraft))
            {
                return false;
            }

            bool blindHojAllowed = th.IsActivelyJamming && eff.HasPassiveHojMissile;
            if (!blindHojAllowed &&
                eff.Role != WeaponCascadeRole.LaserHel &&
                eff.Role != WeaponCascadeRole.CloseInGun &&
                !eff.HasFcuGuidance &&
                eff.EoIrBackupRangeMeters <= 0f)
            {
                return false;
            }

            switch (role)
            {
                case WeaponCascadeRole.LaserHel:
                    return th.IsMissile || th.AltitudeMeters < 4500f;

                case WeaponCascadeRole.CloseInGun:
                    return th.IsMissile || th.AltitudeMeters < 3500f;

                case WeaponCascadeRole.AbmVls:
                    if (th.IsDecoy || th.IsShellOrSmallMunition || th.IsArtilleryShell)
                    {
                        return false;
                    }

                    if (th.IsMissile && !th.IsBallistic && !th.IsNuclear && !th.IsArad &&
                        th.BlastYield < 50f &&
                        HasCloseInDefenseCoverage(ref th, effectorCount, includeGuns: true, trajectoryPaddingMeters: 3000f))
                    {
                        return false;
                    }

                    return th.IsBallistic || th.IsNuclear || th.IsAircraft || th.IsJammer || th.IsMissile;

                case WeaponCascadeRole.ShortMedSam:
                    if (th.IsDecoy)
                    {
                        return false;
                    }

                    if (th.IsArtilleryShell && HasNearbyCloseInDefenseSite(ref eff, ref th, effectorCount, 16000f))
                    {
                        return false;
                    }

                    if ((th.IsShellOrSmallMunition ||
                         (profile.PreferLaserBeforeMissiles && th.IsMissile && th.BlastYield < 50f && !th.IsArad && th.AltitudeMeters < 2600f)) &&
                        HasCloseInDefenseCoverage(ref th, effectorCount, includeGuns: true, trajectoryPaddingMeters: 2500f))
                    {
                        return false;
                    }

                    return true;

                case WeaponCascadeRole.LongRangeSam:
                    if (th.IsDecoy || th.IsShellOrSmallMunition || th.IsArtilleryShell)
                    {
                        return false;
                    }

                    if (th.IsMissile && !th.IsBallistic && !th.IsNuclear && !th.IsArad &&
                        th.BlastYield < 50f &&
                        HasCloseInDefenseCoverage(ref th, effectorCount, includeGuns: true, trajectoryPaddingMeters: 3000f))
                    {
                        return false;
                    }

                    return true;

                default:
                    return false;
            }
        }

        private bool HasCloseInDefenseCoverage(
            ref ThreatSnapshot th,
            int effectorCount,
            bool includeGuns,
            float trajectoryPaddingMeters)
        {
            float velSq = th.Velocity.sqrMagnitude;
            for (int i = 0; i < effectorCount; i++)
            {
                EffectorSnapshot cand = effectorBuffer[i];
                if (cand.Role != WeaponCascadeRole.LaserHel &&
                    (!includeGuns || cand.Role != WeaponCascadeRole.CloseInGun))
                {
                    continue;
                }

                ModularSiteBehavior? candSite = effectorSiteBuffer[i];
                if (candSite == null || !candSite.IsAlive || candSite.IsHoldFireActive)
                {
                    continue;
                }

                if (cand.Role != WeaponCascadeRole.LaserHel && candSite.AmmoRemaining <= 0)
                {
                    continue;
                }

                float coverRadius = cand.MaxRangeMeters + Mathf.Max(0f, trajectoryPaddingMeters);
                float coverSq = coverRadius * coverRadius;
                Vector3 relPos = th.WorldPos - cand.WorldPos;
                if (relPos.sqrMagnitude <= coverSq)
                {
                    return true;
                }

                if (velSq > 1f)
                {
                    float tCpa = Mathf.Clamp(-Vector3.Dot(relPos, th.Velocity) / velSq, 0f, 25f);
                    Vector3 cpa = relPos + (th.Velocity * tCpa);
                    if (cpa.sqrMagnitude <= coverSq)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool HasNearbyCloseInDefenseSite(
            ref EffectorSnapshot samEffector,
            ref ThreatSnapshot th,
            int effectorCount,
            float maxRadiusMeters)
        {
            float radiusSq = maxRadiusMeters * maxRadiusMeters;
            for (int i = 0; i < effectorCount; i++)
            {
                EffectorSnapshot cand = effectorBuffer[i];
                if (cand.Role != WeaponCascadeRole.CloseInGun && cand.Role != WeaponCascadeRole.LaserHel)
                {
                    continue;
                }

                ModularSiteBehavior? candSite = effectorSiteBuffer[i];
                if (candSite == null || !candSite.IsAlive || candSite.IsHoldFireActive)
                {
                    continue;
                }

                if (cand.Role == WeaponCascadeRole.CloseInGun && candSite.AmmoRemaining <= 0)
                {
                    continue;
                }

                if ((cand.WorldPos - th.WorldPos).sqrMagnitude <= radiusSq ||
                    (cand.WorldPos - samEffector.WorldPos).sqrMagnitude <= radiusSq)
                {
                    return true;
                }
            }

            return false;
        }

        private void ExecuteEngagementOrders(
            IReadOnlyList<ModularSiteBehavior> allSites,
            SmartEngagementProfile profile,
            int orderCount,
            float actualDt,
            float now)
        {
            for (int i = 0; i < orderCount; i++)
            {
                EngagementOrder ord = orderBuffer[i];
                if (ord.EffectorSiteIndex < 0 || ord.EffectorSiteIndex >= allSites.Count)
                {
                    continue;
                }

                ModularSiteBehavior site = allSites[ord.EffectorSiteIndex];
                Unit? target = threatUnitBuffer[ord.ThreatIndex];
                if (!site.IsAlive || target == null || target.disabled || site.Unit == null || GameInteropHelpers.IsAircraftSplashed(target))
                {
                    if (site != null && site.IsAlive && site.Spec.Role == WeaponCascadeRole.LaserHel)
                    {
                        site.TurretAim?.HideBeam();
                        site.ActiveHelTargetId = PersistentID.None;
                        site.ActiveHelDwellSeconds = 0f;
                    }

                    continue;
                }

                ThreatSnapshot th = threatBuffer[ord.ThreatIndex];
                switch (ord.Kind)
                {
                    case WtaEngagementKind.LaserHel:
                        ExecuteHelLaserOrder(site, target, ref th, profile, actualDt);
                        break;

                    case WtaEngagementKind.GunCiws:
                        ExecuteGunCiwsOrder(site, target, ref th);
                        break;

                    case WtaEngagementKind.CustomMissile:
                        ExecuteMissileOrder(site, target, ord.UsePassiveHoj, ord.UseSecondaryMissile);
                        break;

                    case WtaEngagementKind.AbmVls:
                        ExecuteMissileOrder(site, target, ord.UsePassiveHoj, ord.UseSecondaryMissile);
                        break;
                }
            }

            _ = now;
        }

        private static Vector3 GetMuzzleOrigin(ModularSiteBehavior site)
        {
            if (site.Rig != null)
            {
                if (site.Rig.MuzzleTransform != null)
                {
                    return site.Rig.MuzzleTransform.position;
                }

                if (site.Rig.TurretHead != null)
                {
                    return site.Rig.TurretHead.position;
                }
            }

            return site.Unit != null ? site.Unit.transform.position + (Vector3.up * 2.5f) : Vector3.zero;
        }

        private static void ExecuteHelLaserOrder(
            ModularSiteBehavior site,
            Unit target,
            ref ThreatSnapshot th,
            SmartEngagementProfile profile,
            float actualDt)
        {
            if (site.CooldownRemaining > 0f || GameInteropHelpers.IsAircraftSplashed(target))
            {
                site.TurretAim?.HideBeam();
                site.ActiveHelTargetId = PersistentID.None;
                site.ActiveHelDwellSeconds = 0f;
                return;
            }

            site.TurretAim?.SetAimWorldPosition(th.WorldPos);
            if (site.TurretAim != null && !site.TurretAim.IsOnTarget)
            {
                site.TurretAim.HideBeam();
                return;
            }

            Vector3 muzzlePos = GetMuzzleOrigin(site);
            if (Physics.Linecast(muzzlePos, target.transform.position, (int)PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
            {
                site.TurretAim?.HideBeam();
                site.ActiveHelDwellSeconds = 0f;
                site.ActiveHelTargetId = PersistentID.None;
                return;
            }

            site.TurretAim?.ShowBeamOrTracer(th.WorldPos, actualDt + 0.12f, 0.085f);

            if (!target.persistentID.Equals(site.ActiveHelTargetId))
            {
                site.ActiveHelTargetId = target.persistentID;
                site.ActiveHelDwellSeconds = 0f;
            }

            site.ActiveHelDwellSeconds += actualDt;

            float baseDwell = Mathf.Max(0.25f, profile.LaserDwellSeconds);
            float powerFactor = Mathf.Max(0.25f, site.Spec.LaserPowerFactor);
            float hardening = MdsConfig.EnableLaserDwell.Value ? Mathf.Max(0.25f, th.ThermalHardening) : 1.0f;
            float requiredDwell = (baseDwell / powerFactor) * hardening;

            if (site.ActiveHelDwellSeconds < requiredDwell)
            {
                return;
            }

            site.ActiveHelDwellSeconds = 0f;
            site.ActiveHelTargetId = PersistentID.None;
            site.CooldownRemaining = Mathf.Max(0.25f, site.Spec.FireCooldownSeconds);

            if (target is Missile targetMissile)
            {
                if (th.IsNuclear && th.AltitudeMeters >= 800f && MdsConfig.EnableHighAltitudeDisarm.Value)
                {
                    GameInteropHelpers.SafeDisarmWarhead(targetMissile);
                }

                targetMissile.Detonate(Vector3.up, hitArmor: true, hitTerrain: false);
                MdsLog.Debug($"[HEL] '{site.SiteId}' destroyed missile '{target.unitName}' after {requiredDwell:0.00}s dwell.");
            }
            else
            {
                ApplyThermalOrKineticDamage(site.Unit!, target, 140f, isFire: true);
            }
        }

        private static void ExecuteGunCiwsOrder(ModularSiteBehavior site, Unit target, ref ThreatSnapshot th)
        {
            if (GameInteropHelpers.IsAircraftSplashed(target))
            {
                return;
            }

            Vector3 myPos = site.Unit!.transform.position;
            float dist = Vector3.Distance(myPos, th.WorldPos);
            float muzzleVel = Mathf.Max(600f, site.Spec.GunMuzzleVelocityMps);
            float leadTime = Mathf.Clamp(dist / muzzleVel, 0.02f, 3.5f);
            Vector3 leadPos = th.WorldPos + (th.Velocity * leadTime) + (0.5f * 9.81f * leadTime * leadTime * Vector3.up);

            site.TurretAim?.SetAimWorldPosition(leadPos);
            if (site.CooldownRemaining > 0f || site.PrimaryAmmoRemaining <= 0)
            {
                return;
            }

            if (site.TurretAim != null && !site.TurretAim.IsOnTarget)
            {
                return;
            }

            Vector3 muzzlePos = GetMuzzleOrigin(site);
            if (Physics.Linecast(muzzlePos, target.transform.position, (int)PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
            {
                return;
            }

            int burstRounds = Math.Min(2, site.PrimaryAmmoRemaining);
            site.PrimaryAmmoRemaining = Math.Max(0, site.PrimaryAmmoRemaining - burstRounds);
            site.SyncWeaponStationsAmmo();
            site.CooldownRemaining = Mathf.Max(0.18f, site.Spec.FireCooldownSeconds);

            Vector3 dir = (leadPos - muzzlePos).sqrMagnitude > 0.01f
                ? (leadPos - muzzlePos).normalized
                : site.Unit.transform.forward;
            Vector3 flashEnd = muzzlePos + (dir * Mathf.Min(18f, dist));
            site.TurretAim?.ShowBeamOrTracer(flashEnd, 0.06f, 0.07f);

            AheadAirburstManager.FireProgrammedBurst(
                site,
                target,
                muzzlePos,
                th.WorldPos,
                th.Velocity,
                th.IsNuclear,
                th.AltitudeMeters);
        }

        private static void ExecuteMissileOrder(
            ModularSiteBehavior site,
            Unit target,
            bool usePassiveHoj,
            bool useSecondaryMissile)
        {
            if (site.CooldownRemaining > 0f || site.AmmoRemaining <= 0 || Spawner.i == null || GameInteropHelpers.IsAircraftSplashed(target))
            {
                return;
            }

            bool hasSecondary = useSecondaryMissile && site.SecondaryAmmoRemaining > 0 && !string.IsNullOrEmpty(site.Spec.SecondaryMissileId);
            string missileId = hasSecondary
                ? site.Spec.SecondaryMissileId
                : site.Spec.PrimaryMissileId;

            if (string.IsNullOrEmpty(missileId) ||
                (!hasSecondary && site.PrimaryAmmoRemaining <= 0) ||
                !ContractorRegistry.TryGetMissileSpec(missileId, out ContractorPackage pkg, out CustomMissileSpec missileSpec))
            {
                return;
            }

            if (!site.Spec.VerticalLaunch && !site.Spec.LaunchAlongSocketForward)
            {
                site.TurretAim?.SetAimWorldPosition(target.transform.position);
                if (site.TurretAim != null && !site.TurretAim.IsOnTarget)
                {
                    return;
                }
            }

            MissileDefinition? baseDef = GameInteropHelpers.ResolveEncyclopediaMissileDef(
                missileSpec.BaseMissileCloneKey,
                preferBallisticOrSam: !missileSpec.IsAntiShip &&
                    (missileSpec.IsAbmInterceptor || missileSpec.MaxEngagementRangeKm >= 20f));
            if (baseDef == null || baseDef.unitPrefab == null)
            {
                return;
            }

            bool reservedAbm = false;
            if (missileSpec.IsAbmInterceptor)
            {
                if (!RtsBridgeHost.Current.AbmStockpile.TryReserveAbmInterceptor(site.Faction, missileSpec.MissileId))
                {
                    return;
                }

                reservedAbm = true;
            }

            Transform launchTf = site.TurretAim != null
                ? site.TurretAim.GetNextLaunchTransformForSlot(hasSecondary, site.PrimaryMagazineSize, site.SecondaryMagazineSize)
                : site.Unit!.transform;

            Missile? launched = CustomMissileLauncher.LaunchCustomMissile(
                pkg,
                missileSpec,
                site.Unit!,
                launchTf,
                target,
                site.Spec.VerticalLaunch,
                usePassiveHoj,
                site.Spec);

            if (launched != null)
            {
                if (hasSecondary)
                {
                    site.SecondaryAmmoRemaining = Math.Max(0, site.SecondaryAmmoRemaining - 1);
                }
                else
                {
                    site.PrimaryAmmoRemaining = Math.Max(0, site.PrimaryAmmoRemaining - 1);
                }

                site.SyncWeaponStationsAmmo();
                site.CooldownRemaining = Mathf.Max(0.8f, site.Spec.FireCooldownSeconds);

                if (usePassiveHoj && missileSpec.SupportsActiveToPassiveHoj)
                {
                    MdsLog.Info($"[HOJ BLIND LAUNCH] '{site.SiteId}' launched '{missileSpec.DisplayName}' -> '{target.unitName}'.");
                }
            }
            else if (reservedAbm && RtsBridgeHost.Current.AbmStockpile is VanillaBridgeFallbacks.VanillaAbmStockpileBridge vb)
            {
                vb.RefundAbmInterceptor(site.Faction);
            }
        }

        private void ClearIdleEffectors(int effectorCount)
        {
            for (int i = 0; i < effectorCount; i++)
            {
                ModularSiteBehavior? site = effectorSiteBuffer[i];
                if (site != null)
                {
                    site.TurretAim?.ClearAim();
                    site.TurretAim?.HideBeam();
                }
            }
        }

        internal static void ApplyThermalOrKineticDamage(Unit attacker, Unit target, float amount, bool isFire)
        {
            PersistentID dealerId = attacker != null ? attacker.persistentID : PersistentID.None;
            if (target is Missile m)
            {
                m.TakeDamage(
                    isFire ? 0f : amount,
                    isFire ? 0f : amount * 0.45f,
                    1f,
                    isFire ? amount : 0f,
                    0f,
                    dealerId);
                return;
            }

            if (target.partLookup != null && target.partLookup.Count > 0)
            {
                UnitPart? chosenPart = null;
                Transform? randTf = target.GetRandomPart();
                if (randTf != null)
                {
                    UnitPart? candidate = randTf.GetComponent<UnitPart>();
                    if (candidate != null && !candidate.IsDetached())
                    {
                        chosenPart = candidate;
                    }
                }

                if (chosenPart == null)
                {
                    int startIdx = UnityEngine.Random.Range(0, target.partLookup.Count);
                    for (int i = 0; i < target.partLookup.Count; i++)
                    {
                        UnitPart? p = target.partLookup[(startIdx + i) % target.partLookup.Count];
                        if (p != null && !p.IsDetached())
                        {
                            chosenPart = p;
                            break;
                        }
                    }
                }

                if (chosenPart != null)
                {
                    chosenPart.TakeDamage(
                        isFire ? 0f : amount,
                        isFire ? 0f : amount * 0.35f,
                        1f,
                        isFire ? amount : 0f,
                        0f,
                        dealerId);
                }
            }
        }
    }
}
