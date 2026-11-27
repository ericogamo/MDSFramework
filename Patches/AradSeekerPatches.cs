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

            SensorNetworkManager.Instance?.NotifyAradThreat(__result, missile);
        }
    }
}
