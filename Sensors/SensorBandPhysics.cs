using MDSFramework.Api;
using UnityEngine;

namespace MDSFramework.Sensors
{
    internal static class SensorBandPhysics
    {
        internal static int BuildBandBitmask(SensorBand[]? bands)
        {
            if (bands == null)
            {
                return 0;
            }

            int mask = 0;
            for (int i = 0; i < bands.Length; i++)
            {
                mask |= 1 << (int)bands[i];
            }

            return mask;
        }

        internal static bool HasBand(int mask, SensorBand band)
        {
            return (mask & (1 << (int)band)) != 0;
        }

        internal static bool CanSiteSense(ModularSiteBehavior? site)
        {
            return site != null &&
                   site.IsAlive &&
                   !site.SuspendedInTransit &&
                   site.IsDeployed;
        }

        internal static float ComputeEffectiveDetectionRangeMeters(
            ModularSiteBehavior? site,
            float targetRcs,
            float targetSpeedMps,
            bool targetEmitsRadar,
            bool targetIsJamming,
            out SensorBand bestBand,
            out float trackQuality)
        {
            if (!CanSiteSense(site) || site!.Spec.SensorProfile == null)
            {
                bestBand = SensorBand.EoIr;
                trackQuality = 0f;
                return 0f;
            }

            return ComputeEffectiveDetectionRangeMeters(
                site.Spec.SensorProfile,
                site.IsRadarEmitting,
                targetRcs,
                targetSpeedMps,
                targetEmitsRadar,
                targetIsJamming,
                site.CurrentJamLevel,
                out bestBand,
                out trackQuality);
        }

        internal static float ComputeEffectiveDetectionRangeMeters(
            EccmSensorProfile profile,
            bool radarEmitting,
            float targetRcs,
            float targetSpeedMps,
            bool targetEmitsRadar,
            bool targetIsJamming,
            float localJamLevel,
            out SensorBand bestBand,
            out float trackQuality)
        {
            bestBand = SensorBand.EoIr;
            trackQuality = 0f;

            if (profile == null)
            {
                return 0f;
            }

            float baseRangeMeters = Mathf.Max(1000f, profile.DetectionRangeKm * 1000f);
            float bestRange = 0f;

            float netJamPenalty = Mathf.Clamp01(localJamLevel - (profile.JamTolerance * 0.75f));
            float radarJamFactor = Mathf.Lerp(1.0f, 0.22f, netJamPenalty);
            float dopplerBoost = targetSpeedMps > 60f ? 1.08f : 1.0f;

            SensorBand[] bands = profile.SupportedBands ?? System.Array.Empty<SensorBand>();
            for (int i = 0; i < bands.Length; i++)
            {
                SensorBand band = bands[i];
                float range = 0f;
                float quality = 0f;

                switch (band)
                {
                    case SensorBand.XBand:
                        if (radarEmitting)
                        {
                            float rcsScale = Mathf.Pow(Mathf.Max(0.02f, targetRcs), 0.25f);
                            range = baseRangeMeters * rcsScale * radarJamFactor * dopplerBoost;
                            quality = 1.0f * radarJamFactor;
                        }
                        break;

                    case SensorBand.SBand:
                        if (radarEmitting)
                        {
                            float rcsScale = Mathf.Pow(Mathf.Max(0.04f, targetRcs), 0.22f);
                            range = baseRangeMeters * 1.12f * rcsScale * radarJamFactor * dopplerBoost;
                            quality = 0.85f * radarJamFactor;
                        }
                        break;

                    case SensorBand.VhfLongWave:
                        if (radarEmitting)
                        {
                            // Long-wave resonance counters low-RCS stealth geometry for early cueing.
                            float antiStealthRcs = Mathf.Max(targetRcs, 0.42f);
                            float rcsScale = Mathf.Pow(antiStealthRcs, 0.20f);
                            range = baseRangeMeters * 1.20f * rcsScale * Mathf.Max(0.45f, radarJamFactor);
                            quality = 0.55f;
                        }
                        break;

                    case SensorBand.EoIr:
                        // Passive optical/IR ignores RF jamming completely inside emergency range.
                        range = Mathf.Max(0f, profile.EoIrEmergencyRangeKm * 1000f);
                        quality = 0.92f;
                        break;

                    case SensorBand.PassiveElint:
                        if (targetEmitsRadar || targetIsJamming)
                        {
                            range = baseRangeMeters * (targetIsJamming ? 1.45f : 1.25f);
                            quality = targetIsJamming ? 0.82f : 0.72f;
                        }
                        break;
                }

                if (range > bestRange)
                {
                    bestRange = range;
                    bestBand = band;
                    trackQuality = quality;
                }
            }

            // Guarantee EO/IR backup if configured even if not explicitly repeated in SupportedBands.
            if (profile.EoIrEmergencyRangeKm > 0.1f)
            {
                float eoirRange = profile.EoIrEmergencyRangeKm * 1000f;
                if (eoirRange > bestRange)
                {
                    bestRange = eoirRange;
                    bestBand = SensorBand.EoIr;
                    trackQuality = 0.88f;
                }
            }

            return bestRange;
        }
    }
}
