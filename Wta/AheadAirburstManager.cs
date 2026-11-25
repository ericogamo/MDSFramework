using System;
using System.Collections.Generic;
using System.IO;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Missiles;
using MDSFramework.Sensors;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Rendering;

namespace MDSFramework.Wta
{
    internal static class AheadAirburstManager
    {
        private const int MaxActiveProjectiles = 64;
        private const int MaxActiveFlashes = 48;
        private const int ConeRayCount = 6;

        private sealed class AheadProjectile
        {
            internal Unit? OwnerUnit;
            internal FactionAffinity OwnerFaction;
            internal FactionHQ? OwnerHq;
            internal Unit? Target;
            internal Vector3 LaunchOrigin;
            internal Vector3 Position;
            internal Vector3 Velocity;
            internal float Elapsed;
            internal float ProgrammedFuseSeconds;
            internal float DamagePerBurst;
            internal GameObject? VisualGo;
            internal LineRenderer? Line;
            internal GameObject? ShellVisualGo;
            internal GlbKeyframedAnimator? ShellAnimator;
            internal float SpinDeg;
        }

        private sealed class AheadBurstFlash
        {
            internal float RemainingSeconds;
            internal float TotalSeconds;
            internal GameObject? RootGo;
            internal GameObject? ShellVisualGo;
            internal GlbKeyframedAnimator? ShellAnimator;
            internal Vector3 DriftVelocity;
        }

        private struct PendingShotAudio
        {
            internal ModularSiteBehavior Site;
            internal AudioClip Clip;
            internal float RemainingDelay;
            internal float Volume;
            internal float PitchMin;
            internal float PitchMax;
        }

        private static readonly List<AheadProjectile> Projectiles = new List<AheadProjectile>(MaxActiveProjectiles);
        private static readonly List<AheadBurstFlash> Flashes = new List<AheadBurstFlash>(MaxActiveFlashes);
        private static readonly List<PendingShotAudio> PendingAudioShots = new List<PendingShotAudio>(32);

        private static Material? tracerMaterial;
        private static Material? burstMaterial;

        internal static void FireProgrammedBurst(
            ModularSiteBehavior site,
            Unit target,
            Vector3 muzzlePos,
            Vector3 targetPos,
            Vector3 targetVel,
            bool isNuclear,
            float targetAltMeters)
        {
            if (site == null || site.Unit == null || target == null)
            {
                return;
            }

            _ = isNuclear;
            _ = targetAltMeters;

            float muzzleVel = Mathf.Max(600f, site.Spec.GunMuzzleVelocityMps);

            // Two-pass ballistic intercept with gravity drop compensation.
            float t1 = Vector3.Distance(muzzlePos, targetPos) / muzzleVel;
            Vector3 p1 = targetPos + (targetVel * t1) + (0.5f * 9.81f * t1 * t1 * Vector3.up);
            float t2 = Vector3.Distance(muzzlePos, p1) / muzzleVel;
            Vector3 interceptPos = targetPos + (targetVel * t2) + (0.5f * 9.81f * t2 * t2 * Vector3.up);

            Vector3 toIntercept = interceptPos - muzzlePos;
            float interceptDist = Mathf.Max(15f, toIntercept.magnitude);
            Vector3 baseDir = toIntercept / interceptDist;

            float standoffMeters = Mathf.Clamp(interceptDist * 0.025f, 12f, 32f);
            float baseFuseSeconds = Mathf.Max(0.03f, (interceptDist - standoffMeters) / muzzleVel);
            float burstDmg = Mathf.Max(15f, site.Spec.GunDamagePerBurst);

            string? shellGlbPath = null;
            if (MdsConfig.EnableCustomGlbVisuals.Value &&
                site.Package != null &&
                !string.IsNullOrWhiteSpace(site.Spec.GunProjectileGlbRelativePath))
            {
                string? resolved = CustomMissileBehavior.ResolveAssetPath(
                    site.Package,
                    site.Spec.GunProjectileGlbRelativePath,
                    Array.Empty<string>());
                if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                {
                    shellGlbPath = resolved;
                }
            }

            float shellScale = Mathf.Clamp(site.Spec.GunProjectileVisualScale, 0.5f, 25f);
            string shellCacheKey = $"ahead_shell_{site.Spec.UnitId}";

            for (int shot = 0; shot < 2; shot++)
            {
                if (Projectiles.Count >= MaxActiveProjectiles)
                {
                    DestroyProjectileVisual(Projectiles[0]);
                    Projectiles.RemoveAt(0);
                }

                Vector3 jitter = UnityEngine.Random.insideUnitSphere * 0.0044f; // ~0.25 deg dispersion
                Vector3 shotDir = (baseDir + jitter).normalized;
                Vector3 spawnPos = muzzlePos + (shotDir * (shot * 6f));
                float fuseOffset = shot * 0.014f;

                CreateTracerVisual(spawnPos, shotDir, out GameObject visualGo, out LineRenderer line);
                GameObject? shellVisualGo = null;
                GlbKeyframedAnimator? shellAnimator = null;
                if (!string.IsNullOrEmpty(shellGlbPath))
                {
                    var shellParent = new GameObject("MDS_AheadShell3D");
                    shellParent.transform.position = spawnPos;
                    shellParent.transform.rotation = Quaternion.LookRotation(shotDir);
                    shellParent.transform.localScale = Vector3.one * shellScale;
                    GlbInstantiatedRig? shellRig = GlbRuntimeLoader.Instantiate(
                        shellCacheKey,
                        shellGlbPath,
                        "projectile",
                        null,
                        shellParent.transform,
                        180f);
                    if (shellRig != null)
                    {
                        shellAnimator = shellRig.RootObject.GetComponent<GlbKeyframedAnimator>()
                            ?? shellRig.RootObject.AddComponent<GlbKeyframedAnimator>();
                        shellAnimator.Initialize(shellRig, 0.26f, startDeployed: false);
                        shellVisualGo = shellParent;
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(shellParent);
                    }
                }

                Projectiles.Add(new AheadProjectile
                {
                    OwnerUnit = site.Unit,
                    OwnerFaction = site.Faction,
                    OwnerHq = site.Hq,
                    Target = target,
                    LaunchOrigin = spawnPos,
                    Position = spawnPos,
                    Velocity = shotDir * muzzleVel,
                    Elapsed = 0f,
                    ProgrammedFuseSeconds = Mathf.Max(0.025f, baseFuseSeconds + fuseOffset),
                    DamagePerBurst = burstDmg * 0.6f,
                    VisualGo = visualGo,
                    Line = line,
                    ShellVisualGo = shellVisualGo,
                    ShellAnimator = shellAnimator,
                    SpinDeg = shot * 90f
                });
            }

            TriggerBurstAudio(site, 2);
        }

        internal static void Tick(float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            TickPendingAudioShots(deltaTime);
            TickFlashes(deltaTime);

            for (int i = Projectiles.Count - 1; i >= 0; i--)
            {
                AheadProjectile proj = Projectiles[i];
                Vector3 prevPos = proj.Position;
                proj.Velocity += Vector3.down * (9.81f * deltaTime);
                Vector3 nextPos = prevPos + (proj.Velocity * deltaTime);
                proj.Elapsed += deltaTime;

                if (Physics.Linecast(prevPos, nextPos, out RaycastHit _, (int)PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                {
                    DestroyProjectileVisual(proj);
                    Projectiles.RemoveAt(i);
                    continue;
                }

                bool fuseReached = proj.Elapsed >= proj.ProgrammedFuseSeconds;
                bool proximityPassed = false;
                if (!fuseReached && proj.Target != null && !proj.Target.disabled)
                {
                    Vector3 toTarget = proj.Target.transform.position - nextPos;
                    if (toTarget.sqrMagnitude <= 55f * 55f && Vector3.Dot(proj.Velocity, toTarget) <= 0f)
                    {
                        proximityPassed = true;
                    }
                }

                if (fuseReached || proximityPassed)
                {
                    DetonateAheadCone(proj, nextPos);
                    DestroyProjectileVisual(proj);
                    Projectiles.RemoveAt(i);
                    continue;
                }

                if (proj.Elapsed >= 4.5f)
                {
                    DestroyProjectileVisual(proj);
                    Projectiles.RemoveAt(i);
                    continue;
                }

                proj.Position = nextPos;
                if (proj.ShellVisualGo != null)
                {
                    proj.SpinDeg = (proj.SpinDeg + (2400f * deltaTime)) % 360f;
                    Vector3 dir = proj.Velocity.sqrMagnitude > 0.01f ? proj.Velocity.normalized : Vector3.forward;
                    proj.ShellVisualGo.transform.position = nextPos;
                    proj.ShellVisualGo.transform.rotation = Quaternion.LookRotation(dir) * Quaternion.Euler(0f, 0f, proj.SpinDeg);
                }

                UpdateTracerVisual(proj);
            }
        }

        internal static void ResetSession()
        {
            for (int i = 0; i < Projectiles.Count; i++)
            {
                DestroyProjectileVisual(Projectiles[i]);
            }

            Projectiles.Clear();

            for (int i = 0; i < Flashes.Count; i++)
            {
                if (Flashes[i].RootGo != null)
                {
                    UnityEngine.Object.Destroy(Flashes[i].RootGo);
                }

                if (Flashes[i].ShellVisualGo != null)
                {
                    UnityEngine.Object.Destroy(Flashes[i].ShellVisualGo);
                }
            }

            Flashes.Clear();
            PendingAudioShots.Clear();
        }

        internal static void Dispose()
        {
            ResetSession();
            WavAudioClipLoader.ClearCache();
            if (tracerMaterial != null)
            {
                UnityEngine.Object.Destroy(tracerMaterial);
                tracerMaterial = null;
            }

            if (burstMaterial != null)
            {
                UnityEngine.Object.Destroy(burstMaterial);
                burstMaterial = null;
            }
        }

        private static void DetonateAheadCone(AheadProjectile proj, Vector3 burstPos)
        {
            Vector3 forward = proj.Velocity.sqrMagnitude > 0.01f ? proj.Velocity.normalized : Vector3.forward;
            GameObject? shellGo = proj.ShellVisualGo;
            GlbKeyframedAnimator? shellAnim = proj.ShellAnimator;
            proj.ShellVisualGo = null;
            proj.ShellAnimator = null;
            SpawnAirburstVisual(burstPos, forward, shellGo, shellAnim, proj.SpinDeg);

            PersistentID dealerId = proj.OwnerUnit != null ? proj.OwnerUnit.persistentID : PersistentID.None;
            try
            {
                DamageEffects.BlastFrag(1.5f, burstPos, dealerId, PersistentID.None);
            }
            catch (Exception ex)
            {
                MdsLog.Debug($"Ahead BlastFrag skipped: {ex.Message}");
            }

            bool hitPrimary = false;
            if (proj.Target != null && !proj.Target.disabled)
            {
                Vector3 toPrimary = proj.Target.transform.position - burstPos;
                float dist = toPrimary.magnitude;
                bool inRange = dist <= 50f;
                bool inCone = dist <= 28f || (dist > 0.01f && Vector3.Dot(forward, toPrimary / dist) >= 0.90f);
                if (inRange && inCone)
                {
                    float scale = Mathf.Clamp(1.05f - (dist / 95f), 0.60f, 1.0f);
                    ApplyAheadDamageWithDisarm(proj.OwnerUnit, proj.Target, proj.DamagePerBurst * scale);
                    hitPrimary = true;
                }
            }

            List<Unit>? allUnits = UnitRegistry.allUnits;
            if (allUnits == null)
            {
                return;
            }

            for (int i = 0; i < allUnits.Count; i++)
            {
                Unit cand = allUnits[i];
                if (cand == null || cand.disabled || (hitPrimary && ReferenceEquals(cand, proj.Target)))
                {
                    continue;
                }

                if (!(cand is Missile || cand is Aircraft) ||
                    !GameInteropHelpers.IsHostileToAffinity(cand, proj.OwnerFaction, proj.OwnerHq))
                {
                    continue;
                }

                Vector3 toCand = cand.transform.position - burstPos;
                float dist = toCand.magnitude;
                if (dist > 45f)
                {
                    continue;
                }

                bool inCone = dist <= 18f || (dist > 0.01f && Vector3.Dot(forward, toCand / dist) >= 0.951f);
                if (!inCone)
                {
                    continue;
                }

                float scale = Mathf.Clamp(1.0f - (dist / 85f), 0.55f, 1.0f);
                ApplyAheadDamageWithDisarm(proj.OwnerUnit, cand, proj.DamagePerBurst * scale);
            }
        }

        private static void ApplyAheadDamageWithDisarm(Unit? attacker, Unit target, float damage)
        {
            if (target == null || target.disabled)
            {
                return;
            }

            bool temporarilyDisarmed = false;
            if (target is Missile m &&
                MdsConfig.EnableHighAltitudeDisarm.Value &&
                (m.transform.position.y - Datum.LocalSeaY) >= 800f &&
                GameInteropHelpers.SafeGetBlastYield(m) >= MdsConfig.AbmBlastYieldThreshold.Value &&
                m.IsArmed())
            {
                temporarilyDisarmed = GameInteropHelpers.SafeDisarmWarhead(m);
            }

            SmartEngagementEngine.ApplyThermalOrKineticDamage(attacker!, target, damage, isFire: false);

            if (temporarilyDisarmed && target is Missile mAfter && !mAfter.disabled)
            {
                mAfter.Arm();
            }
        }

        private static void CreateTracerVisual(Vector3 pos, Vector3 dir, out GameObject go, out LineRenderer line)
        {
            if (tracerMaterial == null)
            {
                tracerMaterial = UrpGlbMaterialBuilder.CreateBeamMaterial(new Color(1.0f, 0.72f, 0.22f, 1f));
            }

            go = new GameObject("MDS_AheadTracer");
            line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = 0.06f;
            line.endWidth = 0.14f;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sharedMaterial = tracerMaterial;
            line.SetPosition(0, pos);
            line.SetPosition(1, pos + (dir * 2f));
        }

        private static void UpdateTracerVisual(AheadProjectile proj)
        {
            if (proj.Line == null)
            {
                return;
            }

            Vector3 dir = proj.Velocity.sqrMagnitude > 0.01f ? proj.Velocity.normalized : Vector3.forward;
            float traveled = Vector3.Distance(proj.LaunchOrigin, proj.Position);
            float streakLen = Mathf.Min(7.5f, Mathf.Max(1.5f, traveled));
            proj.Line.SetPosition(0, proj.Position - (dir * streakLen));
            proj.Line.SetPosition(1, proj.Position);
        }

        private static void SpawnAirburstVisual(
            Vector3 burstPos,
            Vector3 forward,
            GameObject? shellVisualGo,
            GlbKeyframedAnimator? shellAnimator,
            float spinDeg)
        {
            if (burstMaterial == null)
            {
                burstMaterial = UrpGlbMaterialBuilder.CreateBeamMaterial(new Color(1.0f, 0.86f, 0.35f, 0.95f));
            }

            if (Flashes.Count >= MaxActiveFlashes)
            {
                if (Flashes[0].RootGo != null)
                {
                    UnityEngine.Object.Destroy(Flashes[0].RootGo);
                }

                if (Flashes[0].ShellVisualGo != null)
                {
                    UnityEngine.Object.Destroy(Flashes[0].ShellVisualGo);
                }

                Flashes.RemoveAt(0);
            }

            var rootGo = new GameObject("MDS_AheadAirburst");
            rootGo.transform.position = burstPos;
            Quaternion coneBasis = Quaternion.LookRotation(forward);

            for (int r = 0; r < ConeRayCount; r++)
            {
                float angleRad = (r / (float)ConeRayCount) * (Mathf.PI * 2f);
                float coneRadius = 11.5f;
                float coneLength = 34f;
                Vector3 localEnd = new Vector3(
                    Mathf.Cos(angleRad) * coneRadius,
                    Mathf.Sin(angleRad) * coneRadius,
                    coneLength);
                Vector3 worldEnd = burstPos + (coneBasis * localEnd);

                var rayGo = new GameObject($"Ray_{r}");
                rayGo.transform.SetParent(rootGo.transform, false);
                LineRenderer lr = rayGo.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.startWidth = 0.22f;
                lr.endWidth = 0.05f;
                lr.shadowCastingMode = ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.sharedMaterial = burstMaterial;
                lr.SetPosition(0, burstPos);
                lr.SetPosition(1, worldEnd);
            }

            if (shellVisualGo != null)
            {
                shellVisualGo.transform.position = burstPos;
                shellVisualGo.transform.rotation = coneBasis * Quaternion.Euler(0f, 0f, spinDeg);
                shellAnimator?.SetTargetDeployed(true);
            }

            float duration = shellVisualGo != null ? 0.30f : 0.18f;
            Flashes.Add(new AheadBurstFlash
            {
                RemainingSeconds = duration,
                TotalSeconds = duration,
                RootGo = rootGo,
                ShellVisualGo = shellVisualGo,
                ShellAnimator = shellAnimator,
                DriftVelocity = forward * 45f
            });
        }

        private static void TickFlashes(float dt)
        {
            for (int i = Flashes.Count - 1; i >= 0; i--)
            {
                AheadBurstFlash flash = Flashes[i];
                flash.RemainingSeconds -= dt;
                if (flash.RemainingSeconds <= 0f)
                {
                    if (flash.RootGo != null)
                    {
                        UnityEngine.Object.Destroy(flash.RootGo);
                    }

                    if (flash.ShellVisualGo != null)
                    {
                        UnityEngine.Object.Destroy(flash.ShellVisualGo);
                    }

                    Flashes.RemoveAt(i);
                }
                else if (flash.ShellVisualGo != null)
                {
                    flash.ShellVisualGo.transform.position += flash.DriftVelocity * dt;
                    flash.ShellAnimator?.TickAnimation(dt, allowTurretOverrides: false);
                }
            }
        }

        private static void DestroyProjectileVisual(AheadProjectile proj)
        {
            if (proj.VisualGo != null)
            {
                UnityEngine.Object.Destroy(proj.VisualGo);
                proj.VisualGo = null;
                proj.Line = null;
            }

            if (proj.ShellVisualGo != null)
            {
                UnityEngine.Object.Destroy(proj.ShellVisualGo);
                proj.ShellVisualGo = null;
                proj.ShellAnimator = null;
            }
        }

        private static void TriggerBurstAudio(ModularSiteBehavior site, int shotCount)
        {
            if (shotCount <= 0 || site.Package == null || string.IsNullOrWhiteSpace(site.Spec.GunFireAudioRelativePath))
            {
                return;
            }

            string? audioPath = CustomMissileBehavior.ResolveAssetPath(
                site.Package,
                site.Spec.GunFireAudioRelativePath,
                Array.Empty<string>());
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
            {
                return;
            }

            AudioClip? clip = WavAudioClipLoader.LoadOrGet(audioPath);
            if (clip == null)
            {
                return;
            }

            float volume = Mathf.Clamp(site.Spec.GunFireAudioVolume, 0f, 1.5f);
            float pMin = Mathf.Clamp(site.Spec.GunFireAudioPitchMin, 0.5f, 1.5f);
            float pMax = Mathf.Clamp(Mathf.Max(pMin, site.Spec.GunFireAudioPitchMax), 0.5f, 1.5f);
            float interval = Mathf.Clamp(site.Spec.GunBurstShotIntervalSeconds, 0.03f, 0.25f);

            PlaySiteGunShot(site, clip, volume, pMin, pMax);

            for (int shot = 1; shot < shotCount; shot++)
            {
                if (PendingAudioShots.Count >= 64)
                {
                    break;
                }

                PendingAudioShots.Add(new PendingShotAudio
                {
                    Site = site,
                    Clip = clip,
                    RemainingDelay = shot * interval,
                    Volume = volume,
                    PitchMin = pMin,
                    PitchMax = pMax
                });
            }
        }

        private static void TickPendingAudioShots(float dt)
        {
            for (int i = PendingAudioShots.Count - 1; i >= 0; i--)
            {
                PendingShotAudio pending = PendingAudioShots[i];
                pending.RemainingDelay -= dt;
                if (pending.RemainingDelay > 0f)
                {
                    PendingAudioShots[i] = pending;
                    continue;
                }

                PendingAudioShots.RemoveAt(i);
                if (pending.Site != null && pending.Site.IsAlive && pending.Clip != null)
                {
                    PlaySiteGunShot(pending.Site, pending.Clip, pending.Volume, pending.PitchMin, pending.PitchMax);
                }
            }
        }

        private static AudioSource[]? EnsureGunAudioVoices(ModularSiteBehavior site)
        {
            if (site.Unit == null)
            {
                return null;
            }

            AudioSource[]? existing = site.GunAudioVoices;
            if (existing != null && existing.Length > 0 && existing[0] != null)
            {
                return existing;
            }

            AudioMixerGroup? mixerGroup = null;
            AudioSource[] unitSources = site.Unit.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < unitSources.Length; i++)
            {
                if (unitSources[i] != null && unitSources[i].outputAudioMixerGroup != null)
                {
                    mixerGroup = unitSources[i].outputAudioMixerGroup;
                    break;
                }
            }

            if (mixerGroup == null)
            {
                try
                {
                    if (SoundManager.i != null)
                    {
                        mixerGroup = SoundManager.i.HeavyEffectsMixer != null
                            ? SoundManager.i.HeavyEffectsMixer
                            : SoundManager.i.EffectsMixer;
                    }
                }
                catch
                {
                    // Optional native mixer lookup
                }
            }

            Transform anchor = site.Rig != null && site.Rig.MuzzleTransform != null
                ? site.Rig.MuzzleTransform
                : site.Unit.transform;

            var audioGo = new GameObject("MDS_GunAudio");
            audioGo.transform.SetParent(anchor, false);
            audioGo.transform.localPosition = Vector3.zero;
            audioGo.transform.localRotation = Quaternion.identity;

            float minDist = Mathf.Max(5f, site.Spec.GunFireAudioMinDistance);
            float maxDist = Mathf.Max(200f, site.Spec.GunFireAudioMaxDistance);
            const int voiceCount = 4;
            var voices = new AudioSource[voiceCount];
            for (int i = 0; i < voiceCount; i++)
            {
                AudioSource src = audioGo.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = false;
                src.spatialBlend = 1.0f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.minDistance = minDist;
                src.maxDistance = maxDist;
                src.dopplerLevel = 0.5f;
                src.spread = 15f;
                if (mixerGroup != null)
                {
                    src.outputAudioMixerGroup = mixerGroup;
                }

                voices[i] = src;
            }

            site.GunAudioVoices = voices;
            site.GunAudioVoiceCursor = 0;
            return voices;
        }

        private static void PlaySiteGunShot(
            ModularSiteBehavior site,
            AudioClip clip,
            float volume,
            float pitchMin,
            float pitchMax)
        {
            AudioSource[]? voices = EnsureGunAudioVoices(site);
            if (voices == null || voices.Length == 0)
            {
                return;
            }

            int idx = Math.Abs(site.GunAudioVoiceCursor++) % voices.Length;
            AudioSource voice = voices[idx];
            if (voice == null)
            {
                return;
            }

            voice.clip = clip;
            voice.volume = volume;
            voice.pitch = UnityEngine.Random.Range(pitchMin, pitchMax);
            voice.Play();
        }
    }
}
