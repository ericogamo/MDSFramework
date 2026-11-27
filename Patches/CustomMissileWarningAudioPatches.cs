using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Deployment;
using MDSFramework.Missiles;
using UnityEngine;
using UnityEngine.UI;

namespace MDSFramework.Patches
{
    internal static class CustomMissileWarningAudioLookup
    {
        internal static readonly FieldInfo? ThreatListItemLookupField =
            AccessTools.Field(typeof(ThreatList), "itemLookup");
        internal static readonly FieldInfo? ThreatListThreatItemPrefabField =
            AccessTools.Field(typeof(ThreatList), "threatItemPrefab");

        internal static readonly FieldInfo? MissileWarningUnknownMissilesField =
            AccessTools.Field(typeof(MissileWarning), "unknownMissiles");
        internal static readonly FieldInfo? MissileWarningDetectionRangeField =
            AccessTools.Field(typeof(MissileWarning), "detectionRange");
        internal static readonly FieldInfo? MissileWarningAircraftField =
            AccessTools.Field(typeof(MissileWarning), "aircraft");
        internal static readonly FieldInfo? MissileWarningOnMissileWarningEventField =
            AccessTools.Field(typeof(MissileWarning), "onMissileWarning");

        internal static readonly FieldInfo? ThreatItemMissileField =
            AccessTools.Field(typeof(ThreatItem), "missile");
        internal static readonly FieldInfo? ThreatItemTextField =
            AccessTools.Field(typeof(ThreatItem), "text");
        internal static readonly FieldInfo? ThreatItemNotchIndicatorLabelField =
            AccessTools.Field(typeof(ThreatItem), "notchIndicatorLabel");

        private static AudioClip? fallbackLoopClip;
        private static AudioClip? fallbackAlertClip;
        private static readonly Dictionary<MissileWarning, float> NextExtendedCheckAt = new Dictionary<MissileWarning, float>();

        internal static void ResetSession()
        {
            NextExtendedCheckAt.Clear();
        }

        internal static void Dispose()
        {
            ResetSession();
            if (fallbackLoopClip != null)
            {
                UnityEngine.Object.Destroy(fallbackLoopClip);
                fallbackLoopClip = null;
            }

            if (fallbackAlertClip != null)
            {
                UnityEngine.Object.Destroy(fallbackAlertClip);
                fallbackAlertClip = null;
            }
        }

        internal static bool ShouldRunExtendedRangeTick(MissileWarning warning)
        {
            float now = Time.unscaledTime;
            if (NextExtendedCheckAt.TryGetValue(warning, out float nextAt) && now < nextAt)
            {
                return false;
            }

            NextExtendedCheckAt[warning] = now + 0.20f;
            return true;
        }

        internal static bool TryResolveCustomWarningSpec(
            Missile? missile,
            out ContractorPackage package,
            out CustomMissileSpec spec)
        {
            if (missile == null)
            {
                package = null!;
                spec = null!;
                return false;
            }

            if (CustomMissileTracker.TryGet(missile, out CustomMissileBehavior beh) &&
                beh.Package != null &&
                beh.Spec != null)
            {
                package = beh.Package;
                spec = beh.Spec;
                return true;
            }

            if (missile.definition is MissileDefinition mDef &&
                MdsEncyclopediaCatalog.TryResolveMissileDefinition(mDef, out package, out spec))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(missile.unitName) &&
                MdsEncyclopediaCatalog.TryResolveMissileByDisplayName(missile.unitName, out package, out spec, out _))
            {
                return true;
            }

            package = null!;
            spec = null!;
            return false;
        }

        internal static bool HasCustomWarningAudio(CustomMissileSpec? spec)
        {
            if (spec == null)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(spec.WarningAlarmAudioRelativePath) ||
                   !string.IsNullOrWhiteSpace(spec.WarningAlertAudioRelativePath) ||
                   spec.RequiresTargetEcmToTrack;
        }

        internal static AudioClip GetOrCreateLoopClip(ContractorPackage package, CustomMissileSpec spec)
        {
            if (!string.IsNullOrWhiteSpace(spec.WarningAlarmAudioRelativePath))
            {
                string? path = CustomMissileBehavior.ResolveAssetPath(
                    package,
                    spec.WarningAlarmAudioRelativePath,
                    Array.Empty<string>());
                AudioClip? loaded = WavAudioClipLoader.LoadOrGet(path);
                if (loaded != null)
                {
                    return loaded;
                }
            }

            return GetOrCreateFallbackLoopClip();
        }

        internal static AudioClip GetOrCreateAlertClip(ContractorPackage package, CustomMissileSpec spec)
        {
            if (!string.IsNullOrWhiteSpace(spec.WarningAlertAudioRelativePath))
            {
                string? path = CustomMissileBehavior.ResolveAssetPath(
                    package,
                    spec.WarningAlertAudioRelativePath,
                    Array.Empty<string>());
                AudioClip? loaded = WavAudioClipLoader.LoadOrGet(path);
                if (loaded != null)
                {
                    return loaded;
                }
            }

            return GetOrCreateFallbackAlertClip();
        }

        private static AudioClip GetOrCreateFallbackLoopClip()
        {
            if (fallbackLoopClip != null)
            {
                return fallbackLoopClip;
            }

            const int sampleRate = 44100;
            const float duration = 0.80f;
            int totalSamples = Mathf.RoundToInt(sampleRate * duration);
            var samples = new float[totalSamples];

            double phaseHi = 0.0;
            double phaseLo = 0.0;
            for (int i = 0; i < totalSamples; i++)
            {
                float t = (float)i / sampleRate;
                float halfT = t % 0.40f;
                float fHi;
                float fLo;
                float env;
                if (halfT < 0.16f)
                {
                    float u = halfT / 0.16f;
                    fHi = 1480f + (480f * u);
                    fLo = 740f + (240f * u);
                    env = Mathf.Pow(Mathf.Sin(Mathf.PI * u), 0.35f);
                }
                else if (halfT >= 0.19f && halfT < 0.35f)
                {
                    float u = (halfT - 0.19f) / 0.16f;
                    fHi = 980f - (240f * u);
                    fLo = 1960f - (420f * u);
                    env = Mathf.Pow(Mathf.Sin(Mathf.PI * u), 0.35f);
                }
                else
                {
                    fHi = 1480f;
                    fLo = 740f;
                    env = 0f;
                }

                phaseHi += (2.0 * Math.PI * fHi) / sampleRate;
                phaseLo += (2.0 * Math.PI * fLo) / sampleRate;
                float am = 0.78f + (0.22f * Mathf.Sin(2f * Mathf.PI * 45f * t));
                samples[i] = env * am * 0.55f * (
                    (0.65f * (float)Math.Sin(phaseHi)) +
                    (0.35f * (float)Math.Sin(phaseLo)));
            }

            fallbackLoopClip = AudioClip.Create("mds_eccm_rwr_loop_fallback", totalSamples, 1, sampleRate, stream: false);
            fallbackLoopClip.SetData(samples, 0);
            return fallbackLoopClip;
        }

        private static AudioClip GetOrCreateFallbackAlertClip()
        {
            if (fallbackAlertClip != null)
            {
                return fallbackAlertClip;
            }

            const int sampleRate = 44100;
            const float duration = 0.42f;
            int totalSamples = Mathf.RoundToInt(sampleRate * duration);
            var samples = new float[totalSamples];

            double phase = 0.0;
            for (int i = 0; i < totalSamples; i++)
            {
                float t = (float)i / sampleRate;
                float freq;
                float env;
                if (t < 0.12f)
                {
                    float u = t / 0.12f;
                    freq = 1650f;
                    env = Mathf.Pow(Mathf.Sin(Mathf.PI * u), 0.30f);
                }
                else if (t >= 0.14f && t < 0.26f)
                {
                    float u = (t - 0.14f) / 0.12f;
                    freq = 1150f;
                    env = Mathf.Pow(Mathf.Sin(Mathf.PI * u), 0.30f);
                }
                else if (t >= 0.28f && t < 0.41f)
                {
                    float u = (t - 0.28f) / 0.13f;
                    freq = 2200f;
                    env = Mathf.Pow(Mathf.Sin(Mathf.PI * u), 0.35f);
                }
                else
                {
                    freq = 1150f;
                    env = 0f;
                }

                phase += (2.0 * Math.PI * freq) / sampleRate;
                samples[i] = env * 0.65f * (float)Math.Sin(phase);
            }

            fallbackAlertClip = AudioClip.Create("mds_eccm_rwr_alert_fallback", totalSamples, 1, sampleRate, stream: false);
            fallbackAlertClip.SetData(samples, 0);
            return fallbackAlertClip;
        }
    }

    internal sealed class CustomThreatAlarmController : MonoBehaviour
    {
        private sealed class CustomAlarmChannel
        {
            internal readonly AudioSource Source;
            internal readonly HashSet<PersistentID> ActiveIds = new HashSet<PersistentID>();

            internal CustomAlarmChannel(AudioSource source)
            {
                Source = source;
            }
        }

        private readonly Dictionary<string, CustomAlarmChannel> channelsByMissileId =
            new Dictionary<string, CustomAlarmChannel>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<PersistentID, string> missileIdToChannelKey = new Dictionary<PersistentID, string>();
        private readonly Dictionary<PersistentID, float> staleSinceByMissileId = new Dictionary<PersistentID, float>();
        private readonly List<PersistentID> pruneScratch = new List<PersistentID>(8);

        private Aircraft? aircraft;
        private float nextPruneTime;

        internal void SetAircraft(Aircraft? ac)
        {
            Cleanup();
            aircraft = ac;
        }

        internal bool IsTracking(PersistentID persistentId)
        {
            return missileIdToChannelKey.ContainsKey(persistentId);
        }

        internal void AddMissile(Missile missile, ContractorPackage package, CustomMissileSpec spec)
        {
            if (missile == null || aircraft == null || aircraft.disabled)
            {
                return;
            }

            PersistentID id = missile.persistentID;
            string channelKey = spec.MissileId;
            if (!channelsByMissileId.TryGetValue(channelKey, out CustomAlarmChannel? channel))
            {
                AudioSource src = aircraft.gameObject.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = true;
                src.clip = CustomMissileWarningAudioLookup.GetOrCreateLoopClip(package, spec);
                src.volume = Mathf.Clamp01(spec.WarningAlarmVolume);
                src.spatialBlend = 0f;
                src.dopplerLevel = 0f;
                src.pitch = 1f;
                if (SoundManager.i != null && SoundManager.i.MissileAlertMixer != null)
                {
                    src.outputAudioMixerGroup = SoundManager.i.MissileAlertMixer;
                }

                channel = new CustomAlarmChannel(src);
                channelsByMissileId[channelKey] = channel;
            }

            if (missileIdToChannelKey.TryGetValue(id, out string? prevKey) &&
                !string.Equals(prevKey, channelKey, StringComparison.OrdinalIgnoreCase))
            {
                RemoveMissile(id);
            }

            channel.ActiveIds.Add(id);
            missileIdToChannelKey[id] = channelKey;
            staleSinceByMissileId.Remove(id);

            if (channel.Source != null)
            {
                channel.Source.volume = Mathf.Clamp01(spec.WarningAlarmVolume);
                if (!channel.Source.isPlaying && channel.Source.clip != null)
                {
                    channel.Source.time = 0f;
                    channel.Source.Play();
                }
            }

            AudioClip alertClip = CustomMissileWarningAudioLookup.GetOrCreateAlertClip(package, spec);
            if (SoundManager.i != null && alertClip != null)
            {
                SoundManager.PlayInterfaceOneShot(alertClip);
            }
        }

        internal void RemoveMissile(PersistentID persistentId)
        {
            if (!missileIdToChannelKey.TryGetValue(persistentId, out string? channelKey))
            {
                return;
            }

            missileIdToChannelKey.Remove(persistentId);
            staleSinceByMissileId.Remove(persistentId);
            if (channelsByMissileId.TryGetValue(channelKey, out CustomAlarmChannel? channel))
            {
                channel.ActiveIds.Remove(persistentId);
                if (channel.ActiveIds.Count == 0 && channel.Source != null && channel.Source.isPlaying)
                {
                    channel.Source.Stop();
                }
            }
        }

        internal void Cleanup()
        {
            foreach (CustomAlarmChannel channel in channelsByMissileId.Values)
            {
                if (channel.Source != null)
                {
                    channel.Source.Stop();
                    UnityEngine.Object.Destroy(channel.Source);
                }
            }

            channelsByMissileId.Clear();
            missileIdToChannelKey.Clear();
            staleSinceByMissileId.Clear();
            aircraft = null;
        }

        private void Update()
        {
            if (missileIdToChannelKey.Count == 0)
            {
                return;
            }

            if (aircraft == null || aircraft.disabled)
            {
                Cleanup();
                return;
            }

            float now = Time.unscaledTime;
            if (now < nextPruneTime)
            {
                return;
            }

            nextPruneTime = now + 0.5f;
            pruneScratch.Clear();
            foreach (PersistentID id in missileIdToChannelKey.Keys)
            {
                bool stale = !UnitRegistry.TryGetUnit(id, out Unit unit) ||
                             unit is not Missile m ||
                             m.disabled ||
                             m.targetID != aircraft.persistentID;
                if (!stale)
                {
                    staleSinceByMissileId.Remove(id);
                    continue;
                }

                if (!staleSinceByMissileId.TryGetValue(id, out float staleAt))
                {
                    staleSinceByMissileId[id] = now;
                }
                else if (now - staleAt >= 1.5f)
                {
                    pruneScratch.Add(id);
                }
            }

            for (int i = 0; i < pruneScratch.Count; i++)
            {
                RemoveMissile(pruneScratch[i]);
            }
        }

        private void OnDestroy()
        {
            Cleanup();
        }
    }

    [HarmonyPatch(typeof(ThreatList), nameof(ThreatList.SetAircraft))]
    internal static class ThreatListSetAircraftCustomAudioPatch
    {
        private static void Postfix(ThreatList __instance, Aircraft aircraft)
        {
            if (__instance == null)
            {
                return;
            }

            CustomThreatAlarmController ctrl = __instance.GetComponent<CustomThreatAlarmController>()
                ?? __instance.gameObject.AddComponent<CustomThreatAlarmController>();
            ctrl.SetAircraft(aircraft);
        }
    }

    [HarmonyPatch(typeof(ThreatList), "ThreatList_OnAircraftDisable")]
    internal static class ThreatListOnAircraftDisableCustomAudioPatch
    {
        private static void Postfix(ThreatList __instance)
        {
            if (__instance == null)
            {
                return;
            }

            __instance.GetComponent<CustomThreatAlarmController>()?.Cleanup();
        }
    }

    [HarmonyPatch(typeof(ThreatList), "ThreatList_OnMissileWarning")]
    internal static class ThreatListOnMissileWarningCustomAudioPatch
    {
        private static bool Prefix(ThreatList __instance, MissileWarning.OnMissileWarning e)
        {
            if (__instance == null || e.missile == null)
            {
                return true;
            }

            if (!CustomMissileWarningAudioLookup.TryResolveCustomWarningSpec(
                    e.missile,
                    out ContractorPackage package,
                    out CustomMissileSpec spec) ||
                !CustomMissileWarningAudioLookup.HasCustomWarningAudio(spec))
            {
                return true;
            }

            CustomThreatAlarmController? ctrl = __instance.GetComponent<CustomThreatAlarmController>();
            if (ctrl == null)
            {
                return true;
            }

            if (CustomMissileWarningAudioLookup.ThreatListItemLookupField?.GetValue(__instance) is not Dictionary<PersistentID, ThreatItem> itemLookup)
            {
                return true;
            }

            if (!itemLookup.ContainsKey(e.missile.persistentID))
            {
                if (CustomMissileWarningAudioLookup.ThreatListThreatItemPrefabField?.GetValue(__instance) is not GameObject prefab || prefab == null)
                {
                    return true;
                }

                GameObject itemGo = UnityEngine.Object.Instantiate(prefab, __instance.transform);
                ThreatItem? component = itemGo != null ? itemGo.GetComponent<ThreatItem>() : null;
                if (component == null)
                {
                    if (itemGo != null)
                    {
                        UnityEngine.Object.Destroy(itemGo);
                    }

                    return true;
                }

                component.SetItem(e.missile.persistentID);
                ctrl.AddMissile(e.missile, package, spec);
                itemLookup.Add(e.missile.persistentID, component);
            }
            else
            {
                ctrl.AddMissile(e.missile, package, spec);
            }

            __instance.enabled = true;
            if (SceneSingleton<CombatHUD>.i != null)
            {
                SceneSingleton<CombatHUD>.i.FlashMarker(e.missile, flash: true);
            }

            if (SceneSingleton<DynamicMap>.i != null)
            {
                SceneSingleton<DynamicMap>.i.FlagIncomingMissile(e.missile);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(ThreatList), "ThreatList_OffMissileWarning")]
    internal static class ThreatListOffMissileWarningCustomAudioPatch
    {
        private static bool Prefix(ThreatList __instance, MissileWarning.OffMissileWarning e)
        {
            if (__instance == null || e.missile == null)
            {
                return true;
            }

            CustomThreatAlarmController? ctrl = __instance.GetComponent<CustomThreatAlarmController>();
            if (ctrl == null || !ctrl.IsTracking(e.missile.persistentID))
            {
                return true;
            }

            if (SceneSingleton<CombatHUD>.i != null)
            {
                SceneSingleton<CombatHUD>.i.FlashMarker(e.missile, flash: false);
            }

            if (SceneSingleton<DynamicMap>.i != null)
            {
                SceneSingleton<DynamicMap>.i.ClearIncomingMissile(e.missile);
            }

            if (CustomMissileWarningAudioLookup.ThreatListItemLookupField?.GetValue(__instance) is Dictionary<PersistentID, ThreatItem> itemLookup &&
                itemLookup.TryGetValue(e.missile.persistentID, out ThreatItem? item))
            {
                if (item != null)
                {
                    UnityEngine.Object.Destroy(item.gameObject);
                }

                itemLookup.Remove(e.missile.persistentID);
            }

            ctrl.RemoveMissile(e.missile.persistentID);
            return false;
        }
    }

    [HarmonyPatch(typeof(MissileWarning), nameof(MissileWarning.LockedByMissile))]
    internal static class MissileWarningDedupPatch
    {
        private static bool Prefix(MissileWarning __instance, Aircraft aircraft, Missile missile)
        {
            if (__instance == null || missile == null)
            {
                return true;
            }

            if (__instance.knownMissiles != null && __instance.knownMissiles.Contains(missile))
            {
                return false;
            }

            if (CustomMissileWarningAudioLookup.MissileWarningUnknownMissilesField?.GetValue(__instance) is List<Missile> unknown &&
                unknown.Contains(missile))
            {
                return false;
            }

            _ = aircraft;
            return true;
        }
    }

    [HarmonyPatch(typeof(MissileWarning), "Update")]
    internal static class MissileWarningExtendedRangePatch
    {
        private static void Postfix(MissileWarning __instance)
        {
            if (__instance == null)
            {
                return;
            }

            if (CustomMissileWarningAudioLookup.MissileWarningAircraftField?.GetValue(__instance) is not Aircraft aircraft ||
                aircraft.disabled ||
                !GameManager.IsLocalAircraft(aircraft) ||
                !CustomMissileWarningAudioLookup.ShouldRunExtendedRangeTick(__instance))
            {
                return;
            }

            if (CustomMissileWarningAudioLookup.MissileWarningUnknownMissilesField?.GetValue(__instance) is not List<Missile> unknown ||
                unknown.Count == 0)
            {
                return;
            }

            float baseRange = CustomMissileWarningAudioLookup.MissileWarningDetectionRangeField?.GetValue(__instance) is float dr
                ? dr
                : 5000f;

            for (int i = unknown.Count - 1; i >= 0; i--)
            {
                Missile? raw = unknown[i];
                if (raw == null ||
                    !UnitRegistry.TryGetUnit(raw.persistentID, out Missile unit) ||
                    unit == null ||
                    unit.disabled ||
                    unit.targetID != aircraft.persistentID)
                {
                    continue;
                }

                if (!CustomMissileWarningAudioLookup.TryResolveCustomWarningSpec(unit, out _, out CustomMissileSpec spec) ||
                    spec.WarningDetectionRangeMeters <= baseRange)
                {
                    continue;
                }

                if (!FastMath.InRange(unit.transform.position, __instance.transform.position, spec.WarningDetectionRangeMeters) ||
                    !unit.LineOfSight(aircraft.transform.position, 1000f))
                {
                    continue;
                }

                if (!__instance.knownMissiles.Contains(unit))
                {
                    __instance.knownMissiles.Add(unit);
                    aircraft.NetworkHQ?.CmdUpdateTrackingInfo(unit.persistentID);
                    if (CustomMissileWarningAudioLookup.MissileWarningOnMissileWarningEventField?.GetValue(__instance) is Action<MissileWarning.OnMissileWarning> onWarn)
                    {
                        onWarn.Invoke(new MissileWarning.OnMissileWarning { missile = unit });
                    }
                }

                unknown.RemoveAt(i);
            }
        }
    }

    [HarmonyPatch(typeof(ThreatItem), nameof(ThreatItem.AnimateItem))]
    internal static class ThreatItemHojLabelPatch
    {
        private static void Postfix(ThreatItem __instance)
        {
            if (__instance == null)
            {
                return;
            }

            if (CustomMissileWarningAudioLookup.ThreatItemMissileField?.GetValue(__instance) is not Missile missile)
            {
                return;
            }

            if (!CustomMissileWarningAudioLookup.TryResolveCustomWarningSpec(missile, out _, out CustomMissileSpec spec) ||
                !spec.RequiresTargetEcmToTrack)
            {
                return;
            }

            if (CustomMissileWarningAudioLookup.ThreatItemTextField?.GetValue(__instance) is Text mainText &&
                mainText != null &&
                !string.IsNullOrEmpty(mainText.text) &&
                mainText.text.IndexOf("[ARH]", StringComparison.Ordinal) >= 0)
            {
                mainText.text = mainText.text.Replace("[ARH]", "[HOJ]");
            }

            if (CustomMissileWarningAudioLookup.ThreatItemNotchIndicatorLabelField?.GetValue(__instance) is Text notchLabel &&
                notchLabel != null &&
                !string.IsNullOrEmpty(notchLabel.text) &&
                notchLabel.text.IndexOf("[ARH]", StringComparison.Ordinal) >= 0)
            {
                notchLabel.text = notchLabel.text.Replace("[ARH]", "[HOJ]");
            }
        }
    }
}
