using HarmonyLib;
using MDSFramework.Missiles;

namespace MDSFramework.Patches
{
    [HarmonyPatch(typeof(BallisticMissileGuidance), nameof(BallisticMissileGuidance.Initialize))]
    internal static class BallisticGuidanceInitPatch
    {
        private static void Postfix(BallisticMissileGuidance __instance)
        {
            if (!MdsConfig.EnableAbmEarlyWarning.Value || __instance == null)
            {
                return;
            }

            Missile? missile = GameInteropHelpers.SeekerMissileField?.GetValue(__instance) as Missile;
            if (missile != null)
            {
                AbmEarlyWarningService.Instance?.NotifyPotentialBallisticLaunch(missile, hasBallisticGuidance: true);
            }
        }
    }

    [HarmonyPatch(typeof(Missile), "StartMissile")]
    internal static class MissileStartEarlyWarningPatch
    {
        private static void Postfix(Missile __instance)
        {
            if (__instance == null)
            {
                return;
            }

            if (MdsConfig.EnableAbmEarlyWarning.Value)
            {
                float yield = GameInteropHelpers.SafeGetBlastYield(__instance);
                if (yield > MdsConfig.AbmBlastYieldThreshold.Value)
                {
                    AbmEarlyWarningService.Instance?.NotifyPotentialBallisticLaunch(__instance, hasBallisticGuidance: false);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.DeployFins))]
    internal static class MissileDeployFinsPatch
    {
        private static void Postfix(Missile __instance)
        {
            if (__instance != null && CustomMissileTracker.TryGet(__instance, out CustomMissileBehavior custom))
            {
                custom.TriggerFinDeployment();
            }
        }
    }
}
