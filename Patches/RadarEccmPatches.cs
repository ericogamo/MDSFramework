using HarmonyLib;
using MDSFramework.Api;
using MDSFramework.Sensors;
using UnityEngine;

namespace MDSFramework.Patches
{
    [HarmonyPatch(typeof(Unit), nameof(Unit.Jam))]
    internal static class UnitJamPatch
    {
        private static void Postfix(Unit __instance, Unit.JamEventArgs args)
        {
            if (!GameInteropHelpers.IsServerAuthority() || !MdsConfig.EnableEccmNetwork.Value)
            {
                return;
            }

            if (__instance != null && args.jammingUnit != null)
            {
                SensorNetworkManager.Instance?.NotifyJammingEmission(__instance, args.jammingUnit, args.jamAmount);
            }
        }
    }

    [HarmonyPatch(typeof(Radar), "Radar_OnJam")]
    internal static class RadarOnJamPatch
    {
        private static bool Prefix(Radar __instance, ref Unit.JamEventArgs e)
        {
            if (!MdsConfig.EnableEccmNetwork.Value || __instance == null)
            {
                return true;
            }

            Unit? owner = __instance.GetAttachedUnit();
            if (owner == null)
            {
                return true;
            }

            ModularSiteBehavior? site = owner.GetComponent<ModularSiteBehavior>();
            if (site != null && site.Spec.SensorProfile != null)
            {
                // Active EMCON or stowed radar ignores jamming accumulation completely.
                if (!site.IsRadarEmitting)
                {
                    return false;
                }

                float tol = Mathf.Clamp(site.Spec.SensorProfile.JamTolerance + site.EffectiveNetworkEccmBonus, 0f, 0.92f);
                if (tol > 0.05f)
                {
                    e.jamAmount *= Mathf.Clamp(1f - (tol * 0.75f), 0.08f, 1f);
                }

                return true;
            }

            MdsClientSiteProxy? proxy = owner.GetComponent<MdsClientSiteProxy>();
            if (proxy != null && proxy.Spec.SensorProfile != null)
            {
                if (!proxy.IsRadarEmitting)
                {
                    return false;
                }

                float tol = Mathf.Clamp(proxy.Spec.SensorProfile.JamTolerance, 0f, 0.92f);
                if (tol > 0.05f)
                {
                    e.jamAmount *= Mathf.Clamp(1f - (tol * 0.75f), 0.08f, 1f);
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Radar), nameof(Radar.CanSeeRadarReturn))]
    internal static class RadarVhfAntiStealthPatch
    {
        private static void Postfix(Radar __instance, IRadarReturn radarReturn, float dist, float clutterFactor, ref bool __result)
        {
            if (__result || !MdsConfig.EnableEccmNetwork.Value || __instance == null || !__instance.activated)
            {
                return;
            }

            Unit? owner = __instance.GetAttachedUnit();
            if (owner == null)
            {
                return;
            }

            ModularSiteBehavior? site = owner.GetComponent<ModularSiteBehavior>();
            if (site == null || !site.IsRadarEmitting)
            {
                return;
            }

            // VHF Long-Wave radars provide anti-stealth early warning cueing against low-RCS targets within LOS & horizon.
            if (!SensorBandPhysics.HasBand(site.SupportedBandMask, SensorBand.VhfLongWave))
            {
                return;
            }

            if (radarReturn is not Unit targetUnit || targetUnit.disabled)
            {
                return;
            }

            Transform scannerTf = (GameInteropHelpers.TargetDetectorScannerField?.GetValue(__instance) as Transform)
                                  ?? __instance.transform;
            float h1 = Mathf.Max(1f, scannerTf.position.y - Datum.LocalSeaY);
            float h2 = Mathf.Max(1f, targetUnit.transform.position.y - Datum.LocalSeaY);
            float horizon = Mathf.Sqrt(12742000f * h1) + Mathf.Sqrt(12742000f * h2);
            if (dist > horizon || !TargetCalc.LineOfSight(scannerTf, targetUnit.transform, 10f))
            {
                return;
            }

            float vhfEnvelope = Mathf.Max(20000f, site.Spec.SensorProfile.DetectionRangeKm * 1000f);
            if (dist <= vhfEnvelope && !__instance.IsJammed() && clutterFactor < 1.8f)
            {
                __result = true;
            }
        }
    }
}
