using System;
using System.Collections.Generic;
using UnityEngine;

namespace MDSFramework.Api
{
    internal static class VanillaBridgeFallbacks
    {
        internal static readonly VanillaEconomyBridge Economy = new VanillaEconomyBridge();
        internal static readonly VanillaCapBridge Caps = new VanillaCapBridge();
        internal static readonly VanillaLogisticsBridge Logistics = new VanillaLogisticsBridge();
        internal static readonly VanillaAbmStockpileBridge AbmStockpile = new VanillaAbmStockpileBridge();
        internal static readonly VanillaAiThreatSink AiThreats = new VanillaAiThreatSink();

        internal static void ResetSession()
        {
            Caps.ResetSession();
            AbmStockpile.ResetSession();
            AiThreats.ResetSession();
        }

        internal static void Tick(float deltaTime)
        {
            AbmStockpile.Tick(deltaTime);
        }

        internal sealed class VanillaEconomyBridge : IRtsEconomyBridge
        {
            public int QueryFactionTechTier(FactionAffinity faction)
            {
                return 3;
            }

            public bool TrySpendFactionCredits(FactionAffinity faction, float amount, string reason)
            {
                bool freeSandbox = GameManager.gameState == GameState.SinglePlayer && MdsConfig.VanillaFreeSandboxSpawn.Value;
                if (Mathf.Approximately(amount, 0f) || freeSandbox)
                {
                    return true;
                }

                FactionHQ? hq = GameInteropHelpers.TryResolveFactionHQ(faction);
                if (amount < 0f)
                {
                    if (hq != null)
                    {
                        hq.AddFunds(-amount);
                    }

                    return true;
                }

                if (hq == null)
                {
                    return GameManager.gameState == GameState.SinglePlayer;
                }

                if (hq.factionFunds < amount)
                {
                    return false;
                }

                hq.AddFunds(-amount);
                return true;
            }

            public bool IsPackageAllowedForFaction(ContractorPackage package, FactionAffinity faction)
            {
                if (package == null)
                {
                    return false;
                }

                if (package.DefaultFaction == FactionAffinity.Any)
                {
                    return true;
                }

                FactionAffinity effective = faction == FactionAffinity.Any
                    ? GameInteropHelpers.ResolveConcreteFaction(faction, out _)
                    : faction;

                return effective != FactionAffinity.Any && package.DefaultFaction == effective;
            }
        }

        internal sealed class VanillaCapBridge : IRtsCapBridge
        {
            private readonly Dictionary<(FactionAffinity, string), int> usedCaps = new Dictionary<(FactionAffinity, string), int>();

            public bool CanConsumeCap(FactionAffinity faction, string capCategory, int weight, out string blockReason)
            {
                blockReason = string.Empty;
                return true;
            }

            public bool TryConsumeCap(FactionAffinity faction, string capCategory, int weight)
            {
                string keyCat = string.IsNullOrEmpty(capCategory) ? "Ground" : capCategory;
                var key = (faction, keyCat);
                usedCaps.TryGetValue(key, out int current);
                usedCaps[key] = current + Math.Max(0, weight);
                return true;
            }

            public void ReleaseCap(FactionAffinity faction, string capCategory, int weight)
            {
                string keyCat = string.IsNullOrEmpty(capCategory) ? "Ground" : capCategory;
                var key = (faction, keyCat);
                if (usedCaps.TryGetValue(key, out int current))
                {
                    int next = Math.Max(0, current - Math.Max(0, weight));
                    if (next == 0)
                    {
                        usedCaps.Remove(key);
                    }
                    else
                    {
                        usedCaps[key] = next;
                    }
                }
            }

            internal void ResetSession()
            {
                usedCaps.Clear();
            }
        }

        internal sealed class VanillaLogisticsBridge : IRtsLogisticsBridge
        {
            public event Action<string, FactionAffinity, Vector3, float>? OnPhysicalKitDelivered;

            public bool RequestEngineerBuildSite(FactionAffinity faction, ModularUnitSpec spec, Vector3 worldPos, float yawDeg)
            {
                if (spec == null)
                {
                    return false;
                }

                MdsLog.Info($"[VanillaFallback] Immediate engineer placement for '{spec.UnitId}' ({faction}).");
                OnPhysicalKitDelivered?.Invoke(spec.UnitId, faction, worldPos, yawDeg);
                return true;
            }

            public bool RequestTarantulaSlingKit(FactionAffinity faction, ModularUnitSpec spec, Vector3 dropPos, float yawDeg)
            {
                if (spec == null)
                {
                    return false;
                }

                MdsLog.Info($"[VanillaFallback] Immediate sling-kit drop for '{spec.UnitId}' ({faction}).");
                OnPhysicalKitDelivered?.Invoke(spec.UnitId, faction, dropPos, yawDeg);
                return true;
            }
        }

        internal sealed class VanillaAbmStockpileBridge : IRtsAbmStockpileBridge
        {
            private readonly Dictionary<FactionAffinity, int> stockByFaction = new Dictionary<FactionAffinity, int>();
            private float regenTimer;

            public event Action<FactionAffinity, Vector3, float>? OnBallisticThreatDetected;

            public int GetAvailableAbmInterceptors(FactionAffinity faction)
            {
                EnsureFactionInitialized(faction);
                return stockByFaction[faction];
            }

            public bool TryReserveAbmInterceptor(FactionAffinity faction, string missileId)
            {
                EnsureFactionInitialized(faction);
                int current = stockByFaction[faction];
                if (current <= 0)
                {
                    return false;
                }

                stockByFaction[faction] = current - 1;
                return true;
            }

            internal void RefundAbmInterceptor(FactionAffinity faction)
            {
                EnsureFactionInitialized(faction);
                int maxCap = Math.Max(4, MdsConfig.VanillaStartingAbmStockpile.Value);
                if (stockByFaction[faction] < maxCap)
                {
                    stockByFaction[faction] = stockByFaction[faction] + 1;
                }
            }

            internal void RaiseBallisticThreatDetected(FactionAffinity targetFaction, Vector3 originPos, float blastYield)
            {
                OnBallisticThreatDetected?.Invoke(targetFaction, originPos, blastYield);
            }

            internal void Tick(float deltaTime)
            {
                regenTimer += deltaTime;
                if (regenTimer < 90f)
                {
                    return;
                }

                regenTimer = 0f;
                int maxCap = Math.Max(4, MdsConfig.VanillaStartingAbmStockpile.Value);
                var keys = new List<FactionAffinity>(stockByFaction.Keys);
                for (int i = 0; i < keys.Count; i++)
                {
                    FactionAffinity f = keys[i];
                    if (stockByFaction[f] < maxCap)
                    {
                        stockByFaction[f] = stockByFaction[f] + 1;
                    }
                }
            }

            internal void ResetSession()
            {
                stockByFaction.Clear();
                regenTimer = 0f;
            }

            private void EnsureFactionInitialized(FactionAffinity faction)
            {
                if (!stockByFaction.ContainsKey(faction))
                {
                    stockByFaction[faction] = Math.Max(1, MdsConfig.VanillaStartingAbmStockpile.Value);
                }
            }
        }

        internal sealed class VanillaAiThreatSink : IRtsAiThreatSink
        {
            internal readonly Dictionary<string, (FactionAffinity Faction, WeaponCascadeRole Role, Vector3 WorldPos, bool RadarActive)> ActiveSites =
                new Dictionary<string, (FactionAffinity, WeaponCascadeRole, Vector3, bool)>(StringComparer.OrdinalIgnoreCase);

            public void ReportDeployedModularSite(string siteId, FactionAffinity faction, WeaponCascadeRole role, Vector3 worldPos, bool radarActive)
            {
                if (!string.IsNullOrEmpty(siteId))
                {
                    ActiveSites[siteId] = (faction, role, worldPos, radarActive);
                }
            }

            public void ReportSiteDestroyed(string siteId)
            {
                if (!string.IsNullOrEmpty(siteId))
                {
                    ActiveSites.Remove(siteId);
                }
            }

            internal void ResetSession()
            {
                ActiveSites.Clear();
            }
        }
    }
}
