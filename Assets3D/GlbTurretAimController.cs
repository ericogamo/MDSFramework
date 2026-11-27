using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MDSFramework.Assets3D
{
    internal sealed class GlbTurretAimController : MonoBehaviour
    {
        private GlbInstantiatedRig? rig;
        private GlbKeyframedAnimator? animator;
        private float traverseRateDegPerSec = 90f;
        private float elevationRateDegPerSec = 75f;
        private float minElevationDeg = -10f;
        private float maxElevationDeg = 85f;
        private bool spinRadarDish;
        private float radarSpinRateDegPerSec = 90f;

        private float currentYawDeg;
        private float currentPitchDeg;
        private float targetYawDeg;
        private float targetPitchDeg;
        private float desiredUnclampedPitchDeg;
        private bool hasAimTarget;
        private int vlsRoundRobinIndex;
        private int vlsSecondaryRoundRobinIndex;
        private float idleSweepTime;

        private LineRenderer? beamRenderer;
        private Material? beamMaterial;
        private float beamVisibleTimer;
        private Vector3 beamTargetWorld;

        internal bool IsOnTarget { get; private set; }
        internal bool ReadyToStow { get; private set; } = true;
        internal float CurrentYawDeg => currentYawDeg;
        internal float CurrentPitchDeg => currentPitchDeg;

        internal void SetReplicatedAngles(float yawDeg, float pitchDeg)
        {
            targetYawDeg = yawDeg;
            targetPitchDeg = Mathf.Clamp(pitchDeg, minElevationDeg, maxElevationDeg);
            desiredUnclampedPitchDeg = pitchDeg;
            hasAimTarget = true;
        }

        internal void TickReplicatedClient(float deltaTime, bool radarEmitting, bool stowRequested)
        {
            if (rig == null || animator == null)
            {
                return;
            }

            if (beamVisibleTimer > 0f)
            {
                beamVisibleTimer -= deltaTime;
                if (beamVisibleTimer <= 0f)
                {
                    if (beamRenderer != null)
                    {
                        beamRenderer.enabled = false;
                    }
                }
                else
                {
                    UpdateBeamEndpoints();
                }
            }

            if (!animator.IsFullyDeployed)
            {
                ReadyToStow = true;
                return;
            }

            if (spinRadarDish && radarEmitting && !stowRequested && rig.RadarDish != null)
            {
                rig.RadarDish.Rotate(0f, radarSpinRateDegPerSec * deltaTime, 0f, Space.Self);
            }

            if (rig.TurretBase == null)
            {
                ReadyToStow = true;
                return;
            }

            if (stowRequested || !animator.TargetDeployed)
            {
                float restYaw = NormalizeSignedAngle(animator.DeployedTurretBaseLocalRot.eulerAngles.y);
                float restPitch = rig.TurretHead != null
                    ? Mathf.Clamp(-NormalizeSignedAngle(animator.DeployedTurretHeadLocalRot.eulerAngles.x), minElevationDeg, maxElevationDeg)
                    : 0f;

                currentYawDeg = Mathf.MoveTowardsAngle(currentYawDeg, restYaw, traverseRateDegPerSec * deltaTime);
                currentPitchDeg = Mathf.MoveTowards(currentPitchDeg, restPitch, elevationRateDegPerSec * deltaTime);
                ApplyTurretRotations();
                ReadyToStow = Mathf.Abs(Mathf.DeltaAngle(currentYawDeg, restYaw)) < 2.0f &&
                              Mathf.Abs(currentPitchDeg - restPitch) < 2.0f;
                return;
            }

            ReadyToStow = false;
            if (!hasAimTarget)
            {
                return;
            }

            currentYawDeg = Mathf.MoveTowardsAngle(currentYawDeg, targetYawDeg, traverseRateDegPerSec * deltaTime);
            currentPitchDeg = Mathf.MoveTowards(currentPitchDeg, targetPitchDeg, elevationRateDegPerSec * deltaTime);
            ApplyTurretRotations();
        }

        internal void Initialize(
            GlbInstantiatedRig instantiatedRig,
            GlbKeyframedAnimator keyframedAnimator,
            float traverseRate,
            float elevationRate,
            float minElev,
            float maxElev,
            bool spinDish,
            float dishSpinRate,
            Color beamColor)
        {
            rig = instantiatedRig;
            animator = keyframedAnimator;
            traverseRateDegPerSec = Mathf.Max(15f, traverseRate);
            elevationRateDegPerSec = Mathf.Max(15f, elevationRate);
            minElevationDeg = minElev;
            maxElevationDeg = maxElev;
            spinRadarDish = spinDish;
            radarSpinRateDegPerSec = dishSpinRate;

            if (rig.TurretHead != null)
            {
                Vector3 headEuler = animator.DeployedTurretHeadLocalRot.eulerAngles;
                float rawX = NormalizeSignedAngle(headEuler.x);
                currentPitchDeg = Mathf.Clamp(-rawX, minElevationDeg, maxElevationDeg);
                targetPitchDeg = currentPitchDeg;
            }

            if (rig.TurretBase != null)
            {
                Vector3 baseEuler = animator.DeployedTurretBaseLocalRot.eulerAngles;
                currentYawDeg = NormalizeSignedAngle(baseEuler.y);
                targetYawDeg = currentYawDeg;
            }

            EnsureBeamRenderer(beamColor);
        }

        internal void SetAimWorldPosition(Vector3 worldPos)
        {
            if (rig == null || rig.TurretBase == null)
            {
                IsOnTarget = true;
                return;
            }

            Transform pivot = rig.TurretHead != null ? rig.TurretHead : rig.TurretBase;
            Vector3 origin = pivot.position;
            Vector3 worldDir = worldPos - origin;
            if (worldDir.sqrMagnitude < 0.01f)
            {
                IsOnTarget = true;
                return;
            }

            Transform baseParent = rig.TurretBase.parent != null ? rig.TurretBase.parent : rig.RootObject.transform;
            Vector3 localDir = baseParent.InverseTransformDirection(worldDir.normalized);

            float desiredYaw = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;
            float horizMag = Mathf.Sqrt(localDir.x * localDir.x + localDir.z * localDir.z);
            float desiredPitch = Mathf.Atan2(localDir.y, Mathf.Max(0.0001f, horizMag)) * Mathf.Rad2Deg;

            targetYawDeg = desiredYaw;
            desiredUnclampedPitchDeg = desiredPitch;
            targetPitchDeg = Mathf.Clamp(desiredPitch, minElevationDeg, maxElevationDeg);
            hasAimTarget = true;
        }

        internal void ClearAim()
        {
            hasAimTarget = false;
            IsOnTarget = false;
        }

        internal Transform GetNextLaunchTransform()
        {
            if (rig == null)
            {
                return transform;
            }

            if (rig.VlsCells.Count > 0)
            {
                Transform cell = rig.VlsCells[vlsRoundRobinIndex % rig.VlsCells.Count];
                vlsRoundRobinIndex = (vlsRoundRobinIndex + 1) % rig.VlsCells.Count;
                if (cell != null)
                {
                    return cell;
                }
            }

            if (rig.MuzzleTransform != null)
            {
                return rig.MuzzleTransform;
            }

            if (rig.TurretHead != null)
            {
                return rig.TurretHead;
            }

            return rig.RootObject != null ? rig.RootObject.transform : transform;
        }

        internal Transform GetNextLaunchTransformForSlot(bool isSecondary, int primaryCellCount, int secondaryCellCount)
        {
            if (rig != null && rig.VlsCells.Count > 0 && primaryCellCount > 0 && secondaryCellCount > 0)
            {
                int total = rig.VlsCells.Count;
                int primaryCount = Mathf.Min(primaryCellCount, total);
                if (!isSecondary)
                {
                    int idx = vlsRoundRobinIndex % primaryCount;
                    vlsRoundRobinIndex = (vlsRoundRobinIndex + 1) % primaryCount;
                    if (rig.VlsCells[idx] != null)
                    {
                        return rig.VlsCells[idx];
                    }
                }
                else
                {
                    int start = primaryCount < total ? primaryCount : 0;
                    int avail = Mathf.Max(1, Mathf.Min(secondaryCellCount, total - start));
                    int idx = start + (vlsSecondaryRoundRobinIndex % avail);
                    vlsSecondaryRoundRobinIndex = (vlsSecondaryRoundRobinIndex + 1) % avail;
                    if (rig.VlsCells[idx] != null)
                    {
                        return rig.VlsCells[idx];
                    }
                }
            }

            return GetNextLaunchTransform();
        }

        internal void ShowBeamOrTracer(Vector3 targetWorldPos, float durationSeconds, float widthMeters)
        {
            if (beamRenderer == null)
            {
                return;
            }

            beamTargetWorld = targetWorldPos;
            beamVisibleTimer = Mathf.Max(beamVisibleTimer, durationSeconds);
            beamRenderer.startWidth = widthMeters;
            beamRenderer.endWidth = widthMeters * 0.65f;
            beamRenderer.enabled = true;
            UpdateBeamEndpoints();
        }

        internal void HideBeam()
        {
            beamVisibleTimer = 0f;
            if (beamRenderer != null)
            {
                beamRenderer.enabled = false;
            }
        }

        internal void TickAim(float deltaTime, bool radarEmitting, bool stowRequested = false)
        {
            if (rig == null || animator == null)
            {
                return;
            }

            if (beamVisibleTimer > 0f)
            {
                beamVisibleTimer -= deltaTime;
                if (beamVisibleTimer <= 0f)
                {
                    if (beamRenderer != null)
                    {
                        beamRenderer.enabled = false;
                    }
                }
                else
                {
                    UpdateBeamEndpoints();
                }
            }

            if (!animator.IsFullyDeployed)
            {
                IsOnTarget = false;
                ReadyToStow = true;
                return;
            }

            if (spinRadarDish && radarEmitting && !stowRequested && rig.RadarDish != null)
            {
                rig.RadarDish.Rotate(0f, radarSpinRateDegPerSec * deltaTime, 0f, Space.Self);
            }

            if (rig.TurretBase == null)
            {
                IsOnTarget = true;
                ReadyToStow = true;
                return;
            }

            if (stowRequested || !animator.TargetDeployed)
            {
                // Return turret cleanly to deployed rest angles before closing clamshell roof.
                float restYaw = NormalizeSignedAngle(animator.DeployedTurretBaseLocalRot.eulerAngles.y);
                float restPitch = rig.TurretHead != null
                    ? Mathf.Clamp(-NormalizeSignedAngle(animator.DeployedTurretHeadLocalRot.eulerAngles.x), minElevationDeg, maxElevationDeg)
                    : 0f;

                currentYawDeg = Mathf.MoveTowardsAngle(currentYawDeg, restYaw, traverseRateDegPerSec * deltaTime);
                currentPitchDeg = Mathf.MoveTowards(currentPitchDeg, restPitch, elevationRateDegPerSec * deltaTime);
                ApplyTurretRotations();

                float yawErr = Mathf.Abs(Mathf.DeltaAngle(currentYawDeg, restYaw));
                float pitchErr = Mathf.Abs(currentPitchDeg - restPitch);
                ReadyToStow = yawErr < 2.0f && pitchErr < 2.0f;
                IsOnTarget = false;
                return;
            }

            ReadyToStow = false;
            if (!hasAimTarget)
            {
                float restYaw = NormalizeSignedAngle(animator.DeployedTurretBaseLocalRot.eulerAngles.y);
                idleSweepTime += deltaTime * 0.22f;
                float sweepYaw = NormalizeSignedAngle(restYaw + (Mathf.Sin(idleSweepTime) * 45f));
                float sweepPitch = Mathf.Clamp(12f, minElevationDeg, maxElevationDeg);

                currentYawDeg = Mathf.MoveTowardsAngle(currentYawDeg, sweepYaw, traverseRateDegPerSec * 0.45f * deltaTime);
                currentPitchDeg = Mathf.MoveTowards(currentPitchDeg, sweepPitch, elevationRateDegPerSec * 0.45f * deltaTime);
                ApplyTurretRotations();
                IsOnTarget = false;
                return;
            }

            currentYawDeg = Mathf.MoveTowardsAngle(currentYawDeg, targetYawDeg, traverseRateDegPerSec * deltaTime);
            currentPitchDeg = Mathf.MoveTowards(currentPitchDeg, targetPitchDeg, elevationRateDegPerSec * deltaTime);
            ApplyTurretRotations();

            float refPitch = (minElevationDeg >= 15f && desiredUnclampedPitchDeg >= -20f)
                ? targetPitchDeg
                : desiredUnclampedPitchDeg;
            float aimYawErr = Mathf.Abs(Mathf.DeltaAngle(currentYawDeg, targetYawDeg));
            float aimPitchErr = Mathf.Abs(currentPitchDeg - refPitch);
            IsOnTarget = aimYawErr <= 3.5f && aimPitchErr <= 3.5f;
        }

        private void ApplyTurretRotations()
        {
            if (rig == null)
            {
                return;
            }

            if (rig.TurretBase != null)
            {
                rig.TurretBase.localRotation = Quaternion.Euler(0f, currentYawDeg, 0f);
            }

            if (rig.TurretHead != null)
            {
                // Negative X rotation elevates +Z forward axis upward in Unity's left-handed frame.
                rig.TurretHead.localRotation = Quaternion.Euler(-currentPitchDeg, 0f, 0f);
            }
        }

        private void EnsureBeamRenderer(Color color)
        {
            if (beamRenderer != null)
            {
                return;
            }

            var beamGo = new GameObject("MDS_WeaponBeam");
            beamGo.transform.SetParent(transform, false);
            beamRenderer = beamGo.AddComponent<LineRenderer>();
            beamRenderer.useWorldSpace = true;
            beamRenderer.positionCount = 2;
            beamRenderer.startWidth = 0.08f;
            beamRenderer.endWidth = 0.05f;
            beamRenderer.shadowCastingMode = ShadowCastingMode.Off;
            beamRenderer.receiveShadows = false;
            beamRenderer.enabled = false;

            beamMaterial = UrpGlbMaterialBuilder.CreateBeamMaterial(color);
            beamRenderer.sharedMaterial = beamMaterial;
        }

        private void UpdateBeamEndpoints()
        {
            if (beamRenderer == null)
            {
                return;
            }

            Transform originTf = rig?.MuzzleTransform != null
                ? rig.MuzzleTransform
                : rig?.TurretHead != null
                    ? rig.TurretHead
                    : transform;

            beamRenderer.SetPosition(0, originTf.position);
            beamRenderer.SetPosition(1, beamTargetWorld);
        }

        private void OnDestroy()
        {
            if (beamMaterial != null)
            {
                Destroy(beamMaterial);
                beamMaterial = null;
            }
        }

        private static float NormalizeSignedAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f)
            {
                angle -= 360f;
            }
            else if (angle < -180f)
            {
                angle += 360f;
            }

            return angle;
        }
    }
}
