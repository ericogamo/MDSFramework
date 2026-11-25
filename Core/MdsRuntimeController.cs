using System;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Deployment;
using MDSFramework.Missiles;
using MDSFramework.Sensors;
using MDSFramework.Wta;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MDSFramework
{
    internal sealed class MdsRuntimeController : MonoBehaviour
    {
        internal static MdsRuntimeController? Instance { get; private set; }
        internal static bool IsResettingScene { get; private set; }

        private SensorNetworkManager sensorNetwork = null!;
        private AbmEarlyWarningService abmWarning = null!;
        private SmartEngagementEngine wtaEngine = null!;
        private ModularSiteManager siteManager = null!;
        private float nextAirdropRetryTime;

        private void Awake()
        {
            Instance = this;
            sensorNetwork = new SensorNetworkManager();
            abmWarning = new AbmEarlyWarningService();
            wtaEngine = new SmartEngagementEngine();
            siteManager = new ModularSiteManager();

            MdsEncyclopediaCatalog.Initialize();
            MdsEncyclopediaCatalog.EnsureRegisteredAll();
            AirdropCatalogManager.Initialize();
            AirdropCatalogManager.EnsureRegisteredAll();

            SceneManager.sceneUnloaded += OnSceneUnloaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            if (MdsConfig.EnableMissionEditorCatalog.Value &&
                MdsEncyclopediaCatalog.RegisteredUnitCount == 0 &&
                Encyclopedia.i != null &&
                ContractorRegistry.GetPackages().Count > 0)
            {
                MdsEncyclopediaCatalog.EnsureRegisteredAll();
            }

            if (MdsConfig.EnableNativeAircraftAirdrop.Value &&
                (AirdropCatalogManager.RegisteredMountCount == 0 || AirdropCatalogManager.InjectedAircraftCount == 0) &&
                !AirdropCatalogManager.NoTemplateAvailable &&
                Encyclopedia.i != null &&
                Time.unscaledTime >= nextAirdropRetryTime)
            {
                nextAirdropRetryTime = Time.unscaledTime + 1.0f;
                AirdropCatalogManager.EnsureRegisteredAll();
                AircraftMissileCatalogManager.EnsureRegisteredAll();
            }

            if (!GameInteropHelpers.IsGameplayActive() || !GameInteropHelpers.IsServerAuthority())
            {
                return;
            }

            VanillaBridgeFallbacks.Tick(dt);
            sensorNetwork.Tick(dt);
            abmWarning.Tick();
            wtaEngine.Tick(Time.time);
            AheadAirburstManager.Tick(dt);
        }

        private void OnGUI()
        {
            if (MdsConfig.EnableDebugHudOverlay.Value && GameInteropHelpers.IsGameplayActive())
            {
                DrawDebugHudStrip();
            }
        }

        private static void DrawDebugHudStrip()
        {
            int sites = SensorNetworkManager.Instance != null ? SensorNetworkManager.Instance.AllSites.Count : 0;
            int arad = SensorNetworkManager.Instance != null ? SensorNetworkManager.Instance.ActiveAradAlerts : 0;
            int hoj = SensorNetworkManager.Instance != null ? SensorNetworkManager.Instance.ActiveHojTracks.Count : 0;
            int abmWarn = AbmEarlyWarningService.Instance != null ? AbmEarlyWarningService.Instance.ActiveWarningCount : 0;
            int threats = SmartEngagementEngine.Instance != null ? SmartEngagementEngine.Instance.LastThreatCount : 0;
            bool active = SmartEngagementEngine.Instance != null && SmartEngagementEngine.Instance.AnyFactionActive;

            string bridgeTag = RtsBridgeHost.IsRtsBridgeAttached ? "RTS-BRIDGE" : "VANILLA";
            string text =
                $"MDS [{bridgeTag}] | Sites: {sites} | WTA: {(active ? "4Hz ACTIVE" : "1Hz IDLE")} | " +
                $"Threats: {threats} | HOJ: {hoj} | ARAD EMCON: {arad} | ABM WARN: {abmWarn}";

            GUI.Box(new Rect(14f, 10f, 710f, 26f), text);
        }

        private void OnSceneUnloaded(Scene _)
        {
            IsResettingScene = true;
        }

        private void OnActiveSceneChanged(Scene prev, Scene next)
        {
            IsResettingScene = true;
            try
            {
                wtaEngine.ResetSession();
                abmWarning.ResetSession();
                sensorNetwork.ResetSession();
                siteManager.ResetSession();
                CustomMissileTracker.ResetSession();
                AheadAirburstManager.ResetSession();
                MdsDestructionManager.ResetSession();
                VanillaBridgeFallbacks.ResetSession();

                if (next.name.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    GlbRuntimeLoader.ClearCache();
                    WavAudioClipLoader.ClearCache();
                }

                MdsLog.Debug($"MDS session reset on scene change -> {next.name}");
            }
            finally
            {
                IsResettingScene = false;
            }
        }

        private void OnDestroy()
        {
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            MdsEncyclopediaCatalog.Dispose();
            AirdropCatalogManager.Dispose();
            abmWarning?.Dispose();
            siteManager?.Dispose();
            AheadAirburstManager.Dispose();
            MdsDestructionManager.Dispose();
            GlbRuntimeLoader.ClearCache();
            WavAudioClipLoader.ClearCache();

            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }
        }
    }
}
