using HarmonyLib;
using MDSFramework.Sensors;

namespace MDSFramework.Patches
{
    [HarmonyPatch(typeof(ARMSeeker), nameof(ARMSeeker.TrackCurrentTarget))]
    internal static class AradSeekerTrackPatch
    {
        private static void Postfix(ARMSeeker __instance, Radar? __result)
        {
            if (!GameInteropHelpers.IsServerAuthority() || !MdsConfig.EnableAradAutoEmcon.Value || __instance == null)
            {
                return;
            }

            Missile? missile = GameInteropHelpers.SeekerMissileField?.GetValue(__instance) as Missile;
            if (missile == null || missile.disabled)
            {
                return;
            }

            Radar? targeted = __result ?? (GameInteropHelpers.ArmTargetedRadarField?.GetValue(__instance) as Radar);
            if (targeted != null)
            {
                SensorNetworkManager.Instance?.NotifyAradThreat(targeted, missile);
            }
        }
    }
}
