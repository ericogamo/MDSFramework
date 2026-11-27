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

    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.AddECMIntensity), new[] { typeof(float) })]
    internal static class AircraftAddEcmIntensityPatch
    {
        private static void Postfix(Aircraft __instance, float ecmIntensity)
        {
            if (!GameInteropHelpers.IsServerAuthority() ||
                !MdsConfig.EnableEccmNetwork.Value ||
                __instance == null ||
                __instance.disabled)
            {
                return;
            }

            if (ecmIntensity > 0.05f || (__instance.GetECMIntensity() > 0.05f && ecmIntensity >= 0f))
            {
                SensorNetworkManager.Instance?.RecordPassiveEcmPulse(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Radar), "Radar_OnJam")]
    internal static class RadarOnJamPatch
    {
        private static bool Prefix(Radar __instance)
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
                return site.IsRadarEmitting;
            }

            MdsClientSiteProxy? proxy = owner.GetComponent<MdsClientSiteProxy>();
            if (proxy != null && proxy.Spec.SensorProfile != null)
            {
                return proxy.IsRadarEmitting;
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
            if (site == null || !site.IsRadarEmitting || site.Spec.SensorProfile == null)
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

            float rcs = Mathf.Max(0.005f, targetUnit.RCS);
            if (rcs < 0.02f)
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

            float baseEnvelope = Mathf.Max(20000f, site.Spec.SensorProfile.DetectionRangeKm * 1000f);
            float rcsScale = Mathf.Clamp(Mathf.Pow(rcs / 1.0f, 0.25f), 0.25f, 1.0f);
            float clutterPenalty = 1f - Mathf.Clamp01(clutterFactor * 0.20f);
            float vhfEnvelope = baseEnvelope * rcsScale * clutterPenalty;
            if (targetUnit is Missile && rcs < 0.25f)
            {
                vhfEnvelope = Mathf.Min(vhfEnvelope, 12000f);
            }

            if (dist <= vhfEnvelope && !__instance.IsJammed() && clutterFactor < 1.35f)
            {
                __result = true;
            }
        }
    }
}
