using System.Diagnostics.CodeAnalysis;
using BepInEx;
using HarmonyLib;

namespace MDSFramework
{
    [BepInPlugin(MdsPluginInfo.Guid, MdsPluginInfo.Name, MdsPluginInfo.Version)]
    [SuppressMessage("Usage", "CA2243:Attribute string literals should parse correctly", Justification = "BepInEx plugin GUID uses reverse-DNS format.")]
    public sealed class MdsPlugin : BaseUnityPlugin
    {
        private Harmony? harmony;

        internal static MdsPlugin? Instance { get; private set; }
        internal MdsRuntimeController? RuntimeController { get; private set; }

        private void Awake()
        {
            Instance = this;
            MdsLog.Initialize(Logger);
            MdsConfig.Initialize(Config);

            if (!MdsConfig.ModEnabled.Value)
            {
                MdsLog.Info($"{MdsPluginInfo.Name} {MdsPluginInfo.Version} is disabled in config.");
                return;
            }

            harmony = new Harmony(MdsPluginInfo.Guid);
            harmony.PatchAll();

            RuntimeController = gameObject.AddComponent<MdsRuntimeController>();
            MdsLog.Info($"{MdsPluginInfo.Name} {MdsPluginInfo.Version} initialized (standalone-capable, zero RTS dependency).");
        }

        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
            harmony = null;
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }
        }
    }
}
