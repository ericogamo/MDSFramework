using System;
using System.Collections.Generic;
using System.Globalization;
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
            internal FactionHQ? LauncherHq;
            internal FactionHQ? TargetHq;
            internal Vector3 LastPosition;
            internal float BlastYield;
            internal float DetectedTime;
        }

        internal static AbmEarlyWarningService? Instance { get; private set; }

        private readonly List<BallisticWarningEntry> activeWarnings = new List<BallisticWarningEntry>(16);
        private readonly HashSet<uint> notifiedThreatIds = new HashSet<uint>();
        private IRtsAbmStockpileBridge? subscribedBridge;
        private FactionHQ? pendingExternalLauncherHq;
        private FactionHQ? pendingExternalTargetHq;

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
            pendingExternalLauncherHq = null;
            pendingExternalTargetHq = null;
        }

        internal void ApplyClientSnapshot(string encoded)
        {
            activeWarnings.Clear();
            notifiedThreatIds.Clear();
            if (string.IsNullOrEmpty(encoded))
            {
                return;
            }

            string[] rows = encoded.Split(';');
            for (int i = 0; i < rows.Length; i++)
            {
                string[] cols = rows[i].Split(',');
                if (cols.Length < 6)
                {
                    continue;
                }

                uint pidId = uint.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint p) ? p : 0u;
                int fInt = int.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fi) ? fi : 0;
                float x = float.TryParse(cols[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float vx) ? vx : 0f;
                float y = float.TryParse(cols[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float vy) ? vy : 0f;
                float z = float.TryParse(cols[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float vz) ? vz : 0f;
                float yield = float.TryParse(cols[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float yld) ? yld : 0f;

                var pid = new PersistentID { Id = pidId };
                Missile? m = null;
                if (pidId != 0 && pid.TryGetUnit(out Unit u) && u is Missile missile)
                {
                    m = missile;
                    notifiedThreatIds.Add(pidId);
                }

                activeWarnings.Add(new BallisticWarningEntry
                {
                    ThreatId = pid,
                    ThreatMissile = m,
                    TargetFaction = Enum.IsDefined(typeof(FactionAffinity), fInt) ? (FactionAffinity)fInt : FactionAffinity.Any,
                    LastPosition = new Vector3(x, y, z),
                    BlastYield = yield,
                    DetectedTime = Time.time
                });
            }
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
            if (!GameInteropHelpers.IsServerAuthority() || !MdsConfig.EnableAbmEarlyWarning.Value || missile == null || missile.disabled)
            {
                return;
            }

            if (PendingMdsSpawnScope.TryPeek(out PendingMdsSpawnContext pending) &&
                pending.MissileSpec != null &&
                (pending.MissileSpec.IsAbmInterceptor ||
                 pending.MissileSpec.IsAntiShip ||
                 pending.MissileSpec.IsAntiVehicle ||
                 (!hasBallisticGuidance && pending.MissileSpec.BlastYield <= MdsConfig.AbmBlastYieldThreshold.Value)))
            {
                return;
            }

            if (CustomMissileTracker.TryGet(missile, out CustomMissileBehavior custom) &&
                (custom.Spec.IsAbmInterceptor || custom.Spec.IsAntiShip || custom.Spec.IsAntiVehicle))
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

            FactionHQ? launcherHq = missile.NetworkHQ ?? missile.MapHQ;
            FactionHQ? targetHq = ResolveTargetHq(missile, launcherHq);
            FactionAffinity targetFaction = ResolveTargetFaction(launcherHq, targetHq);

            Vector3 pos = missile.transform.position;
            activeWarnings.Add(new BallisticWarningEntry
            {
                ThreatId = pid,
                ThreatMissile = missile,
                TargetFaction = targetFaction,
                LauncherHq = launcherHq,
                TargetHq = targetHq,
                LastPosition = pos,
                BlastYield = yield,
                DetectedTime = Time.time
            });

            MdsLog.Warn(
                $"[ABM EARLY WARNING] Ballistic/Nuclear launch detected: '{missile.unitName}' " +
                $"(yield={yield:0}t, ballistic={hasBallisticGuidance}, targetSide={targetFaction}, targetHq={targetHq?.faction?.factionTag ?? "AnyHostile"}).");

            // Pop up ABM VLS containers immediately for the threatened side (via bridge event if vanilla bridge, else direct).
            if (RtsBridgeHost.Current.AbmStockpile is VanillaBridgeFallbacks.VanillaAbmStockpileBridge vanillaBridge)
            {
                pendingExternalLauncherHq = launcherHq;
                pendingExternalTargetHq = targetHq;
                try
                {
                    vanillaBridge.RaiseBallisticThreatDetected(targetFaction, pos, yield);
                }
                finally
                {
                    pendingExternalLauncherHq = null;
                    pendingExternalTargetHq = null;
                }
            }
            else
            {
                PopUpAbmSitesForFaction(targetFaction, launcherHq, targetHq);
            }
        }

        private static FactionHQ? ResolveTargetHq(Missile missile, FactionHQ? launcherHq)
        {
            if (missile.targetID.IsValid && missile.targetID.TryGetUnit(out Unit targetUnit) && !targetUnit.disabled)
            {
                FactionHQ? uHq = targetUnit.NetworkHQ ?? targetUnit.MapHQ;
                if (uHq != null && !ReferenceEquals(uHq, launcherHq))
                {
                    return uHq;
                }
            }

            if (launcherHq == null)
            {
                return null;
            }

            FactionHQ? singleHostileHq = null;
            int hostileCount = 0;
            try
            {
                foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
                {
                    if (hq == null || ReferenceEquals(hq, launcherHq))
                    {
                        continue;
                    }

                    singleHostileHq = hq;
                    hostileCount++;
                    if (hostileCount > 1)
                    {
                        return null;
                    }
                }
            }
            catch
            {
                return null;
            }

            return hostileCount == 1 ? singleHostileHq : null;
        }

        private static FactionAffinity ResolveTargetFaction(FactionHQ? launcherHq, FactionHQ? targetHq)
        {
            if (targetHq != null)
            {
                FactionAffinity targetAffinity = GameInteropHelpers.ToFactionAffinity(targetHq);
                if (targetAffinity != FactionAffinity.Any)
                {
                    return targetAffinity;
                }
            }

            FactionAffinity launcherFaction = GameInteropHelpers.ToFactionAffinity(launcherHq);
            if (launcherFaction == FactionAffinity.Boscali)
            {
                return FactionAffinity.Primeva;
            }

            if (launcherFaction == FactionAffinity.Primeva)
            {
                return FactionAffinity.Boscali;
            }

            if (launcherHq != null)
            {
                try
                {
                    FactionAffinity uniqueHostileAffinity = FactionAffinity.Any;
                    int distinctAffinities = 0;
                    foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
                    {
                        if (hq == null || ReferenceEquals(hq, launcherHq))
                        {
                            continue;
                        }

                        FactionAffinity aff = GameInteropHelpers.ToFactionAffinity(hq);
                        if (aff != FactionAffinity.Any && aff != uniqueHostileAffinity)
                        {
                            uniqueHostileAffinity = aff;
                            distinctAffinities++;
                        }
                    }

                    if (distinctAffinities == 1)
                    {
                        return uniqueHostileAffinity;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return FactionAffinity.Any;
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

        internal bool HasActiveWarningForHq(FactionHQ? hq, FactionAffinity fallbackFaction)
        {
            for (int i = 0; i < activeWarnings.Count; i++)
            {
                BallisticWarningEntry entry = activeWarnings[i];
                if (hq != null && entry.LauncherHq != null && ReferenceEquals(hq, entry.LauncherHq))
                {
                    continue;
                }

                if (hq != null && entry.TargetHq != null)
                {
                    if (ReferenceEquals(hq, entry.TargetHq))
                    {
                        return true;
                    }

                    continue;
                }

                if (fallbackFaction == FactionAffinity.Any || entry.TargetFaction == FactionAffinity.Any || entry.TargetFaction == fallbackFaction)
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
                if (custom.Spec.IsAbmInterceptor || custom.Spec.IsAntiShip || custom.Spec.IsAntiVehicle)
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
            if (!GameInteropHelpers.IsServerAuthority())
            {
                return;
            }

            MdsLog.Debug($"[ABM] Bridge ballistic threat event: faction={targetFaction}, yield={blastYield:0}t, origin={originPos}.");
            PopUpAbmSitesForFaction(targetFaction, pendingExternalLauncherHq, pendingExternalTargetHq);
        }

        private static void PopUpAbmSitesForFaction(FactionAffinity targetFaction, FactionHQ? launcherHq = null, FactionHQ? targetHq = null)
        {
            if (!GameInteropHelpers.IsServerAuthority() || SensorNetworkManager.Instance == null)
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

                if (launcherHq != null && site.Hq != null && ReferenceEquals(site.Hq, launcherHq))
                {
                    continue;
                }

                bool factionMatch;
                if (targetHq != null && site.Hq != null)
                {
                    factionMatch = ReferenceEquals(site.Hq, targetHq);
                }
                else
                {
                    factionMatch = targetFaction == FactionAffinity.Any || site.Faction == targetFaction || site.Faction == FactionAffinity.Any;
                }

                if (factionMatch)
                {
                    site.LastThreatSeenTime = Time.time;
                    site.RequestDeployState(true);
                }
            }
        }
    }
}
