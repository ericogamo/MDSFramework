using System;
using System.Collections.Generic;
using MDSFramework.Api;
using MDSFramework.Patches;
using MDSFramework.Sensors;
using UnityEngine;

namespace MDSFramework.Missiles
{
    internal sealed class AbmEarlyWarningService
    {
        internal struct BallisticWarningEntry
        {
            internal PersistentID ThreatId;
            internal Missile? ThreatMissile;
            internal FactionAffinity TargetFaction;
            internal Vector3 LastPosition;
            internal float BlastYield;
            internal float DetectedTime;
        }

        internal static AbmEarlyWarningService? Instance { get; private set; }

        private readonly List<BallisticWarningEntry> activeWarnings = new List<BallisticWarningEntry>(16);
        private readonly HashSet<uint> notifiedThreatIds = new HashSet<uint>();
        private IRtsAbmStockpileBridge? subscribedBridge;

        internal IReadOnlyList<BallisticWarningEntry> ActiveWarnings => activeWarnings;
        internal int ActiveWarningCount => activeWarnings.Count;

        internal AbmEarlyWarningService()
        {
            Instance = this;
            RebindBridgeEvents();
            RtsBridgeHost.OnBridgeChanged += RebindBridgeEvents;
        }

        internal void Dispose()
        {
            RtsBridgeHost.OnBridgeChanged -= RebindBridgeEvents;
            if (subscribedBridge != null)
            {
                subscribedBridge.OnBallisticThreatDetected -= OnExternalBallisticThreat;
                subscribedBridge = null;
            }
        }

        internal void ResetSession()
        {
            activeWarnings.Clear();
            notifiedThreatIds.Clear();
        }

        internal void Tick()
        {
            for (int i = activeWarnings.Count - 1; i >= 0; i--)
            {
                BallisticWarningEntry entry = activeWarnings[i];
                if (entry.ThreatMissile == null || entry.ThreatMissile.disabled)
                {
                    if (entry.ThreatId.IsValid)
                    {
                        notifiedThreatIds.Remove(entry.ThreatId.Id);
                    }

                    activeWarnings.RemoveAt(i);
                }
                else
                {
                    entry.LastPosition = entry.ThreatMissile.transform.position;
                    activeWarnings[i] = entry;
                }
            }
        }

        internal void NotifyPotentialBallisticLaunch(Missile missile, bool hasBallisticGuidance)
        {
            if (!MdsConfig.EnableAbmEarlyWarning.Value || missile == null || missile.disabled)
            {
                return;
            }

            if (PendingMdsSpawnScope.TryPeek(out PendingMdsSpawnContext pending) &&
                pending.MissileSpec != null &&
                (pending.MissileSpec.IsAbmInterceptor ||
                 pending.MissileSpec.IsAntiShip ||
                 (!hasBallisticGuidance && pending.MissileSpec.BlastYield <= MdsConfig.AbmBlastYieldThreshold.Value)))
            {
                return;
            }

            if (CustomMissileTracker.TryGet(missile, out CustomMissileBehavior custom) &&
                (custom.Spec.IsAbmInterceptor || custom.Spec.IsAntiShip))
            {
                return;
            }

            float yield = GameInteropHelpers.SafeGetBlastYield(missile);
            bool nuclearThreat = yield > MdsConfig.AbmBlastYieldThreshold.Value;
            if (!hasBallisticGuidance && !nuclearThreat)
            {
                return;
            }

            PersistentID pid = missile.persistentID;
            if (pid.IsValid && notifiedThreatIds.Contains(pid.Id))
            {
                return;
            }

            if (pid.IsValid)
            {
                notifiedThreatIds.Add(pid.Id);
            }

            FactionAffinity launcherFaction = GameInteropHelpers.ToFactionAffinity(missile.NetworkHQ ?? missile.MapHQ);
            FactionAffinity targetFaction = launcherFaction == FactionAffinity.Boscali
                ? FactionAffinity.Primeva
                : launcherFaction == FactionAffinity.Primeva
                    ? FactionAffinity.Boscali
                    : FactionAffinity.Any;

            Vector3 pos = missile.transform.position;
            activeWarnings.Add(new BallisticWarningEntry
            {
                ThreatId = pid,
                ThreatMissile = missile,
                TargetFaction = targetFaction,
                LastPosition = pos,
                BlastYield = yield,
                DetectedTime = Time.time
            });

            MdsLog.Warn(
                $"[ABM EARLY WARNING] Ballistic/Nuclear launch detected: '{missile.unitName}' " +
                $"(yield={yield:0}t, ballistic={hasBallisticGuidance}, targetSide={targetFaction}).");

            // Pop up ABM VLS containers immediately for the threatened side (via bridge event if vanilla bridge, else direct).
            if (RtsBridgeHost.Current.AbmStockpile is VanillaBridgeFallbacks.VanillaAbmStockpileBridge vanillaBridge)
            {
                vanillaBridge.RaiseBallisticThreatDetected(targetFaction, pos, yield);
            }
            else
            {
                PopUpAbmSitesForFaction(targetFaction);
            }
        }

        internal bool HasActiveWarningForFaction(FactionAffinity faction)
        {
            for (int i = 0; i < activeWarnings.Count; i++)
            {
                FactionAffinity target = activeWarnings[i].TargetFaction;
                if (faction == FactionAffinity.Any || target == FactionAffinity.Any || target == faction)
                {
                    return true;
                }
            }

            return false;
        }

        internal bool IsKnownBallisticOrNuclear(Unit unit)
        {
            if (unit is not Missile missile)
            {
                return false;
            }

            if (missile.GetComponent<BallisticMissileGuidance>() != null)
            {
                return true;
            }

            if (CustomMissileTracker.TryGet(missile, out CustomMissileBehavior custom))
            {
                if (custom.Spec.IsAbmInterceptor || custom.Spec.IsAntiShip)
                {
                    return false;
                }

                if (custom.Spec.FlightProfile == MissileFlightProfile.Ballistic ||
                    custom.Spec.FlightProfile == MissileFlightProfile.MarvTerminal)
                {
                    return true;
                }
            }

            if (GameInteropHelpers.SafeGetBlastYield(missile) > MdsConfig.AbmBlastYieldThreshold.Value)
            {
                return true;
            }

            return false;
        }

        private void RebindBridgeEvents()
        {
            if (subscribedBridge != null)
            {
                subscribedBridge.OnBallisticThreatDetected -= OnExternalBallisticThreat;
            }

            subscribedBridge = RtsBridgeHost.Current.AbmStockpile;
            if (subscribedBridge != null)
            {
                subscribedBridge.OnBallisticThreatDetected += OnExternalBallisticThreat;
            }
        }

        private void OnExternalBallisticThreat(FactionAffinity targetFaction, Vector3 originPos, float blastYield)
        {
            MdsLog.Debug($"[ABM] Bridge ballistic threat event: faction={targetFaction}, yield={blastYield:0}t, origin={originPos}.");
            PopUpAbmSitesForFaction(targetFaction);
        }

        private static void PopUpAbmSitesForFaction(FactionAffinity targetFaction)
        {
            if (SensorNetworkManager.Instance == null)
            {
                return;
            }

            IReadOnlyList<ModularSiteBehavior> sites = SensorNetworkManager.Instance.AllSites;
            for (int i = 0; i < sites.Count; i++)
            {
                ModularSiteBehavior site = sites[i];
                if (!site.IsAlive || site.Spec.Role != WeaponCascadeRole.AbmVls)
                {
                    continue;
                }

                if (targetFaction == FactionAffinity.Any || site.Faction == targetFaction || site.Faction == FactionAffinity.Any)
                {
                    site.LastThreatSeenTime = Time.time;
                    site.RequestDeployState(true);
                }
            }
        }
    }
}
