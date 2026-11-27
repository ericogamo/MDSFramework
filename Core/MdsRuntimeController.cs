using System;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Deployment;
using MDSFramework.Missiles;
using MDSFramework.Networking;
using MDSFramework.Patches;
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
        private int lastAirdropCheckPackageCount = -1;

        private void Awake()
        {
            Instance = this;
            sensorNetwork = new SensorNetworkManager();
            abmWarning = new AbmEarlyWarningService();
            wtaEngine = new SmartEngagementEngine();
            siteManager = new ModularSiteManager();

            MdsNetworkSync.Initialize();
            MdsCatalogCoordinator.Initialize();
            MdsEncyclopediaCatalog.Initialize();
            AirdropCatalogManager.Initialize();
            AircraftMissileCatalogManager.EnsureInitializedSubscription();
            MdsCatalogCoordinator.EnsureAllRegisteredCanonical();

            SceneManager.activeSceneChanged += OnActiveSceneChanged;
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            int packageCount = ContractorRegistry.GetPackages().Count;
            if (Encyclopedia.i != null &&
                packageCount > 0 &&
                Time.unscaledTime >= nextAirdropRetryTime)
            {
                bool needsUnitRegistration = MdsEncyclopediaCatalog.RegisteredUnitCount == 0;
                bool needsAirdropRegistration = !AirdropCatalogManager.NoTemplateAvailable &&
                    ((AirdropCatalogManager.RegisteredMountCount == 0 && lastAirdropCheckPackageCount != packageCount) ||
                     (AirdropCatalogManager.RegisteredMountCount > 0 && AirdropCatalogManager.InjectedAircraftCount == 0));
                if (needsUnitRegistration || needsAirdropRegistration)
                {
                    nextAirdropRetryTime = Time.unscaledTime + 1.0f;
                    MdsCatalogCoordinator.EnsureAllRegisteredCanonical();
                    lastAirdropCheckPackageCount = packageCount;
                }
            }

            MdsNetworkSync.Tick();

            if (!GameInteropHelpers.IsGameplayActive())
            {
                return;
            }

            if (!GameInteropHelpers.IsServerAuthority())
            {
                AheadAirburstManager.Tick(dt);
                return;
            }

            VanillaBridgeFallbacks.Tick(dt);
            sensorNetwork.Tick(dt);
            abmWarning.Tick();
            wtaEngine.Tick(Time.time);
            AheadAirburstManager.Tick(dt);
        }

        private void FixedUpdate()
        {
            if (!GameInteropHelpers.IsGameplayActive())
            {
                return;
            }

            AheadAirburstManager.FixedTick(Time.fixedDeltaTime);
        }

        private void OnGUI()
        {
            if (GameInteropHelpers.IsGameplayActive())
            {
                if (!string.IsNullOrEmpty(MdsNetworkSync.CatalogMismatchWarning))
                {
                    Color prevColor = GUI.color;
                    GUI.color = new Color(1f, 0.35f, 0.35f, 1f);
                    GUI.Box(new Rect(14f, 40f, 820f, 26f), MdsNetworkSync.CatalogMismatchWarning);
                    GUI.color = prevColor;
                }

                if (MdsConfig.EnableDebugHudOverlay.Value)
                {
                    DrawDebugHudStrip();
                }
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

        internal void TriggerNetworkSessionReset()
        {
            IsResettingScene = true;
            try
            {
                ResetSessionStateCore(clearHeavyCaches: false, preserveActiveServerConnection: false);
                MdsLog.Debug("MDS session reset on network disconnect/stop.");
            }
            finally
            {
                IsResettingScene = false;
            }
        }

        private void OnActiveSceneChanged(Scene prev, Scene next)
        {
            IsResettingScene = true;
            try
            {
                bool isMenuScene =
                    next.name.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(next.name, "empty", StringComparison.OrdinalIgnoreCase) ||
                    next.name.IndexOf("Waiting", StringComparison.OrdinalIgnoreCase) >= 0;
                bool preserveServer = !isMenuScene && GameInteropHelpers.IsServerAuthority();
                ResetSessionStateCore(clearHeavyCaches: isMenuScene, preserveActiveServerConnection: preserveServer);
                MdsLog.Debug($"MDS session reset on scene change -> {next.name}");
            }
            finally
            {
                IsResettingScene = false;
            }
        }

        private void ResetSessionStateCore(bool clearHeavyCaches, bool preserveActiveServerConnection)
        {
            wtaEngine.ResetSession();
            abmWarning.ResetSession();
            sensorNetwork.ResetSession();
            siteManager.ResetSession();
            CustomMissileTracker.ResetSession();
            MdsClientSiteProxy.ResetSession();
            AheadAirburstManager.ResetSession();
            MdsDestructionManager.ResetSession();
            VanillaBridgeFallbacks.ResetSession();
            MdsNetworkSync.ResetSession(preserveActiveServerConnection);
            PendingMdsSpawnScope.Clear();
            AirMissileLaunchTracker.Clear();
            CustomMissileWarningAudioLookup.ResetSession();

            if (clearHeavyCaches)
            {
                GlbRuntimeLoader.ClearCache();
                WavAudioClipLoader.ClearCache();
                UrpGlbMaterialBuilder.ClearExternalTextureCache();
            }
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            MdsCatalogCoordinator.Dispose();
            MdsEncyclopediaCatalog.Dispose();
            AirdropCatalogManager.Dispose();
            AircraftMissileCatalogManager.Dispose();
            abmWarning?.Dispose();
            siteManager?.Dispose();
            AheadAirburstManager.Dispose();
            MdsDestructionManager.Dispose();
            CustomMissileWarningAudioLookup.Dispose();
            GlbRuntimeLoader.ClearCache();
            WavAudioClipLoader.ClearCache();
            UrpGlbMaterialBuilder.ClearExternalTextureCache();

            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }
        }
    }
}
