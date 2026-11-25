using System;
using System.Collections.Generic;
using MDSFramework.Api;
using UnityEngine;

namespace MDSFramework.Sensors
{
    internal struct HojTrackEntry
    {
        internal FactionAffinity Faction;
        internal Unit? JammerUnit;
        internal PersistentID JammerId;
        internal Vector3 TriangulatedWorldPos;
        internal Vector3 Velocity;
        internal float LastUpdatedTime;
        internal int ObserverCount;
        internal bool VictimIsRadarOrFcu;
    }

    internal sealed class SensorNetworkManager
    {
        private const float LinkRefreshIntervalSeconds = 1.5f;
        private const float BlinkOverlapSeconds = 0.75f;
        private const float HojTrackExpirySeconds = 18.0f;
        private const float PassiveEcmPollIntervalSeconds = 0.5f;

        internal static SensorNetworkManager? Instance { get; private set; }

        private readonly List<ModularSiteBehavior> allSites = new List<ModularSiteBehavior>(32);
        private readonly List<HojTrackEntry> activeHojTracks = new List<HojTrackEntry>(16);
        private readonly List<ModularSiteBehavior> scratchFcus = new List<ModularSiteBehavior>(16);
        private readonly List<ModularSiteBehavior> scratchEffectors = new List<ModularSiteBehavior>(32);
        private readonly List<ModularSiteBehavior> eligibleBlinkFcus = new List<ModularSiteBehavior>(16);
        private readonly List<ModularSiteBehavior> clusterFcus = new List<ModularSiteBehavior>(16);
        private readonly List<bool> visitedFcus = new List<bool>(16);
        private readonly List<ModularSiteBehavior> scratchHubs = new List<ModularSiteBehavior>(8);
        private readonly List<ModularSiteBehavior> netHubCluster = new List<ModularSiteBehavior>(8);
        private readonly List<ModularSiteBehavior> netMembers = new List<ModularSiteBehavior>(32);
        private readonly List<ModularSiteBehavior> netFcus = new List<ModularSiteBehavior>(16);
        private readonly List<bool> visitedHubs = new List<bool>(8);

        private float linkRefreshTimer;
        private float blinkClock;
        private float passiveEcmPollTimer;
        private int lastDeployedHubCount = -1;

        internal IReadOnlyList<ModularSiteBehavior> AllSites => allSites;
        internal IReadOnlyList<HojTrackEntry> ActiveHojTracks => activeHojTracks;
        internal int ActiveAradAlerts { get; private set; }

        internal SensorNetworkManager()
        {
            Instance = this;
        }

        internal void RegisterSite(ModularSiteBehavior site)
        {
            if (site == null || allSites.Contains(site))
            {
                return;
            }

            allSites.Add(site);
            RefreshLinksAndBlinking(forceLinkRefresh: true);
        }

        internal void UnregisterSite(ModularSiteBehavior site)
        {
            if (site == null)
            {
                return;
            }

            allSites.Remove(site);
            try
            {
                RtsBridgeHost.Current.AiThreats.ReportSiteDestroyed(site.SiteId);
                if (site.ConsumedRtsCap && site.Spec?.Logistics != null)
                {
                    RtsBridgeHost.Current.Caps.ReleaseCap(
                        site.Faction,
                        site.Spec.Logistics.CapCategory,
                        site.Spec.Logistics.CapWeight);
                }
            }
            catch (Exception ex)
            {
                MdsLog.Debug($"Error releasing site '{site.SiteId}': {ex.Message}");
            }

            if (!MdsRuntimeController.IsResettingScene)
            {
                RefreshLinksAndBlinking(forceLinkRefresh: true);
            }
        }

        internal void ResetSession()
        {
            allSites.Clear();
            activeHojTracks.Clear();
            scratchFcus.Clear();
            scratchEffectors.Clear();
            eligibleBlinkFcus.Clear();
            clusterFcus.Clear();
            visitedFcus.Clear();
            scratchHubs.Clear();
            netHubCluster.Clear();
            netMembers.Clear();
            netFcus.Clear();
            visitedHubs.Clear();
            linkRefreshTimer = 0f;
            blinkClock = 0f;
            passiveEcmPollTimer = 0f;
            lastDeployedHubCount = -1;
            ActiveAradAlerts = 0;
        }

        internal void Tick(float deltaTime)
        {
            blinkClock += deltaTime;
            linkRefreshTimer += deltaTime;
            passiveEcmPollTimer += deltaTime;

            for (int i = allSites.Count - 1; i >= 0; i--)
            {
                ModularSiteBehavior site = allSites[i];
                if (site == null || !site.IsAlive)
                {
                    allSites.RemoveAt(i);
                    continue;
                }

                site.TickSite(deltaTime);
            }

            RefreshLinksAndBlinking(forceLinkRefresh: linkRefreshTimer >= LinkRefreshIntervalSeconds);
            if (linkRefreshTimer >= LinkRefreshIntervalSeconds)
            {
                linkRefreshTimer = 0f;
            }

            if (passiveEcmPollTimer >= PassiveEcmPollIntervalSeconds)
            {
                passiveEcmPollTimer = 0f;
                PollActiveEcmEmissions();
            }

            PruneExpiredHojTracks();
        }

        internal bool HasActiveHojTrackForFaction(FactionAffinity faction)
        {
            for (int i = 0; i < activeHojTracks.Count; i++)
            {
                HojTrackEntry entry = activeHojTracks[i];
                if (entry.Faction == faction && entry.JammerUnit != null && !entry.JammerUnit.disabled)
                {
                    return true;
                }
            }

            return false;
        }

        internal bool HasActiveHojTrack(FactionAffinity faction, Unit? target)
        {
            if (target == null || target.disabled)
            {
                return false;
            }

            for (int i = 0; i < activeHojTracks.Count; i++)
            {
                HojTrackEntry entry = activeHojTracks[i];
                if (entry.Faction == faction && ReferenceEquals(entry.JammerUnit, target))
                {
                    return true;
                }
            }

            return false;
        }

        internal void NotifyJammingEmission(Unit victim, Unit jammer, float jamAmount)
        {
            if (victim == null || jammer == null || jammer.disabled)
            {
                return;
            }

            ModularSiteBehavior? victimSite = null;
            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior site = allSites[i];
                if (site.IsAlive && ReferenceEquals(site.Unit, victim))
                {
                    site.NotifyJammed(jammer, jamAmount);
                    victimSite = site;
                }
            }

            if (!MdsConfig.EnableHojTriangulation.Value)
            {
                return;
            }

            FactionAffinity victimFaction = GameInteropHelpers.ToFactionAffinity(victim.NetworkHQ ?? victim.MapHQ);
            if (victimFaction == FactionAffinity.Any ||
                !GameInteropHelpers.IsHostileToAffinity(jammer, victimFaction, victim.NetworkHQ ?? victim.MapHQ))
            {
                return;
            }

            bool victimIsRadarOrFcu =
                victim.radar != null ||
                (victimSite != null &&
                 (victimSite.Spec.Role == WeaponCascadeRole.FireControl ||
                  victimSite.Spec.Role == WeaponCascadeRole.EarlyWarningRadar ||
                  (victimSite.Spec.SensorProfile != null && victimSite.Spec.SensorProfile.IsFireControlUnit)));

            Vector3 jammerPos = jammer.transform.position;
            int observers = 0;
            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior site = allSites[i];
                if (!site.IsAlive || site.Faction != victimFaction || site.Spec.SensorProfile == null)
                {
                    continue;
                }

                if (!site.Spec.SensorProfile.EnableHomeOnJamTriangulation &&
                    !SensorBandPhysics.HasBand(site.SupportedBandMask, SensorBand.PassiveElint))
                {
                    continue;
                }

                float maxRangeM = Mathf.Max(80000f, site.Spec.SensorProfile.DetectionRangeKm * 1500f);
                if (site.Unit != null && (site.Unit.transform.position - jammerPos).sqrMagnitude <= maxRangeM * maxRangeM)
                {
                    observers++;
                }
            }

            if (victimSite != null || victimIsRadarOrFcu)
            {
                observers = Math.Max(observers, 1);
            }

            if (observers <= 0)
            {
                return;
            }

            // Small triangulation error when only 1 receiver observes jammer; converges with >= 2 nodes.
            Vector3 estimatedPos = observers >= 2
                ? jammerPos
                : jammerPos + new Vector3(Mathf.Sin(Time.time * 1.7f) * 85f, 0f, Mathf.Cos(Time.time * 1.3f) * 85f);

            Vector3 vel = jammer.rb != null ? jammer.rb.velocity : Vector3.zero;
            UpsertHojTrack(victimFaction, jammer, estimatedPos, vel, observers, victimIsRadarOrFcu);
        }

        private void UpsertHojTrack(
            FactionAffinity faction,
            Unit jammer,
            Vector3 estimatedPos,
            Vector3 vel,
            int observers,
            bool victimIsRadarOrFcu)
        {
            for (int i = 0; i < activeHojTracks.Count; i++)
            {
                HojTrackEntry entry = activeHojTracks[i];
                if (entry.Faction == faction && ReferenceEquals(entry.JammerUnit, jammer))
                {
                    bool wakeNow = (!entry.VictimIsRadarOrFcu && victimIsRadarOrFcu) ||
                                   (Time.time - entry.LastUpdatedTime > 1.0f);
                    entry.TriangulatedWorldPos = estimatedPos;
                    entry.Velocity = vel;
                    entry.LastUpdatedTime = Time.time;
                    entry.ObserverCount = observers;
                    entry.VictimIsRadarOrFcu |= victimIsRadarOrFcu;
                    activeHojTracks[i] = entry;

                    if (wakeNow)
                    {
                        if (victimIsRadarOrFcu)
                        {
                            MdsLog.Info($"[HOJ] Jammer '{jammer.unitName}' jamming friendly FCU/radar for {faction}; waking WTA.");
                        }

                        MDSFramework.Wta.SmartEngagementEngine.Instance?.RequestImmediateEvaluate(faction);
                    }

                    return;
                }
            }

            activeHojTracks.Add(new HojTrackEntry
            {
                Faction = faction,
                JammerUnit = jammer,
                JammerId = jammer.persistentID,
                TriangulatedWorldPos = estimatedPos,
                Velocity = vel,
                LastUpdatedTime = Time.time,
                ObserverCount = observers,
                VictimIsRadarOrFcu = victimIsRadarOrFcu
            });

            if (victimIsRadarOrFcu)
            {
                MdsLog.Info($"[HOJ] New jammer track '{jammer.unitName}' against friendly FCU/radar for {faction}; triggering immediate VLS response.");
            }

            Wta.SmartEngagementEngine.Instance?.RequestImmediateEvaluate(faction);
        }

        private void PollActiveEcmEmissions()
        {
            if (!MdsConfig.EnableHojTriangulation.Value ||
                allSites.Count == 0 ||
                !GameInteropHelpers.IsServerAuthority() ||
                UnitRegistry.allUnits == null)
            {
                return;
            }

            List<Unit> allUnits = UnitRegistry.allUnits;
            for (int u = 0; u < allUnits.Count; u++)
            {
                if (allUnits[u] is not Aircraft ac ||
                    ac.disabled ||
                    GameInteropHelpers.IsAircraftSplashed(ac) ||
                    ac.GetECMIntensity() <= 0.15f)
                {
                    continue;
                }

                PollFactionEcmForAircraft(ac, FactionAffinity.Boscali);
                PollFactionEcmForAircraft(ac, FactionAffinity.Primeva);
            }
        }

        private void PollFactionEcmForAircraft(Aircraft ac, FactionAffinity faction)
        {
            FactionHQ? hq = GameInteropHelpers.TryResolveFactionHQ(faction);
            if (!GameInteropHelpers.IsHostileToAffinity(ac, faction, hq))
            {
                return;
            }

            Vector3 acPos = ac.transform.position;
            int observers = 0;
            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior site = allSites[i];
                if (!site.IsAlive || site.Faction != faction || site.Unit == null || site.Spec.SensorProfile == null)
                {
                    continue;
                }

                if (!site.Spec.SensorProfile.EnableHomeOnJamTriangulation &&
                    !SensorBandPhysics.HasBand(site.SupportedBandMask, SensorBand.PassiveElint))
                {
                    continue;
                }

                float maxRangeM = Mathf.Max(75000f, site.Spec.SensorProfile.DetectionRangeKm * 1500f);
                Vector3 siteEye = site.Unit.transform.position + (Vector3.up * 3f);
                if ((site.Unit.transform.position - acPos).sqrMagnitude <= maxRangeM * maxRangeM &&
                    !Physics.Linecast(siteEye, acPos, (int)PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                {
                    observers++;
                }
            }

            if (observers <= 0)
            {
                return;
            }

            Vector3 estimatedPos = observers >= 2
                ? acPos
                : acPos + new Vector3(Mathf.Sin(Time.time * 1.7f) * 85f, 0f, Mathf.Cos(Time.time * 1.3f) * 85f);
            Vector3 vel = ac.rb != null ? ac.rb.velocity : Vector3.zero;
            UpsertHojTrack(faction, ac, estimatedPos, vel, observers, victimIsRadarOrFcu: false);
        }

        internal void NotifyAradThreat(Radar? targetedRadar, Missile aradMissile)
        {
            if (!MdsConfig.EnableAradAutoEmcon.Value || aradMissile == null)
            {
                return;
            }

            Unit? radarUnit = targetedRadar != null ? targetedRadar.GetAttachedUnit() : null;
            Vector3 aradPos = aradMissile.transform.position;
            Vector3 aradVel = aradMissile.rb != null ? aradMissile.rb.velocity : Vector3.zero;
            bool hasVel = aradVel.sqrMagnitude > 25f;
            Vector3 aradDir = hasVel ? aradVel.normalized : aradMissile.transform.forward;

            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior site = allSites[i];
                if (!site.IsAlive || !site.IsRadarCapable || site.Unit == null || site.Spec.SensorProfile == null)
                {
                    continue;
                }

                if (!site.Spec.SensorProfile.IsFireControlUnit && site.Spec.Role != WeaponCascadeRole.EarlyWarningRadar)
                {
                    continue;
                }

                if (!GameInteropHelpers.IsHostileToAffinity(aradMissile, site.Faction, site.Hq))
                {
                    continue;
                }

                bool directTarget = radarUnit != null && ReferenceEquals(site.Unit, radarUnit);
                Vector3 toSite = site.Unit.transform.position - aradPos;
                float distSq = toSite.sqrMagnitude;
                bool closeIncoming = !directTarget &&
                                     site.IsRadarEmitting &&
                                     distSq <= 10000f * 10000f &&
                                     hasVel &&
                                     distSq > 1f &&
                                     Vector3.Dot(aradDir, toSite.normalized) > 0.70f;

                if (!directTarget && !closeIncoming)
                {
                    continue;
                }

                float shutdownDur = Mathf.Max(3f, site.Spec.SensorProfile.AradEmconShutdownSeconds);

                if (site.EmconState != FcuEmconState.AradShutdown)
                {
                    site.AradShutdownUntil = Time.time + shutdownDur;
                    site.ForcedActiveUntil = 0f;
                    site.EmconState = FcuEmconState.AradShutdown;
                    MdsLog.Info($"[ECCM] ARAD threat detected near '{site.SiteId}' -> EMCON shutdown for {shutdownDur:0.0}s.");
                    PromotePeerFcuForHandoff(site);
                }
            }
        }

        private void PromotePeerFcuForHandoff(ModularSiteBehavior downedFcu)
        {
            if (downedFcu.Unit == null)
            {
                return;
            }

            float radiusSq = Mathf.Pow(Mathf.Max(2000f, downedFcu.Spec.FcuLinkRadiusKm * 1000f), 2f);
            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior candidate = allSites[i];
                if (!candidate.IsAlive || ReferenceEquals(candidate, downedFcu) || candidate.Faction != downedFcu.Faction)
                {
                    continue;
                }

                if (candidate.Spec.SensorProfile == null || !candidate.Spec.SensorProfile.IsFireControlUnit)
                {
                    continue;
                }

                if (candidate.EmconState == FcuEmconState.AradShutdown)
                {
                    continue;
                }

                bool sameCommandNet = downedFcu.IsInCommandNet &&
                                      candidate.IsInCommandNet &&
                                      downedFcu.CommandNetId != 0 &&
                                      downedFcu.CommandNetId == candidate.CommandNetId;

                if (sameCommandNet ||
                    (candidate.Unit != null &&
                     (candidate.Unit.transform.position - downedFcu.Unit.transform.position).sqrMagnitude <= radiusSq))
                {
                    candidate.ForcedActiveUntil = Mathf.Max(candidate.ForcedActiveUntil, downedFcu.AradShutdownUntil);
                    candidate.PopUpRequested = true;
                    candidate.EmconState = FcuEmconState.BlinkingActive;
                    MdsLog.Info($"[ECCM] Auto-handoff FCU fire-control from '{downedFcu.SiteId}' -> '{candidate.SiteId}' (net={sameCommandNet}).");
                    return;
                }
            }
        }

        private void RefreshLinksAndBlinking(bool forceLinkRefresh)
        {
            int deployedHubCount = 0;
            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior s = allSites[i];
                if (s.IsAlive && !s.SuspendedInTransit && s.IsCommandCenter && s.IsDeployed)
                {
                    deployedHubCount++;
                }
            }

            if (forceLinkRefresh || deployedHubCount != lastDeployedHubCount)
            {
                lastDeployedHubCount = deployedHubCount;
                RebuildFcuLinks();
            }

            UpdateFcuEmconAndBlinkingForFaction(FactionAffinity.Boscali);
            UpdateFcuEmconAndBlinkingForFaction(FactionAffinity.Primeva);
            UpdateFcuEmconAndBlinkingForFaction(FactionAffinity.Any);
            RefreshCommandNetActiveFcuCounts();

            int aradShutdownCount = 0;
            for (int i = 0; i < allSites.Count; i++)
            {
                if (allSites[i].EmconState == FcuEmconState.AradShutdown)
                {
                    aradShutdownCount++;
                }
            }

            ActiveAradAlerts = aradShutdownCount;
        }

        private void RefreshCommandNetActiveFcuCounts()
        {
            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior site = allSites[i];
                if (!site.IsAlive || !site.IsInCommandNet || site.CommandNetId == 0)
                {
                    continue;
                }

                int netId = site.CommandNetId;
                int activeCount = 0;
                for (int j = 0; j < allSites.Count; j++)
                {
                    ModularSiteBehavior cand = allSites[j];
                    if (cand.IsAlive &&
                        cand.IsInCommandNet &&
                        cand.CommandNetId == netId &&
                        cand.Spec.SensorProfile != null &&
                        cand.Spec.SensorProfile.IsFireControlUnit &&
                        cand.IsRadarEmitting)
                    {
                        activeCount++;
                    }
                }

                site.CommandNetActiveFcuCount = activeCount;
            }
        }

        private void RebuildFcuLinks()
        {
            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior site = allSites[i];
                site.LinkedFcus.Clear();
                site.IsInCommandNet = false;
                site.CommandNetId = 0;
                site.CommandNetFcuCount = 0;
                site.CommandNetActiveFcuCount = 0;
                site.CommandNetEffectorCount = 0;
                site.EffectiveNetworkEccmBonus = 0f;
                site.EffectiveNetworkDetectionMultiplier = 1f;

                if (site.NativeRadar != null && GameInteropHelpers.RadarJamToleranceField != null && site.Spec.SensorProfile != null)
                {
                    GameInteropHelpers.RadarJamToleranceField.SetValue(site.NativeRadar, site.Spec.SensorProfile.JamTolerance);
                }

                if (!site.IsAlive || site.SuspendedInTransit || !site.Spec.RequiresFcuLink || site.Unit == null)
                {
                    continue;
                }

                float radiusSq = Mathf.Pow(Mathf.Max(2000f, site.Spec.FcuLinkRadiusKm * 1000f), 2f);
                Vector3 myPos = site.Unit.transform.position;

                for (int j = 0; j < allSites.Count; j++)
                {
                    ModularSiteBehavior cand = allSites[j];
                    if (!cand.IsAlive || cand.SuspendedInTransit || cand.Faction != site.Faction || cand.Unit == null)
                    {
                        continue;
                    }

                    if (cand.Spec.SensorProfile != null && cand.Spec.SensorProfile.IsFireControlUnit)
                    {
                        if ((cand.Unit.transform.position - myPos).sqrMagnitude <= radiusSq)
                        {
                            site.LinkedFcus.Add(cand);
                        }
                    }
                }
            }

            int nextNetId = 1;
            BuildSkyshieldNetClustersForFaction(FactionAffinity.Boscali, ref nextNetId);
            BuildSkyshieldNetClustersForFaction(FactionAffinity.Primeva, ref nextNetId);
            BuildSkyshieldNetClustersForFaction(FactionAffinity.Any, ref nextNetId);
        }

        private void BuildSkyshieldNetClustersForFaction(FactionAffinity faction, ref int nextNetId)
        {
            scratchHubs.Clear();
            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior s = allSites[i];
                if (s.IsAlive && !s.SuspendedInTransit && s.IsDeployed && s.IsCommandCenter && s.Faction == faction && s.Unit != null)
                {
                    scratchHubs.Add(s);
                }
            }

            if (scratchHubs.Count == 0)
            {
                return;
            }

            visitedHubs.Clear();
            for (int i = 0; i < scratchHubs.Count; i++)
            {
                visitedHubs.Add(false);
            }

            for (int seed = 0; seed < scratchHubs.Count; seed++)
            {
                if (visitedHubs[seed])
                {
                    continue;
                }

                netHubCluster.Clear();
                visitedHubs[seed] = true;
                netHubCluster.Add(scratchHubs[seed]);

                for (int q = 0; q < netHubCluster.Count; q++)
                {
                    ModularSiteBehavior hubA = netHubCluster[q];
                    Vector3 posA = hubA.Unit!.transform.position;
                    for (int cand = 0; cand < scratchHubs.Count; cand++)
                    {
                        if (visitedHubs[cand])
                        {
                            continue;
                        }

                        ModularSiteBehavior hubB = scratchHubs[cand];
                        float relayRadiusM = Mathf.Max(5000f, Mathf.Max(hubA.Spec.NetworkRelayRadiusKm, hubB.Spec.NetworkRelayRadiusKm) * 1000f);
                        if ((hubB.Unit!.transform.position - posA).sqrMagnitude <= relayRadiusM * relayRadiusM)
                        {
                            visitedHubs[cand] = true;
                            netHubCluster.Add(hubB);
                        }
                    }
                }

                int netId = nextNetId++;
                netMembers.Clear();
                netFcus.Clear();

                float maxEccmBonus = 0f;
                float maxDetectMult = 1f;
                for (int h = 0; h < netHubCluster.Count; h++)
                {
                    ModularSiteBehavior hub = netHubCluster[h];
                    netMembers.Add(hub);
                    maxEccmBonus = Mathf.Max(maxEccmBonus, Mathf.Max(0f, hub.Spec.NetworkEccmBonus));
                    maxDetectMult = Mathf.Max(maxDetectMult, Mathf.Max(1f, hub.Spec.NetworkDetectionBonusMultiplier));
                }

                // Step 1: Collect all friendly sites (FCUs, EWRs, effectors) within relay radius of any hub in this cluster.
                for (int i = 0; i < allSites.Count; i++)
                {
                    ModularSiteBehavior cand = allSites[i];
                    if (!cand.IsAlive || cand.SuspendedInTransit || cand.IsCommandCenter || cand.IsInCommandNet ||
                        cand.Faction != faction || cand.Unit == null)
                    {
                        continue;
                    }

                    Vector3 candPos = cand.Unit.transform.position;
                    bool withinHub = false;
                    for (int h = 0; h < netHubCluster.Count; h++)
                    {
                        ModularSiteBehavior hub = netHubCluster[h];
                        float relayM = Mathf.Max(5000f, hub.Spec.NetworkRelayRadiusKm * 1000f);
                        if ((candPos - hub.Unit!.transform.position).sqrMagnitude <= relayM * relayM)
                        {
                            withinHub = true;
                            break;
                        }
                    }

                    if (!withinHub)
                    {
                        continue;
                    }

                    netMembers.Add(cand);
                    if (cand.Spec.SensorProfile != null && cand.Spec.SensorProfile.IsFireControlUnit)
                    {
                        netFcus.Add(cand);
                    }
                }

                // Step 2: Include nearby effectors directly linked to an FCU inside this net cluster (CEC extension).
                for (int i = 0; i < allSites.Count; i++)
                {
                    ModularSiteBehavior cand = allSites[i];
                    if (!cand.IsAlive || cand.SuspendedInTransit || !cand.IsWeaponSite || cand.IsInCommandNet ||
                        cand.Faction != faction || cand.Unit == null || netMembers.Contains(cand))
                    {
                        continue;
                    }

                    for (int f = 0; f < netFcus.Count; f++)
                    {
                        if (cand.LinkedFcus.Contains(netFcus[f]))
                        {
                            netMembers.Add(cand);
                            break;
                        }
                    }
                }

                int fcuCount = netFcus.Count;
                int activeFcuCount = 0;
                for (int f = 0; f < netFcus.Count; f++)
                {
                    if (netFcus[f].IsRadarEmitting)
                    {
                        activeFcuCount++;
                    }
                }

                int effectorCount = 0;
                for (int m = 0; m < netMembers.Count; m++)
                {
                    if (netMembers[m].IsWeaponSite)
                    {
                        effectorCount++;
                    }
                }

                float eccmBonus = fcuCount >= 2 ? maxEccmBonus : 0f;
                float detectMult = fcuCount >= 2 ? maxDetectMult : 1f;

                for (int m = 0; m < netMembers.Count; m++)
                {
                    ModularSiteBehavior member = netMembers[m];
                    member.IsInCommandNet = true;
                    member.CommandNetId = netId;
                    member.CommandNetFcuCount = fcuCount;
                    member.CommandNetActiveFcuCount = activeFcuCount;
                    member.CommandNetEffectorCount = effectorCount;
                    member.EffectiveNetworkEccmBonus = eccmBonus;
                    member.EffectiveNetworkDetectionMultiplier = detectMult;

                    if (member.Spec.RequiresFcuLink)
                    {
                        for (int f = 0; f < netFcus.Count; f++)
                        {
                            ModularSiteBehavior fcu = netFcus[f];
                            if (!member.LinkedFcus.Contains(fcu))
                            {
                                member.LinkedFcus.Add(fcu);
                            }
                        }
                    }

                    if (eccmBonus > 0f && member.NativeRadar != null && GameInteropHelpers.RadarJamToleranceField != null && member.Spec.SensorProfile != null)
                    {
                        float boostedTol = Mathf.Clamp(member.Spec.SensorProfile.JamTolerance + eccmBonus, 0f, 0.92f);
                        GameInteropHelpers.RadarJamToleranceField.SetValue(member.NativeRadar, boostedTol);
                    }
                }
            }
        }

        private void UpdateFcuEmconAndBlinkingForFaction(FactionAffinity faction)
        {
            scratchFcus.Clear();
            scratchEffectors.Clear();
            for (int i = 0; i < allSites.Count; i++)
            {
                ModularSiteBehavior site = allSites[i];
                if (!site.IsAlive || site.IsCommandCenter || site.Faction != faction || site.Spec.SensorProfile == null)
                {
                    continue;
                }

                if (site.Spec.SensorProfile.IsFireControlUnit)
                {
                    scratchFcus.Add(site);
                }
                else
                {
                    scratchEffectors.Add(site);
                }
            }

            if (scratchFcus.Count > 0)
            {
                float now = Time.time;
                eligibleBlinkFcus.Clear();
                for (int i = 0; i < scratchFcus.Count; i++)
                {
                    ModularSiteBehavior fcu = scratchFcus[i];

                    if (fcu.EmconState == FcuEmconState.AradShutdown)
                    {
                        if (now >= fcu.AradShutdownUntil)
                        {
                            fcu.EmconState = FcuEmconState.Normal;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    if (now < fcu.ForcedActiveUntil)
                    {
                        fcu.EmconState = FcuEmconState.BlinkingActive;
                        continue;
                    }

                    if (MdsConfig.EnableTwoFcuBlinking.Value &&
                        MdsConfig.EnableEccmNetwork.Value &&
                        fcu.Spec.SensorProfile.EnableTwoFcuBlinking &&
                        fcu.IsDeployed)
                    {
                        eligibleBlinkFcus.Add(fcu);
                    }
                    else
                    {
                        fcu.EmconState = FcuEmconState.Normal;
                    }
                }

                if (eligibleBlinkFcus.Count < 2)
                {
                    for (int i = 0; i < eligibleBlinkFcus.Count; i++)
                    {
                        eligibleBlinkFcus[i].EmconState = FcuEmconState.Normal;
                    }
                }
                else
                {
                    visitedFcus.Clear();
                    for (int i = 0; i < eligibleBlinkFcus.Count; i++)
                    {
                        visitedFcus.Add(false);
                    }

                    for (int seed = 0; seed < eligibleBlinkFcus.Count; seed++)
                    {
                        if (visitedFcus[seed])
                        {
                            continue;
                        }

                        clusterFcus.Clear();
                        visitedFcus[seed] = true;
                        clusterFcus.Add(eligibleBlinkFcus[seed]);

                        for (int q = 0; q < clusterFcus.Count; q++)
                        {
                            ModularSiteBehavior a = clusterFcus[q];
                            if (a.Unit == null)
                            {
                                continue;
                            }

                            Vector3 posA = a.Unit.transform.position;
                            for (int cand = 0; cand < eligibleBlinkFcus.Count; cand++)
                            {
                                if (visitedFcus[cand])
                                {
                                    continue;
                                }

                                ModularSiteBehavior b = eligibleBlinkFcus[cand];
                                if (b.Unit == null)
                                {
                                    continue;
                                }

                                float linkM = Mathf.Max(10000f, Mathf.Max(a.Spec.FcuLinkRadiusKm, b.Spec.FcuLinkRadiusKm) * 1500f);
                                bool netConnected = a.IsInCommandNet &&
                                                    b.IsInCommandNet &&
                                                    a.CommandNetId != 0 &&
                                                    a.CommandNetId == b.CommandNetId;
                                if (netConnected || (b.Unit.transform.position - posA).sqrMagnitude <= linkM * linkM)
                                {
                                    visitedFcus[cand] = true;
                                    clusterFcus.Add(b);
                                }
                            }
                        }

                        if (clusterFcus.Count < 2)
                        {
                            clusterFcus[0].EmconState = FcuEmconState.Normal;
                            continue;
                        }

                        // Stagger active windows across N >= 2 FCUs in each local cluster with overlap handoff.
                        float onSec = Mathf.Max(3f, clusterFcus[0].Spec.SensorProfile.BlinkOnSeconds);
                        float offSec = Mathf.Max(1f, clusterFcus[0].Spec.SensorProfile.BlinkOffSeconds);
                        float cycleSlotSec = clusterFcus.Count == 2
                            ? Mathf.Max(3f, (onSec + offSec) * 0.5f)
                            : onSec;
                        float litDuration = clusterFcus.Count == 2
                            ? Mathf.Max(cycleSlotSec, onSec)
                            : cycleSlotSec;
                        float totalCycle = cycleSlotSec * clusterFcus.Count;
                        float phase = blinkClock % totalCycle;

                        for (int idx = 0; idx < clusterFcus.Count; idx++)
                        {
                            float slotStart = idx * cycleSlotSec;
                            float slotEnd = slotStart + litDuration + BlinkOverlapSeconds;
                            bool isLit = (phase >= slotStart && phase <= slotEnd) ||
                                         (slotEnd > totalCycle && phase <= (slotEnd - totalCycle)) ||
                                         (idx == 0 && phase >= totalCycle - BlinkOverlapSeconds);

                            clusterFcus[idx].EmconState = isLit ? FcuEmconState.BlinkingActive : FcuEmconState.BlinkingSilent;
                        }
                    }
                }
            }

            for (int i = 0; i < scratchEffectors.Count; i++)
            {
                UpdateEffectorBackupState(scratchEffectors[i]);
            }
        }

        private static void UpdateEffectorBackupState(ModularSiteBehavior effector)
        {
            if (effector.EmconState == FcuEmconState.AradShutdown && Time.time < effector.AradShutdownUntil)
            {
                return;
            }

            if (effector.Spec.RequiresFcuLink && !effector.HasFireControlGuidance)
            {
                effector.EmconState = FcuEmconState.EoIrEmergencyOnly;
            }
            else
            {
                effector.EmconState = FcuEmconState.Normal;
            }
        }

        private void PruneExpiredHojTracks()
        {
            float now = Time.time;
            for (int i = activeHojTracks.Count - 1; i >= 0; i--)
            {
                HojTrackEntry entry = activeHojTracks[i];
                if (entry.JammerUnit == null ||
                    entry.JammerUnit.disabled ||
                    GameInteropHelpers.IsAircraftSplashed(entry.JammerUnit) ||
                    now - entry.LastUpdatedTime > HojTrackExpirySeconds)
                {
                    activeHojTracks.RemoveAt(i);
                }
            }
        }
    }
}
