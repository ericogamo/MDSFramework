using System;
using UnityEngine;

namespace MDSFramework.Assets3D
{
    internal sealed class GlbKeyframedAnimator : MonoBehaviour
    {
        private GlbInstantiatedRig? rig;
        private GlbAnimationClipData? clip;
        private float currentTime;
        private float clipDuration;
        private float playbackRate = 1f;
        private bool targetDeployed;
        private bool hasClip;
        private bool lastSampleApplied;

        internal bool TargetDeployed => targetDeployed;
        internal bool HasClip => hasClip;
        internal float NormalizedProgress => clipDuration > 0.0001f ? Mathf.Clamp01(currentTime / clipDuration) : (targetDeployed ? 1f : 0f);
        internal bool IsFullyDeployed => !hasClip || (targetDeployed && NormalizedProgress >= 0.995f);
        internal bool IsFullyStowed => hasClip ? (!targetDeployed && NormalizedProgress <= 0.005f) : !targetDeployed;

        internal Vector3 DeployedTurretBaseLocalPos { get; private set; }
        internal Quaternion DeployedTurretBaseLocalRot { get; private set; } = Quaternion.identity;
        internal Quaternion DeployedTurretHeadLocalRot { get; private set; } = Quaternion.identity;

        internal void Initialize(GlbInstantiatedRig instantiatedRig, float desiredDurationSeconds = 0f, bool startDeployed = false)
        {
            rig = instantiatedRig;
            clip = instantiatedRig.DeployClip;
            hasClip = clip != null && clip.Channels.Count > 0 && clip.Duration > 0.001f;
            clipDuration = hasClip ? clip!.Duration : Mathf.Max(0.1f, desiredDurationSeconds);
            if (hasClip && desiredDurationSeconds > 0.1f)
            {
                playbackRate = clip!.Duration / desiredDurationSeconds;
            }
            else
            {
                playbackRate = 1f;
            }

            if (hasClip)
            {
                // Capture deployed rest transforms at end of clip so turret aiming resets cleanly.
                SampleClipAtTime(clipDuration, suppressTurretOverrides: false);
                if (rig.TurretBase != null)
                {
                    DeployedTurretBaseLocalPos = rig.TurretBase.localPosition;
                    DeployedTurretBaseLocalRot = rig.TurretBase.localRotation;
                }

                if (rig.TurretHead != null)
                {
                    DeployedTurretHeadLocalRot = rig.TurretHead.localRotation;
                }
            }
            else
            {
                if (rig.TurretBase != null)
                {
                    DeployedTurretBaseLocalPos = rig.TurretBase.localPosition;
                    DeployedTurretBaseLocalRot = rig.TurretBase.localRotation;
                }

                if (rig.TurretHead != null)
                {
                    DeployedTurretHeadLocalRot = rig.TurretHead.localRotation;
                }
            }

            targetDeployed = startDeployed;
            currentTime = startDeployed ? clipDuration : 0f;
            if (hasClip)
            {
                SampleClipAtTime(currentTime, suppressTurretOverrides: false);
                lastSampleApplied = true;
            }
        }

        internal void InitializeOneShot(
            GlbInstantiatedRig instantiatedRig,
            GlbAnimationClipData? oneShotClip,
            float desiredDurationSeconds = 2.5f)
        {
            rig = instantiatedRig;
            clip = oneShotClip ?? instantiatedRig.DestroyClip ?? instantiatedRig.DeployClip;
            hasClip = clip != null && clip.Channels.Count > 0 && clip.Duration > 0.001f;
            clipDuration = hasClip ? clip!.Duration : Mathf.Max(0.1f, desiredDurationSeconds);
            playbackRate = (hasClip && desiredDurationSeconds > 0.1f)
                ? (clip!.Duration / desiredDurationSeconds)
                : 1f;
            targetDeployed = true;
            currentTime = 0f;
            lastSampleApplied = false;
            if (hasClip)
            {
                SampleClipAtTime(0f, suppressTurretOverrides: false);
                lastSampleApplied = true;
            }
        }

        internal void SetTargetDeployed(bool deployed)
        {
            targetDeployed = deployed;
        }

        internal void ForceProgress(float normalized)
        {
            targetDeployed = normalized >= 0.5f;
            currentTime = Mathf.Clamp01(normalized) * clipDuration;
            if (hasClip)
            {
                SampleClipAtTime(currentTime, suppressTurretOverrides: false);
                lastSampleApplied = true;
            }
        }

        internal void TickAnimation(float deltaTime, bool allowTurretOverrides)
        {
            if (!hasClip)
            {
                float targetTime = targetDeployed ? clipDuration : 0f;
                currentTime = Mathf.MoveTowards(currentTime, targetTime, deltaTime * playbackRate);
                return;
            }

            float goalTime = targetDeployed ? clipDuration : 0f;
            bool moving = !Mathf.Approximately(currentTime, goalTime);
            if (moving)
            {
                currentTime = Mathf.MoveTowards(currentTime, goalTime, deltaTime * playbackRate);
                SampleClipAtTime(currentTime, suppressTurretOverrides: allowTurretOverrides);
                lastSampleApplied = true;
            }
            else if (!lastSampleApplied)
            {
                SampleClipAtTime(currentTime, suppressTurretOverrides: allowTurretOverrides);
                lastSampleApplied = true;
            }
        }

        private void SampleClipAtTime(float time, bool suppressTurretOverrides)
        {
            if (rig == null || clip == null)
            {
                return;
            }

            Transform[] nodes = rig.NodeTransforms;
            for (int c = 0; c < clip.Channels.Count; c++)
            {
                GlbAnimationChannelData ch = clip.Channels[c];
                if (ch.NodeIndex < 0 || ch.NodeIndex >= nodes.Length)
                {
                    continue;
                }

                Transform targetTf = nodes[ch.NodeIndex];
                if (targetTf == null)
                {
                    continue;
                }

                if (suppressTurretOverrides &&
                    ch.Path == GlbAnimationPath.Rotation &&
                    (ReferenceEquals(targetTf, rig.TurretBase) ||
                     ReferenceEquals(targetTf, rig.TurretHead) ||
                     ReferenceEquals(targetTf, rig.RadarDish)))
                {
                    continue;
                }

                switch (ch.Path)
                {
                    case GlbAnimationPath.Translation:
                        targetTf.localPosition = SampleVec3(ch, time);
                        break;
                    case GlbAnimationPath.Scale:
                        targetTf.localScale = SampleVec3(ch, time);
                        break;
                    case GlbAnimationPath.Rotation:
                        targetTf.localRotation = SampleQuat(ch, time);
                        break;
                }
            }
        }

        private static Vector3 SampleVec3(GlbAnimationChannelData ch, float time)
        {
            float[] times = ch.Times;
            Vector3[] vals = ch.Vec3Values;
            int count = times.Length;
            if (count == 0 || vals.Length == 0)
            {
                return Vector3.zero;
            }

            if (ch.Interpolation == GlbInterpolationMode.CubicSpline && vals.Length >= count * 3 && vals.Length >= 3)
            {
                return SampleVec3Cubic(times, vals, time);
            }

            if (time <= times[0] || count == 1 || vals.Length == 1)
            {
                return vals[0];
            }

            int last = Math.Min(count - 1, vals.Length - 1);
            if (time >= times[count - 1])
            {
                return vals[last];
            }

            int idx = Math.Min(FindSegment(times, time), vals.Length - 1);
            if (ch.Interpolation == GlbInterpolationMode.Step)
            {
                return vals[idx];
            }

            int nextIdx = Math.Min(idx + 1, vals.Length - 1);
            float dt = times[idx + 1] - times[idx];
            float t = dt > 0.00001f ? (time - times[idx]) / dt : 0f;
            return Vector3.Lerp(vals[idx], vals[nextIdx], t);
        }

        private static Vector3 SampleVec3Cubic(float[] times, Vector3[] vals, float time)
        {
            int count = times.Length;
            if (time <= times[0] || count == 1)
            {
                return vals[1];
            }

            int last = count - 1;
            if (time >= times[last])
            {
                return vals[(last * 3) + 1];
            }

            int k = FindSegment(times, time);
            float dt = times[k + 1] - times[k];
            float t = dt > 0.00001f ? (time - times[k]) / dt : 0f;
            float t2 = t * t;
            float t3 = t2 * t;

            Vector3 p0 = vals[(k * 3) + 1];
            Vector3 b0 = vals[(k * 3) + 2] * dt;
            Vector3 a1 = vals[((k + 1) * 3) + 0] * dt;
            Vector3 p1 = vals[((k + 1) * 3) + 1];

            return ((2f * t3 - 3f * t2 + 1f) * p0)
                 + ((t3 - 2f * t2 + t) * b0)
                 + ((-2f * t3 + 3f * t2) * p1)
                 + ((t3 - t2) * a1);
        }

        private static Quaternion SampleQuat(GlbAnimationChannelData ch, float time)
        {
            float[] times = ch.Times;
            Quaternion[] vals = ch.QuatValues;
            int count = times.Length;
            if (count == 0 || vals.Length == 0)
            {
                return Quaternion.identity;
            }

            if (ch.Interpolation == GlbInterpolationMode.CubicSpline && vals.Length >= count * 3 && vals.Length >= 3)
            {
                if (time <= times[0] || count == 1)
                {
                    return vals[1];
                }

                int lastC = count - 1;
                if (time >= times[lastC])
                {
                    return vals[(lastC * 3) + 1];
                }

                int k = FindSegment(times, time);
                float dt = times[k + 1] - times[k];
                float t = dt > 0.00001f ? (time - times[k]) / dt : 0f;
                return Quaternion.Slerp(vals[(k * 3) + 1], vals[((k + 1) * 3) + 1], t);
            }

            if (time <= times[0] || count == 1 || vals.Length == 1)
            {
                return vals[0];
            }

            int last = Math.Min(count - 1, vals.Length - 1);
            if (time >= times[count - 1])
            {
                return vals[last];
            }

            int idx = Math.Min(FindSegment(times, time), vals.Length - 1);
            if (ch.Interpolation == GlbInterpolationMode.Step)
            {
                return vals[idx];
            }

            int nextIdx = Math.Min(idx + 1, vals.Length - 1);
            float segDt = times[idx + 1] - times[idx];
            float alpha = segDt > 0.00001f ? (time - times[idx]) / segDt : 0f;
            return Quaternion.Slerp(vals[idx], vals[nextIdx], alpha);
        }

        private static int FindSegment(float[] times, float time)
        {
            int lo = 0;
            int hi = times.Length - 2;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (time < times[mid])
                {
                    hi = mid - 1;
                }
                else if (time >= times[mid + 1])
                {
                    lo = mid + 1;
                }
                else
                {
                    return mid;
                }
            }

            return Math.Max(0, Math.Min(times.Length - 2, lo));
        }
    }
}
