using HarmonyLib;
using MDSFramework.Api;
using MDSFramework.Missiles;
using UnityEngine;

namespace MDSFramework.Patches
{
    [HarmonyPatch(typeof(Missile), nameof(Missile.GetECMIntensity))]
    internal static class MissileGetEcmIntensityPatch
    {
        private static void Postfix(Missile __instance, ref float __result)
        {
            if (__instance == null)
            {
                return;
            }

            if (CustomMissileTracker.TryGet(__instance, out CustomMissileBehavior beh) &&
                beh.Spec.HasActiveJammer &&
                beh.Spec.ActiveJammerIntensity > 0f)
            {
                __result = Mathf.Max(__result, beh.Spec.ActiveJammerIntensity);
            }
        }
    }

    [HarmonyPatch(
        typeof(Missile),
        nameof(Missile.GetRadarReturn),
        new[]
        {
            typeof(Vector3),
            typeof(Radar),
            typeof(Unit),
            typeof(float),
            typeof(float),
            typeof(RadarParams),
            typeof(bool)
        })]
    internal static class MissileGetRadarReturnPatch
    {
        private static void Prefix(Missile __instance, out float __state)
        {
            __state = -1f;
            if (__instance == null)
            {
                return;
            }

            if (CustomMissileTracker.TryGet(__instance, out CustomMissileBehavior beh) &&
                beh.Spec.StealthRcsOverride > 0f)
            {
                __state = __instance.RCS;
                __instance.RCS = beh.Spec.StealthRcsOverride;
            }
        }

        private static void Postfix(Missile __instance, float __state)
        {
            if (__instance != null && __state >= 0f)
            {
                __instance.RCS = __state;
            }
        }
    }

    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), nameof(OpticalSeekerCruiseMissile.Seek))]
    internal static class OpticalSeekerCruiseMissileSeaWraithBypassPatch
    {
        private static bool Prefix(OpticalSeekerCruiseMissile __instance)
        {
            if (__instance == null)
            {
                return true;
            }

            Missile? missile = GameInteropHelpers.SeekerMissileField?.GetValue(__instance) as Missile;
            if (missile != null &&
                CustomMissileTracker.TryGet(missile, out CustomMissileBehavior beh) &&
                beh.Spec.FlightProfile == MissileFlightProfile.SeaSkimmingEvasive)
            {
                beh.RunCruiseSeekerHousekeeping();
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), nameof(ARHSeeker.Seek))]
    internal static class ArhSeekerSeaWraithBypassPatch
    {
        private static bool Prefix(ARHSeeker __instance)
        {
            if (__instance == null)
            {
                return true;
            }

            Missile? missile = GameInteropHelpers.SeekerMissileField?.GetValue(__instance) as Missile;
            if (missile != null &&
                CustomMissileTracker.TryGet(missile, out CustomMissileBehavior beh) &&
                beh.Spec.FlightProfile == MissileFlightProfile.SeaSkimmingEvasive)
            {
                beh.RunCruiseSeekerHousekeeping();
                return false;
            }

            return true;
        }
    }
}
