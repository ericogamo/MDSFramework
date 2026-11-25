using System;
using UnityEngine;

namespace MDSFramework.Api
{
    public interface IRtsEconomyBridge
    {
        int QueryFactionTechTier(FactionAffinity faction);
        bool TrySpendFactionCredits(FactionAffinity faction, float amount, string reason);
        bool IsPackageAllowedForFaction(ContractorPackage package, FactionAffinity faction);
    }

    public interface IRtsCapBridge
    {
        bool CanConsumeCap(FactionAffinity faction, string capCategory, int weight, out string blockReason);
        bool TryConsumeCap(FactionAffinity faction, string capCategory, int weight);
        void ReleaseCap(FactionAffinity faction, string capCategory, int weight);
    }

    public interface IRtsLogisticsBridge
    {
        bool RequestEngineerBuildSite(FactionAffinity faction, ModularUnitSpec spec, Vector3 worldPos, float yawDeg);
        bool RequestTarantulaSlingKit(FactionAffinity faction, ModularUnitSpec spec, Vector3 dropPos, float yawDeg);
        event Action<string, FactionAffinity, Vector3, float> OnPhysicalKitDelivered;
    }

    public interface IRtsAbmStockpileBridge
    {
        int GetAvailableAbmInterceptors(FactionAffinity faction);
        bool TryReserveAbmInterceptor(FactionAffinity faction, string missileId);
        event Action<FactionAffinity, Vector3, float> OnBallisticThreatDetected;
    }

    public interface IRtsAiThreatSink
    {
        void ReportDeployedModularSite(string siteId, FactionAffinity faction, WeaponCascadeRole role, Vector3 worldPos, bool radarActive);
        void ReportSiteDestroyed(string siteId);
    }

    public interface IRtsBridgeHost
    {
        IRtsEconomyBridge Economy { get; set; }
        IRtsCapBridge Caps { get; set; }
        IRtsLogisticsBridge Logistics { get; set; }
        IRtsAbmStockpileBridge AbmStockpile { get; set; }
        IRtsAiThreatSink AiThreats { get; set; }
    }

    public static class RtsBridgeHost
    {
        private static readonly DefaultBridgeHost Host = new DefaultBridgeHost();

        public static bool IsRtsBridgeAttached { get; private set; }
        public static IRtsBridgeHost Current => Host;
        public static Func<Unit, bool>? QueryUnitHoldFire { get; set; }

        public static event Action? OnBridgeChanged;

        public static void Attach(
            IRtsEconomyBridge economy,
            IRtsCapBridge caps,
            IRtsLogisticsBridge logistics,
            IRtsAbmStockpileBridge abmStockpile,
            IRtsAiThreatSink aiThreats)
        {
            Host.Economy = economy ?? throw new ArgumentNullException(nameof(economy));
            Host.Caps = caps ?? throw new ArgumentNullException(nameof(caps));
            Host.Logistics = logistics ?? throw new ArgumentNullException(nameof(logistics));
            Host.AbmStockpile = abmStockpile ?? throw new ArgumentNullException(nameof(abmStockpile));
            Host.AiThreats = aiThreats ?? throw new ArgumentNullException(nameof(aiThreats));
            IsRtsBridgeAttached = true;

            MdsLog.Info("External RTS bridge attached to MDSFramework.");
            OnBridgeChanged?.Invoke();
        }

        public static void ResetToVanillaDefaults()
        {
            Host.ResetToVanilla();
            IsRtsBridgeAttached = false;
            QueryUnitHoldFire = null;
            OnBridgeChanged?.Invoke();
        }

        private sealed class DefaultBridgeHost : IRtsBridgeHost
        {
            internal DefaultBridgeHost()
            {
                ResetToVanilla();
            }

            public IRtsEconomyBridge Economy { get; set; } = null!;
            public IRtsCapBridge Caps { get; set; } = null!;
            public IRtsLogisticsBridge Logistics { get; set; } = null!;
            public IRtsAbmStockpileBridge AbmStockpile { get; set; } = null!;
            public IRtsAiThreatSink AiThreats { get; set; } = null!;

            internal void ResetToVanilla()
            {
                Economy = VanillaBridgeFallbacks.Economy;
                Caps = VanillaBridgeFallbacks.Caps;
                Logistics = VanillaBridgeFallbacks.Logistics;
                AbmStockpile = VanillaBridgeFallbacks.AbmStockpile;
                AiThreats = VanillaBridgeFallbacks.AiThreats;
            }
        }
    }
}
