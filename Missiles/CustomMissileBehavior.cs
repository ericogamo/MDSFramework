using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Deployment;
using MDSFramework.Sensors;
using UnityEngine;

namespace MDSFramework.Missiles
{
    internal static class CustomMissileTracker
    {
        internal static readonly Dictionary<uint, CustomMissileBehavior> ActiveByUnitId =
            new Dictionary<uint, CustomMissileBehavior>();

        internal static void Register(CustomMissileBehavior behavior)
        {
            if (behavior != null && behavior.Missile != null && behavior.Missile.persistentID.IsValid)
            {
                ActiveByUnitId[behavior.Missile.persistentID.Id] = behavior;
            }
        }

        internal static void Unregister(CustomMissileBehavior behavior)
        {
            if (behavior != null && behavior.Missile != null && behavior.Missile.persistentID.IsValid)
            {
                ActiveByUnitId.Remove(behavior.Missile.persistentID.Id);
            }
        }

        internal static bool TryGet(Unit? unit, out CustomMissileBehavior behavior)
        {
            if (unit != null && unit.persistentID.IsValid)
            {
                return ActiveByUnitId.TryGetValue(unit.persistentID.Id, out behavior!);
            }

            behavior = null!;
            return false;
        }

        internal static void ResetSession()
        {
            ActiveByUnitId.Clear();
        }
    }

    internal sealed class CustomMissileBehavior : MonoBehaviour
    {
        internal ContractorPackage Package = null!;
        internal CustomMissileSpec Spec = null!;
        internal Missile Missile = null!;
        internal Unit? TargetUnit;
        internal FactionAffinity OwnerFaction;
        internal bool PassiveHojActive;

        private static readonly int TerrainClearanceMask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ExclusionZonesMask;
        private const float LandTreeClearanceMeters = 58f;

        private GlbInstantiatedRig? rig;
        private GlbKeyframedAnimator? animator;
        private bool finsDeployed;
        private bool aeroFinsDeployed;
        private bool boosterDropped;
        private bool interceptedTarget;
        private bool launchedVertically;
        private float ageSeconds;
        private float maxFlightLifetimeSeconds = 45f;
        private float lastHojAimUpdate;
        private float lastDatalinkRefresh;
        private float lastProfileAimUpdate;
        private float nextJamPulseTime;
        private bool hojUpdatedAimThisTick;
        private float weaveSeed;
        private Vector3 initialLaunchDir = Vector3.up;
        private Vector3 prevPos;
        private Vector3 prevTargetPos;
        private bool hasPrevPositions;
        private Vector3 prevVisualPos;
        private bool hasPrevVisualPos;
        private Vector3 lastKnownHojPos;
        private Vector3 lastKnownHojVel;
        private float lastKnownHojTime;
        private bool hasLastKnownHoj;

        // Pre-calculated 3D terrain-avoidance route & evasive guidance state
        private readonly List<Vector3> precomputedRoutePoints = new List<Vector3>(256);
        private readonly List<bool> precomputedRouteOverLand = new List<bool>(256);
        private readonly List<AaThreatBubble> hostileAaThreats = new List<AaThreatBubble>(32);
        private int routeCursorIndex;
        private bool routePrecomputed;
        private Vector3 routeTargetAnchorPos;
        private float lastRouteRecomputeTime;
        private float smoothedJinkOffset;
        private bool emergencyPullUpThisTick;
        private int chosenFlankSign;
        private float nextLiveAaCheckTime;

        // Air-drop free-fall separation -> unpowered aerodynamic glide -> 10 ft sea-skim ignition (JSM)
        private bool airDropGlideActive;
        private bool airDropMotorIgnited;
        private float airDropGlideStartSpeed;
        private FieldInfo? motorDelayTimerField;
        private FieldInfo? motorStartupSourceField;
        private AudioSource? cachedStartupAudio;
        private float cachedStartupAudioVol = 1f;
        private bool startupAudioMuted;

        private readonly struct AaThreatBubble
        {
            internal readonly Vector3 Pos;
            internal readonly float Radius;
            internal readonly float Weight;

            internal AaThreatBubble(Vector3 pos, float radius, float weight)
            {
                Pos = pos;
                Radius = radius;
                Weight = weight;
            }
        }

        // Flight attitude & control-surface animation state
        private bool controlSurfacesCalibrated;
        private Quaternion[] finRestLocalRots = Array.Empty<Quaternion>();
        private Vector3[] finTrailingUpDirsInMissile = Array.Empty<Vector3>();
        private Vector3[] finRadialDirsInMissile = Array.Empty<Vector3>();
        private Quaternion wingLeftRestLocalRot = Quaternion.identity;
        private Quaternion wingRightRestLocalRot = Quaternion.identity;
        private Quaternion rigBaseLocalRot = Quaternion.identity;
        private float aoaPitchSign = -1f;
        private float bankRollSign = -1f;
        private float wingLeftFlexSign = 1f;
        private float wingRightFlexSign = -1f;
        private Vector3 prevVisualVelDir = Vector3.forward;
        private bool hasPrevVisualVelDir;
        private float currentBankDeg;
        private float smoothedPitchCmd;
        private float smoothedYawCmd;
        private float smoothedRollCmd;
        private float smoothedVisualAoADeg;
        private float smoothedWingFlexDeg;

        internal float ThermalHardening => Spec != null ? Mathf.Max(0.25f, Spec.ThermalHardening) : 1.0f;
        internal bool IsDecoy => Spec != null && (Spec.IsDecoy || Spec.FlightProfile == MissileFlightProfile.Decoy);
        internal bool IsTargetActivelyJamming { get; private set; }

        internal void Initialize(
            ContractorPackage package,
            CustomMissileSpec spec,
            Missile missile,
            Unit? targetUnit,
            FactionAffinity ownerFaction,
            bool startPassiveHoj,
            bool verticalLaunch = false,
            bool isAirDropped = false)
        {
            Package = package;
            Spec = spec;
            Missile = missile;
            TargetUnit = targetUnit;
            OwnerFaction = ownerFaction;
            PassiveHojActive = startPassiveHoj && spec.SupportsActiveToPassiveHoj;
            launchedVertically = verticalLaunch;
            Vector3 fwd = missile.transform.forward;
            initialLaunchDir = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.up;
            maxFlightLifetimeSeconds = spec.IsAntiShip
                ? Mathf.Clamp(Mathf.Max(110f, spec.MaxEngagementRangeKm * 5.6f), 110f, 380f)
                : Mathf.Clamp(Mathf.Max(25f, spec.MaxEngagementRangeKm * 2.0f), 25f, 150f);
            weaveSeed = (missile.persistentID.Id % 17) * 0.43f;
            if (PassiveHojActive && targetUnit != null && !targetUnit.disabled)
            {
                lastKnownHojPos = targetUnit.transform.position;
                lastKnownHojVel = targetUnit.rb != null ? targetUnit.rb.velocity : Vector3.zero;
                lastKnownHojTime = Time.time;
                hasLastKnownHoj = true;
            }

            if (GameInteropHelpers.MissileBlastYieldField != null && spec.BlastYield > 0f)
            {
                try
                {
                    GameInteropHelpers.MissileBlastYieldField.SetValue(missile, spec.BlastYield);
                }
                catch
                {
                    // ignored
                }
            }

            ConfigureSeeker();
            ExtendMotorForSpecRange();
            AttachCustomVisualRig();

            if (isAirDropped &&
                spec.FlightProfile == MissileFlightProfile.SeaSkimmingEvasive)
            {
                float seaY = Datum.LocalSeaY;
                float groundY = Mathf.Max(seaY, SampleTerrainY(missile.transform.position.x, missile.transform.position.z, seaY));
                float startClearance = missile.transform.position.y - groundY;
                airDropGlideActive = true;
                airDropMotorIgnited = false;
                airDropGlideStartSpeed = Mathf.Max(
                    95f,
                    missile.rb != null
                        ? missile.rb.velocity.magnitude
                        : (missile.startingVelocity.magnitude > 1f ? missile.startingVelocity.magnitude : 210f));
                HoldFirstMotorDelay(0.45f, muteStartupAudio: true);

                if (!missile.remoteSim && targetUnit != null && !targetUnit.disabled)
                {
                    PrecomputeSeaWraithRoute(missile.transform.position, targetUnit.transform.position);
                    if (startClearance <= 35f)
                    {
                        AdjustLaunchPitchForImmediateTerrain(missile.transform.position);
                    }
                }
            }
            else if (spec.FlightProfile == MissileFlightProfile.SeaSkimmingEvasive &&
                !missile.remoteSim &&
                targetUnit != null &&
                !targetUnit.disabled)
            {
                PrecomputeSeaWraithRoute(missile.transform.position, targetUnit.transform.position);
                AdjustLaunchPitchForImmediateTerrain(missile.transform.position);
            }

            Missile.SetAimpoint(
                GameInteropHelpers.WorldToGlobal(missile.transform.position + (initialLaunchDir * 3000f)),
                Vector3.zero);

            if (spec.StealthRcsOverride > 0f)
            {
                missile.RCS = spec.StealthRcsOverride;
            }
            else if (spec.IsDecoy || spec.FlightProfile == MissileFlightProfile.Decoy)
            {
                // Decoy missiles simulate large bomber/cruise RCS to saturate defences.
                missile.RCS = 6.5f;
            }

            CustomMissileTracker.Register(this);
            missile.onDisableUnit += OnMissileDisabled;

            prevPos = missile.transform.position;
            prevTargetPos = targetUnit != null ? targetUnit.transform.position : prevPos;
            hasPrevPositions = targetUnit != null;

            if (MdsConfig.EnableAbmEarlyWarning.Value &&
                !spec.IsAbmInterceptor &&
                !spec.IsAntiShip &&
                spec.BlastYield > MdsConfig.AbmBlastYieldThreshold.Value)
            {
                bool hasBallisticProfile = spec.FlightProfile == MissileFlightProfile.Ballistic ||
                                           spec.FlightProfile == MissileFlightProfile.MarvTerminal ||
                                           missile.GetComponent<BallisticMissileGuidance>() != null;
                AbmEarlyWarningService.Instance?.NotifyPotentialBallisticLaunch(missile, hasBallisticProfile);
            }
        }

        internal void InitializeClientVisualOnly(
            ContractorPackage package,
            CustomMissileSpec spec,
            Missile missile)
        {
            Package = package;
            Spec = spec;
            Missile = missile;
            AttachCustomVisualRig();

            if (spec.FlightProfile == MissileFlightProfile.SeaSkimmingEvasive &&
                ((missile.ownerID.IsValid && missile.ownerID.TryGetUnit(out Unit ownerUnit) && ownerUnit is Aircraft) ||
                 (missile.transform.position.y - Datum.LocalSeaY) > 35f))
            {
                airDropGlideActive = true;
                airDropMotorIgnited = false;
                HoldFirstMotorDelay(0.45f, muteStartupAudio: true);
            }

            prevVisualPos = missile.transform.position;
            hasPrevVisualPos = true;
            CustomMissileTracker.Register(this);
            missile.onDisableUnit -= OnMissileDisabled;
            missile.onDisableUnit += OnMissileDisabled;
        }

        internal void TriggerFinDeployment()
        {
            if (finsDeployed)
            {
                return;
            }

            finsDeployed = true;
            animator?.SetTargetDeployed(true);
        }

        private void ConfigureSeeker()
        {
            MissileSeeker? seeker = GameInteropHelpers.SafeGetSeeker(Missile);
            if (seeker is ARHSeeker arh)
            {
                if (GameInteropHelpers.ArhHomeOnJamField != null)
                {
                    GameInteropHelpers.ArhHomeOnJamField.SetValue(arh, Spec.SupportsActiveToPassiveHoj);
                }

                if (GameInteropHelpers.ArhJamToleranceField != null)
                {
                    float tol = Spec.IsAntiShip
                        ? Mathf.Clamp01(Spec.SeekerEccmJamTolerance)
                        : (Spec.SupportsActiveToPassiveHoj ? 0.50f : 0.35f);
                    GameInteropHelpers.ArhJamToleranceField.SetValue(arh, tol);
                }

                if (GameInteropHelpers.ArhLoftAmountField != null &&
                    Spec.FlightProfile == MissileFlightProfile.Lofting)
                {
                    GameInteropHelpers.ArhLoftAmountField.SetValue(arh, 0.35f);
                }

                GameInteropHelpers.ArhMaxDatalinkAngleField?.SetValue(arh, 180f);
                GameInteropHelpers.ArhMaxTrackingAngleField?.SetValue(arh, 115f);
                GameInteropHelpers.ArhSelfDestructSpeedField?.SetValue(arh, 75f);
                GameInteropHelpers.ArhGuidanceDelayField?.SetValue(arh, launchedVertically ? 0.80f : 0.45f);
                GameInteropHelpers.ArhArmDelayField?.SetValue(arh, 1.1f);

                if (TargetUnit != null && !TargetUnit.disabled)
                {
                    Vector3 targetVel = TargetUnit.rb != null ? TargetUnit.rb.velocity : Vector3.zero;
                    float initDist = Vector3.Distance(Missile.transform.position, TargetUnit.transform.position);
                    GameInteropHelpers.SeekerTargetUnitField?.SetValue(arh, TargetUnit);
                    GameInteropHelpers.ArhKnownPosField?.SetValue(arh, GameInteropHelpers.WorldToGlobal(TargetUnit.transform.position));
                    GameInteropHelpers.ArhKnownVelField?.SetValue(arh, targetVel);
                    GameInteropHelpers.ArhTargetDistField?.SetValue(arh, initDist);
                    GameInteropHelpers.ArhTimeToTargetField?.SetValue(arh, initDist / Mathf.Max(400f, Spec.CruiseSpeedMps));
                }
            }
            else if (seeker is OpticalSeekerCruiseMissile optical)
            {
                if (Spec.IsAntiShip)
                {
                    GameInteropHelpers.OpticalCruiseAltTargetField?.SetValue(optical, Mathf.Max(2.5f, Spec.SeaSkimAltitudeMeters));
                    GameInteropHelpers.OpticalCruiseTermRangeField?.SetValue(optical, 4200f);
                    GameInteropHelpers.OpticalCruiseFinDelayField?.SetValue(optical, Spec.FinDeployDelaySeconds);
                    GameInteropHelpers.OpticalCruiseGuidanceDelayField?.SetValue(optical, 0.35f);
                }

                if (TargetUnit != null && !TargetUnit.disabled)
                {
                    GameInteropHelpers.SeekerTargetUnitField?.SetValue(optical, TargetUnit);
                }
            }

            if (PassiveHojActive)
            {
                Missile.NetworkseekerMode = Missile.SeekerMode.passive;
            }
        }

        private float GetEffectiveMaxSpeedMps()
        {
            if (Spec == null)
            {
                return 0f;
            }

            if (Spec.MaxSpeedMps > 0f)
            {
                return Spec.MaxSpeedMps;
            }

            if (Spec.TargetMach > 0f)
            {
                return Mathf.Max(310f, Spec.TargetMach * 340.3f * 1.06f);
            }

            return Spec.CruiseSpeedMps > 0f ? Mathf.Max(400f, Spec.CruiseSpeedMps) : 0f;
        }

        private void ExtendMotorForSpecRange()
        {
            if (GameInteropHelpers.MissileMotorsField?.GetValue(Missile) is not Array motors || motors.Length == 0)
            {
                return;
            }

            Type? motorType = motors.GetValue(0)?.GetType();
            if (motorType == null)
            {
                return;
            }

            FieldInfo? burnTimeField = motorType.GetField("burnTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? fuelMassField = motorType.GetField("fuelMass", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? burnRateField = motorType.GetField("burnRate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? topSpeedField = motorType.GetField("topSpeed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (burnTimeField == null)
            {
                return;
            }

            float totalBurn = 0f;
            for (int i = 0; i < motors.Length; i++)
            {
                object? motorObj = motors.GetValue(i);
                if (motorObj != null && burnTimeField.GetValue(motorObj) is float bt && bt > 0f)
                {
                    totalBurn += bt;
                }
            }

            float desiredBurn = Spec.IsAntiShip
                ? Mathf.Clamp(Spec.MaxEngagementRangeKm * 4.8f, 60f, 320f)
                : Mathf.Clamp(Spec.MaxEngagementRangeKm * 0.35f, 8f, 30f);
            float scale = totalBurn > 0.1f && totalBurn < desiredBurn ? desiredBurn / totalBurn : 1f;
            float maxSpeedCap = GetEffectiveMaxSpeedMps();

            for (int i = 0; i < motors.Length; i++)
            {
                object? motorObj = motors.GetValue(i);
                if (motorObj == null)
                {
                    continue;
                }

                if (scale > 1.01f && burnTimeField.GetValue(motorObj) is float bt && bt > 0f)
                {
                    float newBt = bt * scale;
                    burnTimeField.SetValue(motorObj, newBt);
                    if (burnRateField != null && fuelMassField != null &&
                        burnRateField.GetValue(motorObj) is float br && br > 0f &&
                        fuelMassField.GetValue(motorObj) is float fm && fm > 0f)
                    {
                        burnRateField.SetValue(motorObj, fm / newBt);
                    }
                }

                if (topSpeedField != null && maxSpeedCap > 0f)
                {
                    topSpeedField.SetValue(motorObj, maxSpeedCap);
                }
            }
        }

        private void AttachCustomVisualRig()
        {
            if (!MdsConfig.EnableCustomGlbVisuals.Value)
            {
                return;
            }

            string? resolvedGlb = ResolveAssetPath(Package, Spec.GlbRelativePath, Spec.FallbackGlbRelativePaths);
            string? resolvedDecal = ResolveAssetPath(Package, Spec.DecalRelativePath, Array.Empty<string>());

            MeshRenderer[] existingMeshes = Missile.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < existingMeshes.Length; i++)
            {
                existingMeshes[i].enabled = false;
            }

            SkinnedMeshRenderer[] existingSkinned = Missile.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < existingSkinned.Length; i++)
            {
                existingSkinned[i].enabled = false;
            }

            rig = GlbRuntimeLoader.Instantiate(
                Spec.MissileId,
                resolvedGlb,
                Spec.FallbackMeshArchetype,
                resolvedDecal,
                Missile.transform,
                Spec.ModelYawOffsetDeg);

            if (rig != null)
            {
                rigBaseLocalRot = rig.RootObject.transform.localRotation;
                aoaPitchSign = (Quaternion.Euler(5f, 0f, 0f) * Vector3.forward).y >= 0f ? 1f : -1f;
                bankRollSign = (Quaternion.Euler(0f, 0f, 5f) * Vector3.right).y >= 0f ? -1f : 1f;
                animator = rig.RootObject.AddComponent<GlbKeyframedAnimator>();
                animator.Initialize(rig, desiredDurationSeconds: 0.75f, startDeployed: !Spec.EnableFoldingFins);
                finsDeployed = !Spec.EnableFoldingFins;
            }
        }

        internal void RunCruiseSeekerHousekeeping()
        {
            if (Missile == null || Missile.disabled)
            {
                return;
            }

            if (!aeroFinsDeployed && ageSeconds >= Spec.FinDeployDelaySeconds)
            {
                aeroFinsDeployed = true;
                Missile.DeployFins();
                TriggerFinDeployment();
            }

            if (!Missile.IsTangible() && ageSeconds >= 1.8f)
            {
                Missile.SetTangible(tangible: true);
            }

            Missile.UpdateRadarAlt();

            if (!Missile.IsArmed() && ageSeconds >= 2.2f)
            {
                Missile.Arm();
            }
        }

        private void FixedUpdate()
        {
            if (Missile == null || Missile.disabled)
            {
                return;
            }

            float dt = Time.fixedDeltaTime;
            ageSeconds += dt;

            if (Spec.EnableFoldingFins && !finsDeployed && ageSeconds >= Spec.FinDeployDelaySeconds)
            {
                TriggerFinDeployment();
            }

            if (Spec.BoosterDropSeconds > 0f && !boosterDropped && ageSeconds >= Spec.BoosterDropSeconds)
            {
                boosterDropped = true;
                DetachBoosterStage();
            }

            animator?.TickAnimation(dt, allowTurretOverrides: false);

            if (Spec.IsAntiShip && Spec.TargetMach > 0f &&
                ageSeconds > (Spec.BoosterDropSeconds > 0f ? Spec.BoosterDropSeconds * 0.7f : 1.0f))
            {
                float targetMps = Spec.TargetMach * 340.3f;
                float throttle = Mathf.Clamp(0.55f + ((targetMps - Missile.speed) * 0.025f), 0.12f, 1.0f);
                Missile.SetThrottle(throttle);
            }

            if (Missile.remoteSim)
            {
                if (airDropGlideActive && !airDropMotorIgnited)
                {
                    float clientHeight = Missile.transform.position.y - Datum.LocalSeaY;
                    if (ageSeconds >= 0.85f && (clientHeight <= 24f || ageSeconds >= 18f))
                    {
                        airDropMotorIgnited = true;
                        airDropGlideActive = false;
                        ReleaseFirstMotorDelay();
                    }
                    else
                    {
                        HoldFirstMotorDelay(0.25f, muteStartupAudio: true);
                    }
                }

                UpdateFlightControlSurfaceAnimation(dt);
                return;
            }

            hojUpdatedAimThisTick = false;
            UpdateTargetReference();

            if (Spec.HasActiveJammer && Time.time >= nextJamPulseTime)
            {
                nextJamPulseTime = Time.time + Mathf.Max(0.25f, Spec.ActiveJammerPulseIntervalSeconds);
                PulseActiveDirectionalJammer();
            }

            if (airDropGlideActive && !airDropMotorIgnited)
            {
                UpdateAirDropGlideState(dt);
            }
            else
            {
                float climbDuration = Spec.IsAntiShip
                    ? (Spec.BoosterDropSeconds > 0f ? 1.55f : 0.95f)
                    : (launchedVertically ? 0.80f : 0.45f);
                if (!airDropMotorIgnited && ageSeconds < climbDuration)
                {
                    Missile.SetAimpoint(
                        GameInteropHelpers.WorldToGlobal(Missile.transform.position + (initialLaunchDir * 3000f)),
                        Vector3.zero);
                }
                else
                {
                    UpdateActivePassiveHoj(dt);
                    UpdateActiveTargetDatalink();
                    UpdateFlightProfileGuidance(dt);
                }
            }

            EnforceSpeedLimit(dt);
            UpdateFlightControlSurfaceAnimation(dt);

            if (Spec.IsAbmInterceptor)
            {
                CheckAbmIntercept();
            }

            prevPos = Missile.transform.position;
            if (TargetUnit != null && !TargetUnit.disabled)
            {
                prevTargetPos = TargetUnit.transform.position;
                hasPrevPositions = true;
            }
            else
            {
                hasPrevPositions = false;
            }
        }

        private void EnsureMotorReflectionFields(Type motorType)
        {
            motorDelayTimerField ??= motorType.GetField("delayTimer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            motorStartupSourceField ??= motorType.GetField("startupSource", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private void HoldFirstMotorDelay(float holdSeconds, bool muteStartupAudio)
        {
            try
            {
                if (GameInteropHelpers.MissileMotorsField?.GetValue(Missile) is not Array motors || motors.Length == 0)
                {
                    return;
                }

                object? firstMotor = motors.GetValue(0);
                if (firstMotor == null)
                {
                    return;
                }

                EnsureMotorReflectionFields(firstMotor.GetType());
                motorDelayTimerField?.SetValue(firstMotor, holdSeconds);

                if (muteStartupAudio && motorStartupSourceField?.GetValue(firstMotor) is AudioSource src && src != null)
                {
                    if (!startupAudioMuted)
                    {
                        cachedStartupAudio = src;
                        cachedStartupAudioVol = src.volume;
                        startupAudioMuted = true;
                    }

                    src.volume = 0f;
                    if (src.isPlaying)
                    {
                        src.Stop();
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        private void ReleaseFirstMotorDelay()
        {
            try
            {
                if (startupAudioMuted && cachedStartupAudio != null)
                {
                    cachedStartupAudio.volume = cachedStartupAudioVol;
                }

                startupAudioMuted = false;

                if (GameInteropHelpers.MissileMotorsField?.GetValue(Missile) is not Array motors || motors.Length == 0)
                {
                    return;
                }

                object? firstMotor = motors.GetValue(0);
                if (firstMotor == null)
                {
                    return;
                }

                EnsureMotorReflectionFields(firstMotor.GetType());
                motorDelayTimerField?.SetValue(firstMotor, 0f);
            }
            catch
            {
                // ignored
            }
        }

        private void UpdateAirDropGlideState(float dt)
        {
            Vector3 myPos = Missile.transform.position;
            float seaY = Datum.LocalSeaY;
            float seaSkimFloor = seaY + Mathf.Max(2.6f, Spec.SeaSkimAltitudeMeters);
            float localTerrainY = SampleTerrainY(myPos.x, myPos.z, seaY);
            float localFloorY = localTerrainY > seaY + 0.5f
                ? localTerrainY + LandTreeClearanceMeters
                : seaSkimFloor;

            float heightAboveFloor = myPos.y - localFloorY;
            float currentSpeed = Missile.rb != null ? Missile.rb.velocity.magnitude : airDropGlideStartSpeed;
            float distToTarget = TargetUnit != null && !TargetUnit.disabled
                ? Vector3.Distance(myPos, TargetUnit.transform.position)
                : float.MaxValue;

            bool shouldIgnite = ageSeconds >= 0.85f &&
                                (heightAboveFloor <= 24f ||
                                 currentSpeed <= 165f ||
                                 distToTarget <= 2400f ||
                                 ageSeconds >= 38f);

            if (shouldIgnite)
            {
                airDropMotorIgnited = true;
                airDropGlideActive = false;
                ReleaseFirstMotorDelay();
                UpdateActivePassiveHoj(dt);
                UpdateActiveTargetDatalink();
                UpdateFlightProfileGuidance(dt);
                return;
            }

            HoldFirstMotorDelay(0.25f, muteStartupAudio: true);
            UpdateActiveTargetDatalink();

            if (ageSeconds < 0.85f)
            {
                // Clean unpowered free-fall separation away from the launching aircraft before wing glide starts.
                Vector3 dropAim = myPos + (initialLaunchDir * 2500f) + (Vector3.down * 350f);
                Missile.SetAimpoint(GameInteropHelpers.WorldToGlobal(dropAim), Vector3.zero);
                return;
            }

            SteerAirDropGlide(dt, myPos, seaY, localFloorY, currentSpeed);
        }

        private void SteerAirDropGlide(float dt, Vector3 myPos, float seaY, float localFloorY, float currentSpeed)
        {
            if (Missile.rb == null)
            {
                return;
            }

            Vector3 horizAim = myPos + (initialLaunchDir * 3000f);
            float targetTerrainAheadY = localFloorY;

            if (TargetUnit != null && !TargetUnit.disabled)
            {
                Vector3 targetPos = TargetUnit.transform.position;
                Vector3 targetVel = TargetUnit.rb != null ? TargetUnit.rb.velocity : Vector3.zero;
                float dist = Vector3.Distance(myPos, targetPos);
                float tti = Mathf.Clamp(dist / Mathf.Max(180f, currentSpeed), 0.05f, 8.0f);
                Vector3 routeAim = targetPos + (targetVel * (tti * 0.75f));
                UpdateSeaSkimmingEvasiveProfile(myPos, targetPos, targetVel, dist, tti, dt, ref routeAim);
                horizAim = routeAim;
                targetTerrainAheadY = Mathf.Max(localFloorY, routeAim.y);
            }

            Vector3 flatDelta = new Vector3(horizAim.x - myPos.x, 0f, horizAim.z - myPos.z);
            float flatDist = flatDelta.magnitude;
            Vector3 flatDir = flatDist > 1f
                ? flatDelta / flatDist
                : new Vector3(initialLaunchDir.x, 0f, initialLaunchDir.z).normalized;
            if (flatDir.sqrMagnitude < 0.01f)
            {
                flatDir = Vector3.forward;
            }

            float seaSkimFloor = seaY + Mathf.Max(2.6f, Spec.SeaSkimAltitudeMeters);
            float glideHeight = myPos.y - targetTerrainAheadY;

            // Smooth aerodynamic glide descent toward 10 ft sea-skim (up to -13 deg above high altitude, flaring near surface, or +10 deg if hill ahead).
            float desiredPitchDeg;
            if (glideHeight < -2f)
            {
                desiredPitchDeg = Mathf.Clamp((-glideHeight / 32f) * 10f, 1.5f, 10f);
            }
            else if (glideHeight <= 65f)
            {
                desiredPitchDeg = -Mathf.Lerp(0.5f, 6.5f, Mathf.Clamp01(glideHeight / 65f));
            }
            else
            {
                desiredPitchDeg = -Mathf.Lerp(6.5f, 13.0f, Mathf.Clamp01((glideHeight - 65f) / 380f));
            }

            float pitchRad = desiredPitchDeg * Mathf.Deg2Rad;
            Vector3 desiredDir = ((flatDir * Mathf.Cos(pitchRad)) + (Vector3.up * Mathf.Sin(pitchRad))).normalized;

            if (Time.time - lastProfileAimUpdate >= 0.12f)
            {
                lastProfileAimUpdate = Time.time;
                Missile.SetAimpoint(GameInteropHelpers.WorldToGlobal(myPos + (desiredDir * 2400f)), Vector3.zero);
            }

            Vector3 vel = Missile.rb.velocity;
            float speed = vel.magnitude;
            if (speed < 35f)
            {
                return;
            }

            Vector3 prevDir = vel / speed;
            float turnRate = emergencyPullUpThisTick ? 3.2f : 1.05f;
            Vector3 newDir = Vector3.RotateTowards(prevDir, desiredDir, dt * turnRate, 0f);

            float aheadTerrainY = SampleTerrainY(myPos.x + (newDir.x * 160f), myPos.z + (newDir.z * 160f), seaY);
            float safeFloorY = aheadTerrainY > seaY + 0.5f ? aheadTerrainY + LandTreeClearanceMeters : seaSkimFloor;
            Vector3 nextCheckPos = myPos + (newDir * (speed * Mathf.Max(dt, 0.25f)));
            if (nextCheckPos.y < safeFloorY)
            {
                float neededDy = Mathf.Clamp((safeFloorY - myPos.y) / Mathf.Max(45f, speed * 0.65f), 0.02f, 0.45f);
                newDir.y = Mathf.Max(neededDy, newDir.y);
                newDir.Normalize();
            }

            float sinFlight = Mathf.Clamp(newDir.y, -0.5f, 0.5f);
            float glideAccel = (-Physics.gravity.magnitude * sinFlight) - (speed * speed * 0.00018f);
            float nextSpeed = Mathf.Clamp(speed + (glideAccel * dt), 162f, Mathf.Max(airDropGlideStartSpeed, 295f));

            Vector3 prevFlat = new Vector3(prevDir.x, 0f, prevDir.z);
            Vector3 newFlat = new Vector3(newDir.x, 0f, newDir.z);
            float signedYawRateDeg = (prevFlat.sqrMagnitude > 0.001f && newFlat.sqrMagnitude > 0.001f && dt > 0.0001f)
                ? Vector3.SignedAngle(prevFlat.normalized, newFlat.normalized, Vector3.up) / dt
                : 0f;
            float targetBankDeg = Mathf.Clamp(signedYawRateDeg * 1.15f, -40f, 40f);
            currentBankDeg = Mathf.Lerp(currentBankDeg, targetBankDeg, 1f - Mathf.Exp(-dt * 6.0f));

            Missile.rb.velocity = newDir * nextSpeed;
            Missile.rb.MoveRotation(Quaternion.LookRotation(newDir, Vector3.up) * Quaternion.Euler(0f, 0f, currentBankDeg * bankRollSign));
        }

        private void EnforceSpeedLimit(float dt)
        {
            if (Missile == null || Missile.rb == null || Spec == null)
            {
                return;
            }

            float effectiveCap = GetEffectiveMaxSpeedMps();
            if (effectiveCap <= 0f)
            {
                return;
            }

            if (!Spec.IsAntiShip && Spec.MaxMach > 0f)
            {
                float altMeters = Mathf.Max(0f, Missile.transform.position.y - Datum.LocalSeaY);
                float speedOfSound = Mathf.Max(290f, LevelInfo.GetSpeedOfSound(altMeters));
                effectiveCap = Mathf.Min(effectiveCap, Spec.MaxMach * speedOfSound);
            }

            Vector3 vel = Missile.rb.velocity;
            float speed = vel.magnitude;

            if (!Spec.IsAntiShip && Missile.EngineOn())
            {
                float throttle = speed >= effectiveCap * 0.98f
                    ? 0.05f
                    : (speed >= effectiveCap * 0.92f ? 0.45f : 1.0f);
                Missile.SetThrottle(throttle);
            }

            if (speed > effectiveCap && speed > 1f)
            {
                float dampedSpeed = Mathf.MoveTowards(speed, effectiveCap, dt * 450f);
                Missile.rb.velocity = vel * (dampedSpeed / speed);
            }
        }

        internal bool ShouldSuppressPrematureSelfDestruct(bool hitArmor, bool hitTerrain)
        {
            if (hitArmor || hitTerrain || interceptedTarget || Spec == null || Missile == null)
            {
                return false;
            }

            if (GameInteropHelpers.SafeGetHitpoints(Missile) <= 0f)
            {
                return false;
            }

            if (Missile.transform.position.y < Datum.LocalSeaY)
            {
                return false;
            }

            if (ageSeconds >= maxFlightLifetimeSeconds)
            {
                return false;
            }

            if (airDropGlideActive)
            {
                return true;
            }

            if (TargetUnit != null && !TargetUnit.disabled)
            {
                if (Vector3.Distance(Missile.transform.position, TargetUnit.transform.position) <= 220f)
                {
                    return false;
                }

                if (ageSeconds < 10.0f || Missile.EngineOn() || Missile.speed >= 85f)
                {
                    return true;
                }
            }
            else if (PassiveHojActive && hasLastKnownHoj)
            {
                if (Vector3.Distance(Missile.transform.position, lastKnownHojPos) <= 220f)
                {
                    return false;
                }

                if (ageSeconds < 10.0f || Missile.EngineOn() || Missile.speed >= 85f)
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdateActiveTargetDatalink()
        {
            if (PassiveHojActive || TargetUnit == null || TargetUnit.disabled || GameInteropHelpers.IsAircraftSplashed(TargetUnit))
            {
                return;
            }

            if (Time.time - lastDatalinkRefresh < 0.25f)
            {
                return;
            }

            lastDatalinkRefresh = Time.time;
            if (Missile.targetID.NotValid)
            {
                Missile.SetTarget(TargetUnit);
            }

            if (GameInteropHelpers.SafeGetSeeker(Missile) is ARHSeeker arh)
            {
                bool locked = GameInteropHelpers.ArhRadarLockField?.GetValue(arh) is bool rle && rle;
                if (!locked)
                {
                    Vector3 vel = TargetUnit.rb != null ? TargetUnit.rb.velocity : Vector3.zero;
                    GameInteropHelpers.SeekerTargetUnitField?.SetValue(arh, TargetUnit);
                    GameInteropHelpers.ArhKnownPosField?.SetValue(arh, GameInteropHelpers.WorldToGlobal(TargetUnit.transform.position));
                    GameInteropHelpers.ArhKnownVelField?.SetValue(arh, vel);
                }
            }
            else if (Spec.IsAntiShip && GameInteropHelpers.SafeGetSeeker(Missile) is MissileSeeker seeker)
            {
                if (GameInteropHelpers.SeekerTargetUnitField?.GetValue(seeker) is not Unit currentSeekerTarget || currentSeekerTarget.disabled)
                {
                    GameInteropHelpers.SeekerTargetUnitField?.SetValue(seeker, TargetUnit);
                }
            }
        }

        private void UpdateTargetReference()
        {
            if (Spec.IsAntiShip && TargetUnit != null && TargetUnit is not Ship)
            {
                TargetUnit = null;
            }

            if (TargetUnit != null && !TargetUnit.disabled)
            {
                return;
            }

            if (Missile.targetID.IsValid && Missile.targetID.TryGetUnit(out Unit resolved) && !resolved.disabled)
            {
                if (Spec.IsAntiShip && resolved is not Ship)
                {
                    return;
                }

                TargetUnit = resolved;
            }
        }

        private void UpdateActivePassiveHoj(float dt)
        {
            if (!Spec.SupportsActiveToPassiveHoj)
            {
                return;
            }

            bool jammerDetected = false;
            Vector3 jammerPos = Vector3.zero;
            Vector3 jammerVel = Vector3.zero;
            const float onboardPassiveRfRangeSq = 15000f * 15000f;

            if (SensorNetworkManager.Instance != null)
            {
                IReadOnlyList<HojTrackEntry> tracks = SensorNetworkManager.Instance.ActiveHojTracks;
                float maxRangeSq = Mathf.Pow(Mathf.Max(15000f, Spec.MaxEngagementRangeKm * 1200f), 2f);
                Vector3 myPos = Missile.transform.position;
                Vector3 myFwd = Missile.transform.forward;

                for (int i = 0; i < tracks.Count; i++)
                {
                    HojTrackEntry tr = tracks[i];
                    if (tr.Faction != OwnerFaction || tr.JammerUnit == null || tr.JammerUnit.disabled)
                    {
                        continue;
                    }

                    if (Spec.IsAntiShip && tr.JammerUnit is not Ship)
                    {
                        continue;
                    }

                    if (!GameInteropHelpers.IsHostileToAffinity(tr.JammerUnit, OwnerFaction, Missile.NetworkHQ ?? Missile.MapHQ))
                    {
                        continue;
                    }

                    if (ReferenceEquals(TargetUnit, tr.JammerUnit))
                    {
                        jammerDetected = true;
                        bool onboardPassive = (tr.JammerUnit.transform.position - myPos).sqrMagnitude <= onboardPassiveRfRangeSq;
                        jammerPos = onboardPassive ? tr.JammerUnit.transform.position : tr.TriangulatedWorldPos;
                        jammerVel = onboardPassive
                            ? (tr.JammerUnit.rb != null ? tr.JammerUnit.rb.velocity : Vector3.zero)
                            : tr.Velocity;
                        break;
                    }

                    if (TargetUnit == null)
                    {
                        Vector3 toJammer = tr.TriangulatedWorldPos - myPos;
                        if (toJammer.sqrMagnitude <= maxRangeSq &&
                            Vector3.Angle(myFwd, toJammer) <= 60f)
                        {
                            jammerDetected = true;
                            bool onboardPassive = (tr.JammerUnit.transform.position - myPos).sqrMagnitude <= onboardPassiveRfRangeSq;
                            jammerPos = onboardPassive ? tr.JammerUnit.transform.position : tr.TriangulatedWorldPos;
                            jammerVel = onboardPassive
                                ? (tr.JammerUnit.rb != null ? tr.JammerUnit.rb.velocity : Vector3.zero)
                                : tr.Velocity;
                            TargetUnit = tr.JammerUnit;
                            Missile.SetTarget(tr.JammerUnit);

                            Transform? part = tr.JammerUnit.GetRandomPart();
                            if (part != null && tr.JammerUnit.rb != null)
                            {
                                Missile.SetProxyFuse(part, tr.JammerUnit.rb);
                            }

                            if (GameInteropHelpers.SafeGetSeeker(Missile) is ARHSeeker seekerRef)
                            {
                                GameInteropHelpers.SeekerTargetUnitField?.SetValue(seekerRef, tr.JammerUnit);
                                GameInteropHelpers.ArhKnownPosField?.SetValue(seekerRef, GameInteropHelpers.WorldToGlobal(jammerPos));
                                GameInteropHelpers.ArhKnownVelField?.SetValue(seekerRef, jammerVel);
                                GameInteropHelpers.ArhRadarLockField?.SetValue(seekerRef, false);
                            }

                            break;
                        }
                    }
                }
            }

            if (!jammerDetected && GameInteropHelpers.SafeGetSeeker(Missile) is ARHSeeker arh &&
                GameInteropHelpers.ArhIsJammedField != null)
            {
                try
                {
                    object? jammed = GameInteropHelpers.ArhIsJammedField.GetValue(arh);
                    if (jammed is bool b && b && TargetUnit != null && !TargetUnit.disabled)
                    {
                        jammerDetected = true;
                        jammerPos = TargetUnit.transform.position;
                        jammerVel = TargetUnit.rb != null ? TargetUnit.rb.velocity : Vector3.zero;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            IsTargetActivelyJamming = jammerDetected;

            if (jammerDetected)
            {
                lastKnownHojPos = jammerPos;
                lastKnownHojVel = jammerVel;
                lastKnownHojTime = Time.time;
                hasLastKnownHoj = true;

                if (!PassiveHojActive)
                {
                    PassiveHojActive = true;
                    Missile.NetworkseekerMode = Missile.SeekerMode.passive;
                    MdsLog.Debug($"[HOJ] '{Spec.MissileId}' switched Active -> Passive Home-on-Jam.");
                }
                else if (Missile.seekerMode != Missile.SeekerMode.passive)
                {
                    Missile.NetworkseekerMode = Missile.SeekerMode.passive;
                }

                if (TargetUnit != null && !TargetUnit.disabled && Missile.targetID.NotValid)
                {
                    Missile.SetTarget(TargetUnit);
                }

                if (GameInteropHelpers.SafeGetSeeker(Missile) is ARHSeeker arhSeeker)
                {
                    if (TargetUnit != null && !TargetUnit.disabled)
                    {
                        GameInteropHelpers.SeekerTargetUnitField?.SetValue(arhSeeker, TargetUnit);
                    }

                    GameInteropHelpers.ArhKnownPosField?.SetValue(arhSeeker, GameInteropHelpers.WorldToGlobal(jammerPos));
                    GameInteropHelpers.ArhKnownVelField?.SetValue(arhSeeker, jammerVel);
                    GameInteropHelpers.ArhRadarLockField?.SetValue(arhSeeker, false);
                    hojUpdatedAimThisTick = true;
                }
                else if (Spec.FlightProfile != MissileFlightProfile.SeaSkimmingEvasive)
                {
                    hojUpdatedAimThisTick = true;
                    if (Time.time - lastHojAimUpdate >= 0.25f)
                    {
                        lastHojAimUpdate = Time.time;
                        Missile.SetAimpoint(GameInteropHelpers.WorldToGlobal(jammerPos), jammerVel);
                    }
                }
            }
            else if (PassiveHojActive)
            {
                bool hasLiveTarget = TargetUnit != null && !TargetUnit.disabled;
                float distToTarget = hasLiveTarget
                    ? Vector3.Distance(Missile.transform.position, TargetUnit!.transform.position)
                    : float.MaxValue;
                bool hqTracked = hasLiveTarget &&
                                 Missile.NetworkHQ != null &&
                                 Missile.NetworkHQ.IsTargetBeingTracked(TargetUnit!);

                if (hasLiveTarget && (distToTarget <= 16000f || hqTracked))
                {
                    // Enter active terminal lock once within autonomous ARH range or back on HQ radar.
                    PassiveHojActive = false;
                    Missile.NetworkseekerMode = Missile.SeekerMode.activeLock;
                    if (GameInteropHelpers.SafeGetSeeker(Missile) is ARHSeeker arhTerm)
                    {
                        Vector3 tPos = TargetUnit!.transform.position;
                        Vector3 tVel = TargetUnit.rb != null ? TargetUnit.rb.velocity : Vector3.zero;
                        GameInteropHelpers.SeekerTargetUnitField?.SetValue(arhTerm, TargetUnit);
                        GameInteropHelpers.ArhKnownPosField?.SetValue(arhTerm, GameInteropHelpers.WorldToGlobal(tPos));
                        GameInteropHelpers.ArhKnownVelField?.SetValue(arhTerm, tVel);
                    }
                }
                else if (hasLastKnownHoj && (Time.time - lastKnownHojTime) <= 22f)
                {
                    // Coast inertially along last observed jammer velocity so seeker never drops blind targets on brief jam pauses.
                    lastKnownHojPos += lastKnownHojVel * dt;
                    if (Missile.seekerMode != Missile.SeekerMode.passive)
                    {
                        Missile.NetworkseekerMode = Missile.SeekerMode.passive;
                    }

                    if (GameInteropHelpers.SafeGetSeeker(Missile) is ARHSeeker arhCoast)
                    {
                        GameInteropHelpers.ArhKnownPosField?.SetValue(arhCoast, GameInteropHelpers.WorldToGlobal(lastKnownHojPos));
                        GameInteropHelpers.ArhKnownVelField?.SetValue(arhCoast, lastKnownHojVel);
                        GameInteropHelpers.ArhRadarLockField?.SetValue(arhCoast, false);
                        hojUpdatedAimThisTick = true;
                    }
                    else if (Spec.FlightProfile != MissileFlightProfile.SeaSkimmingEvasive &&
                             Time.time - lastHojAimUpdate >= 0.25f)
                    {
                        lastHojAimUpdate = Time.time;
                        Missile.SetAimpoint(GameInteropHelpers.WorldToGlobal(lastKnownHojPos), lastKnownHojVel);
                        hojUpdatedAimThisTick = true;
                    }
                }
                else
                {
                    PassiveHojActive = false;
                    Missile.NetworkseekerMode = Missile.SeekerMode.activeSearch;
                }
            }
        }

        private void UpdateFlightProfileGuidance(float dt)
        {
            if (TargetUnit == null || TargetUnit.disabled || Missile.rb == null)
            {
                return;
            }

            Vector3 myPos = Missile.transform.position;
            Vector3 targetPos = TargetUnit.transform.position;
            Vector3 targetVel = TargetUnit.rb != null ? TargetUnit.rb.velocity : Vector3.zero;
            Vector3 toTarget = targetPos - myPos;
            float dist = toTarget.magnitude;
            if (dist < 1f)
            {
                return;
            }

            float interceptSpeed = Mathf.Max(400f, Spec.CruiseSpeedMps);
            float tti = Mathf.Clamp(dist / interceptSpeed, 0.05f, 8.0f);
            Vector3 predictedPos = targetPos + (targetVel * (tti * 0.75f));

            Vector3 aimPoint = predictedPos;
            switch (Spec.FlightProfile)
            {
                case MissileFlightProfile.Lofting:
                    if (dist > 4500f && myPos.y - Datum.LocalSeaY < Spec.LoftApexMeters)
                    {
                        aimPoint = Vector3.Lerp(myPos, predictedPos, 0.45f) + new Vector3(0f, Mathf.Min(3000f, dist * 0.35f), 0f);
                    }

                    break;

                case MissileFlightProfile.SeaSkimming:
                    if (dist > 2500f)
                    {
                        float baseY = Datum.LocalSeaY;
                        var probeOrigin = new Vector3(
                            predictedPos.x,
                            Mathf.Max(predictedPos.y + 500f, baseY + 2500f),
                            predictedPos.z);
                        if (Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit terrainHit, 5000f, (int)PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                        {
                            baseY = Mathf.Max(baseY, terrainHit.point.y);
                        }

                        float desiredY = baseY + Mathf.Max(10f, Spec.SeaSkimAltitudeMeters);
                        aimPoint = new Vector3(predictedPos.x, desiredY, predictedPos.z);
                    }

                    break;

                case MissileFlightProfile.SeaSkimmingEvasive:
                    UpdateSeaSkimmingEvasiveProfile(myPos, targetPos, targetVel, dist, tti, dt, ref aimPoint);
                    break;

                case MissileFlightProfile.Ballistic:
                    if (Spec.IsAbmInterceptor)
                    {
                        // Lead high-speed ballistic reentry vehicle directly.
                        aimPoint = targetPos + (targetVel * Mathf.Clamp(dist / Math.Max(900f, Spec.CruiseSpeedMps), 0.02f, 6f));
                    }
                    else if (dist > 7000f)
                    {
                        aimPoint = Vector3.Lerp(myPos, predictedPos, 0.5f) + new Vector3(0f, dist * 0.45f, 0f);
                    }

                    break;

                case MissileFlightProfile.MarvTerminal:
                    if (dist < 7500f && dist > 350f)
                    {
                        Vector3 fwd = toTarget / dist;
                        Vector3 side = Vector3.Cross(Vector3.up, fwd);
                        if (side.sqrMagnitude < 0.01f)
                        {
                            side = Vector3.right;
                        }
                        else
                        {
                            side.Normalize();
                        }

                        float weave = Mathf.Sin((ageSeconds * 5.2f) + weaveSeed) * 380f;
                        float verticalJink = Mathf.Cos((ageSeconds * 3.8f) + weaveSeed) * 180f;
                        aimPoint = predictedPos + (side * weave) + (Vector3.up * verticalJink);
                    }

                    break;
            }

            if (!hojUpdatedAimThisTick && Time.time - lastProfileAimUpdate >= 0.12f)
            {
                lastProfileAimUpdate = Time.time;
                Missile.SetAimpoint(GameInteropHelpers.WorldToGlobal(aimPoint), targetVel);
            }

            if (!hojUpdatedAimThisTick && Spec.FlightProfile == MissileFlightProfile.SeaSkimmingEvasive &&
                ageSeconds > (Spec.BoosterDropSeconds > 0f ? 1.55f : 0.95f))
            {
                Vector3 desiredDir = (aimPoint - myPos).normalized;
                float speed = Missile.rb.velocity.magnitude;
                float targetSpeed = Spec.TargetMach > 0f ? Spec.TargetMach * 340.3f : Mathf.Max(280f, Spec.CruiseSpeedMps);
                float nextSpeed = Spec.TargetMach > 0f && ageSeconds > (Spec.BoosterDropSeconds > 0f ? Spec.BoosterDropSeconds : 1.6f)
                    ? Mathf.MoveTowards(speed, targetSpeed, dt * 95f)
                    : speed;
                if (speed > 35f && desiredDir.sqrMagnitude > 0.01f)
                {
                    Vector3 prevDir = Missile.rb.velocity.normalized;
                    float turnRate = emergencyPullUpThisTick ? 3.5f : 1.15f;
                    Vector3 newDir = Vector3.RotateTowards(prevDir, desiredDir, dt * turnRate, 0f);

                    float seaY = Datum.LocalSeaY;
                    float localTerrainY = SampleTerrainY(myPos.x, myPos.z, seaY);
                    float aheadTerrainY = SampleTerrainY(myPos.x + (newDir.x * 160f), myPos.z + (newDir.z * 160f), seaY);
                    float highestLocal = Mathf.Max(localTerrainY, aheadTerrainY);
                    float safeFloorY = highestLocal > seaY + 0.5f
                        ? highestLocal + 32f
                        : seaY + 2.55f;

                    Vector3 nextPosCheck = myPos + (newDir * (nextSpeed * Mathf.Max(dt, 0.25f)));
                    if (nextPosCheck.y < safeFloorY && dist > 180f)
                    {
                        float neededDy = Mathf.Clamp((safeFloorY - myPos.y) / Mathf.Max(45f, nextSpeed * 0.65f), 0.04f, 0.55f);
                        newDir.y = Mathf.Max(neededDy, newDir.y);
                        newDir.Normalize();
                    }

                    Vector3 prevFlat = new Vector3(prevDir.x, 0f, prevDir.z);
                    Vector3 newFlat = new Vector3(newDir.x, 0f, newDir.z);
                    float signedYawRateDeg = (prevFlat.sqrMagnitude > 0.001f && newFlat.sqrMagnitude > 0.001f && dt > 0.0001f)
                        ? Vector3.SignedAngle(prevFlat.normalized, newFlat.normalized, Vector3.up) / dt
                        : 0f;
                    float targetBankDeg = Mathf.Clamp(signedYawRateDeg * 1.15f, -45f, 45f);
                    currentBankDeg = Mathf.Lerp(currentBankDeg, targetBankDeg, 1f - Mathf.Exp(-dt * 6.5f));

                    Vector3 newVel = newDir * nextSpeed;
                    Missile.rb.velocity = newVel;
                    if (newVel.sqrMagnitude > 1f)
                    {
                        Quaternion lookRot = Quaternion.LookRotation(newDir, Vector3.up);
                        Missile.rb.MoveRotation(lookRot * Quaternion.Euler(0f, 0f, currentBankDeg * bankRollSign));
                    }
                }
            }
            // Assist steering gently for custom vertical/ballistic/MARV profiles where vanilla seeker lags.
            else if (!hojUpdatedAimThisTick && ageSeconds > 0.35f &&
                     (Spec.IsAbmInterceptor || Spec.FlightProfile == MissileFlightProfile.MarvTerminal ||
                      Spec.FlightProfile == MissileFlightProfile.SeaSkimming))
            {
                Vector3 desiredDir = (aimPoint - myPos).normalized;
                float speed = Missile.rb.velocity.magnitude;
                if (speed > 40f)
                {
                    Vector3 newVel = Vector3.RotateTowards(Missile.rb.velocity.normalized, desiredDir, dt * 1.4f, 0f) * speed;
                    Missile.rb.velocity = newVel;
                    if (newVel.sqrMagnitude > 1f)
                    {
                        Missile.rb.MoveRotation(Quaternion.LookRotation(newVel.normalized, Vector3.up));
                    }
                }
            }
        }

        private void UpdateSeaSkimmingEvasiveProfile(
            Vector3 myPos,
            Vector3 targetPos,
            Vector3 targetVel,
            float dist,
            float tti,
            float dt,
            ref Vector3 aimPoint)
        {
            emergencyPullUpThisTick = false;
            float seaY = Datum.LocalSeaY;
            float seaSkimFloor = seaY + Mathf.Max(2.6f, Spec.SeaSkimAltitudeMeters);

            if (dist <= 450f)
            {
                Vector3 terminalPt = new Vector3(
                    targetPos.x + (targetVel.x * tti * 0.5f),
                    Mathf.Max(seaY + 2.2f, targetPos.y + 1.4f),
                    targetPos.z + (targetVel.z * tti * 0.5f));

                if (Physics.Linecast(
                        myPos + (Vector3.up * 2f),
                        terminalPt + (Vector3.up * 2f),
                        out RaycastHit termHit,
                        TerrainClearanceMask,
                        QueryTriggerInteraction.Ignore))
                {
                    terminalPt.y = Mathf.Max(terminalPt.y, termHit.point.y + 52f);
                    emergencyPullUpThisTick = true;
                }

                aimPoint = terminalPt;
                return;
            }

            bool needsTargetMovedRecompute =
                Time.time - lastRouteRecomputeTime >= 3.5f &&
                dist > 1400f &&
                Vector3.SqrMagnitude(targetPos - routeTargetAnchorPos) > 900f * 900f;

            bool needsLiveAaRecompute = false;
            if (Time.time >= nextLiveAaCheckTime)
            {
                nextLiveAaCheckTime = Time.time + 1.5f;
                if (dist > 1800f &&
                    Time.time - lastRouteRecomputeTime >= 3.5f &&
                    routePrecomputed &&
                    precomputedRoutePoints.Count >= 2)
                {
                    needsLiveAaRecompute = IsUpcomingRouteEnteringNewAaThreat(targetPos);
                }
            }

            if (!routePrecomputed ||
                precomputedRoutePoints.Count < 2 ||
                needsTargetMovedRecompute ||
                needsLiveAaRecompute)
            {
                PrecomputeSeaWraithRoute(myPos, targetPos);
            }

            int ptCount = precomputedRoutePoints.Count;
            if (ptCount >= 2)
            {
                int searchEnd = Mathf.Min(ptCount - 1, routeCursorIndex + 16);
                int bestIdx = routeCursorIndex;
                float bestSq = float.MaxValue;
                for (int i = routeCursorIndex; i <= searchEnd; i++)
                {
                    Vector3 p = precomputedRoutePoints[i];
                    float dx = p.x - myPos.x;
                    float dz = p.z - myPos.z;
                    float dSq = (dx * dx) + (dz * dz);
                    if (dSq < bestSq)
                    {
                        bestSq = dSq;
                        bestIdx = i;
                    }
                }

                routeCursorIndex = bestIdx;
            }

            float currentSpeed = Missile.rb != null ? Missile.rb.velocity.magnitude : Spec.CruiseSpeedMps;
            float lookaheadMeters = Mathf.Clamp(Mathf.Max(currentSpeed, 220f) * 1.15f, 260f, 440f);
            Vector3 baseLookahead = targetPos;
            float maxChordSafeY = seaSkimFloor;
            bool anyOverLand = false;

            if (ptCount >= 2)
            {
                Vector3 prevPt = precomputedRoutePoints[routeCursorIndex];
                maxChordSafeY = Mathf.Max(maxChordSafeY, prevPt.y);
                anyOverLand |= precomputedRouteOverLand[routeCursorIndex];

                float accumulated = 0f;
                int idx = routeCursorIndex;
                Vector3 chosenPt = prevPt;
                while (idx + 1 < ptCount)
                {
                    Vector3 nextPt = precomputedRoutePoints[idx + 1];
                    float segLen = Vector2.Distance(new Vector2(prevPt.x, prevPt.z), new Vector2(nextPt.x, nextPt.z));
                    maxChordSafeY = Mathf.Max(maxChordSafeY, nextPt.y);
                    anyOverLand |= precomputedRouteOverLand[idx + 1];

                    if (accumulated + segLen >= lookaheadMeters && segLen > 0.1f)
                    {
                        float t = Mathf.Clamp01((lookaheadMeters - accumulated) / segLen);
                        chosenPt = Vector3.Lerp(prevPt, nextPt, t);
                        break;
                    }

                    accumulated += segLen;
                    prevPt = nextPt;
                    chosenPt = nextPt;
                    idx++;
                }

                chosenPt.y = Mathf.Max(chosenPt.y, maxChordSafeY);
                baseLookahead = chosenPt;
            }

            float localSurfaceY = SampleTerrainY(myPos.x, myPos.z, seaY);
            if (localSurfaceY > seaY + 0.5f)
            {
                anyOverLand = true;
                baseLookahead.y = Mathf.Max(baseLookahead.y, localSurfaceY + 52f);
            }

            if (dist <= 1200f && dist > 450f)
            {
                float terminalBlend = Mathf.InverseLerp(1200f, 450f, dist);
                Vector3 predictedTarget = targetPos + (targetVel * (tti * 0.5f));
                baseLookahead.x = Mathf.Lerp(baseLookahead.x, predictedTarget.x, terminalBlend);
                baseLookahead.z = Mathf.Lerp(baseLookahead.z, predictedTarget.z, terminalBlend);

                float blendedTerrainY = SampleTerrainY(baseLookahead.x, baseLookahead.z, seaY);
                if (blendedTerrainY > seaY + 0.5f)
                {
                    anyOverLand = true;
                    baseLookahead.y = Mathf.Max(baseLookahead.y, blendedTerrainY + 52f);
                }
            }

            Vector3 flatToAim = new Vector3(baseLookahead.x - myPos.x, 0f, baseLookahead.z - myPos.z);
            Vector3 groundDir = flatToAim.sqrMagnitude > 0.01f
                ? flatToAim.normalized
                : new Vector3(Missile.transform.forward.x, 0f, Missile.transform.forward.z).normalized;
            if (groundDir.sqrMagnitude < 0.01f)
            {
                groundDir = Vector3.forward;
            }

            float jinkRange = Mathf.Max(1000f, Spec.EvasiveJinkStartRangeKm * 1000f);
            float desiredJink = 0f;
            if (Spec.EnableTerminalEvasiveJink && !anyOverLand && dist <= jinkRange && dist > 450f)
            {
                Vector3 lateral = Vector3.Cross(Vector3.up, groundDir).normalized;
                float taper = Mathf.Clamp01((dist - 450f) / 700f);
                float period = Mathf.Max(0.8f, Spec.EvasiveJinkPeriodSeconds);
                float candidateJink = Mathf.Sin((ageSeconds * ((Mathf.PI * 2f) / period)) + weaveSeed) *
                                      Spec.EvasiveJinkAmplitudeMeters * taper;
                Vector3 jinkCheckPt = baseLookahead + (lateral * candidateJink);
                if (SampleTerrainY(jinkCheckPt.x, jinkCheckPt.z, seaY) <= seaY + 0.5f)
                {
                    desiredJink = candidateJink;
                }
            }

            smoothedJinkOffset = Mathf.MoveTowards(smoothedJinkOffset, desiredJink, Mathf.Max(dt, 0.02f) * 150f);
            if (Mathf.Abs(smoothedJinkOffset) > 0.1f)
            {
                Vector3 lateral = Vector3.Cross(Vector3.up, groundDir).normalized;
                baseLookahead += lateral * smoothedJinkOffset;
            }

            // Live forward obstacle shield: catch sharp cliff spurs, tall trees, or buildings ahead.
            Vector3 velDir = Missile.rb != null && Missile.rb.velocity.sqrMagnitude > 25f
                ? Missile.rb.velocity.normalized
                : Missile.transform.forward;
            if (Physics.Raycast(myPos, velDir, out RaycastHit fwdHit, 580f, TerrainClearanceMask, QueryTriggerInteraction.Ignore))
            {
                baseLookahead.y = Mathf.Max(baseLookahead.y, fwdHit.point.y + 72f);
                emergencyPullUpThisTick = true;
            }

            if (Physics.Raycast(myPos - (Vector3.up * 12f), velDir, out RaycastHit bellyHit, 460f, TerrainClearanceMask, QueryTriggerInteraction.Ignore))
            {
                baseLookahead.y = Mathf.Max(baseLookahead.y, bellyHit.point.y + 72f);
                emergencyPullUpThisTick = true;
            }

            baseLookahead.y = Mathf.Max(seaSkimFloor, baseLookahead.y);
            aimPoint = baseLookahead;
        }

        private void AdjustLaunchPitchForImmediateTerrain(Vector3 startPos)
        {
            if (launchedVertically || precomputedRoutePoints.Count < 2)
            {
                return;
            }

            float requiredSlope = 0f;
            int checkLimit = Mathf.Min(precomputedRoutePoints.Count - 1, 8);
            for (int i = 1; i <= checkLimit; i++)
            {
                Vector3 pt = precomputedRoutePoints[i];
                float dx = pt.x - startPos.x;
                float dz = pt.z - startPos.z;
                float horiz = Mathf.Sqrt((dx * dx) + (dz * dz));
                if (horiz < 20f)
                {
                    continue;
                }

                float slope = (pt.y - startPos.y) / horiz;
                if (slope > requiredSlope)
                {
                    requiredSlope = slope;
                }
            }

            if (requiredSlope > 0.06f)
            {
                Vector3 flat = new Vector3(initialLaunchDir.x, 0f, initialLaunchDir.z);
                if (flat.sqrMagnitude < 0.001f)
                {
                    flat = Vector3.forward;
                }
                else
                {
                    flat.Normalize();
                }

                float minY = Mathf.Clamp(requiredSlope + 0.14f, 0.26f, 0.68f);
                if (initialLaunchDir.y < minY)
                {
                    initialLaunchDir = new Vector3(flat.x, minY, flat.z).normalized;
                }
            }
        }

        private void PrecomputeSeaWraithRoute(Vector3 startPos, Vector3 targetPos)
        {
            float seaY = Datum.LocalSeaY;
            Vector3[] waypoints = SelectBestHorizontalRoute(startPos, targetPos, seaY);

            precomputedRoutePoints.Clear();
            precomputedRouteOverLand.Clear();

            float totalLen = 0f;
            for (int w = 0; w < waypoints.Length - 1; w++)
            {
                totalLen += Vector2.Distance(
                    new Vector2(waypoints[w].x, waypoints[w].z),
                    new Vector2(waypoints[w + 1].x, waypoints[w + 1].z));
            }

            if (totalLen < 20f)
            {
                float skimY = seaY + Mathf.Max(2.8f, Spec.SeaSkimAltitudeMeters);
                precomputedRoutePoints.Add(new Vector3(startPos.x, Mathf.Max(startPos.y, skimY), startPos.z));
                precomputedRouteOverLand.Add(false);
                precomputedRoutePoints.Add(new Vector3(targetPos.x, Mathf.Max(targetPos.y, skimY), targetPos.z));
                precomputedRouteOverLand.Add(false);
                routeCursorIndex = 0;
                routePrecomputed = true;
                routeTargetAnchorPos = targetPos;
                lastRouteRecomputeTime = Time.time;
                return;
            }

            float stepMeters = Mathf.Clamp(totalLen / 380f, 100f, 160f);
            var samplePts = new List<Vector3>(256);
            var segDirs = new List<Vector3>(256);

            for (int w = 0; w < waypoints.Length - 1; w++)
            {
                Vector3 a = waypoints[w];
                Vector3 b = waypoints[w + 1];
                float segLen = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
                if (segLen < 1f)
                {
                    continue;
                }

                Vector3 dir = new Vector3(b.x - a.x, 0f, b.z - a.z).normalized;
                int steps = Mathf.Max(1, Mathf.CeilToInt(segLen / stepMeters));
                int startStep = w == 0 ? 0 : 1;
                for (int s = startStep; s <= steps; s++)
                {
                    float t = s / (float)steps;
                    samplePts.Add(new Vector3(Mathf.Lerp(a.x, b.x, t), seaY, Mathf.Lerp(a.z, b.z, t)));
                    segDirs.Add(dir);
                }
            }

            int n = samplePts.Count;
            if (n < 2)
            {
                return;
            }

            float seaSkimFloor = seaY + Mathf.Max(2.8f, Spec.SeaSkimAltitudeMeters);
            float[] rawY = new float[n];
            bool[] overLand = new bool[n];

            for (int i = 0; i < n; i++)
            {
                Vector3 pt = samplePts[i];
                Vector3 side = Vector3.Cross(Vector3.up, segDirs[i]).normalized;
                float hCenter = SampleTerrainY(pt.x, pt.z, seaY);
                float hLeft = SampleTerrainY(pt.x - (side.x * 55f), pt.z - (side.z * 55f), seaY);
                float hRight = SampleTerrainY(pt.x + (side.x * 55f), pt.z + (side.z * 55f), seaY);
                float h = Mathf.Max(hCenter, Mathf.Max(hLeft, hRight));

                bool isLand = h > seaY + 0.5f;
                overLand[i] = isLand;
                rawY[i] = isLand
                    ? Mathf.Max(seaSkimFloor, h + LandTreeClearanceMeters)
                    : seaSkimFloor;
            }

            // Dilate peaks by +-2 samples (~200-320 m) so the missile clears cliff shoulders and canopies.
            float[] safeY = new float[n];
            for (int i = 0; i < n; i++)
            {
                float maxY = rawY[i];
                int lo = Mathf.Max(0, i - 2);
                int hi = Mathf.Min(n - 1, i + 2);
                for (int j = lo; j <= hi; j++)
                {
                    if (rawY[j] > maxY)
                    {
                        maxY = rawY[j];
                    }
                }

                safeY[i] = maxY;
            }

            // Backward climb-slope envelope: begin climbing km ahead of steep ridges (tan 17 deg).
            float climbTan = Mathf.Tan(17f * Mathf.Deg2Rad);
            for (int i = n - 2; i >= 0; i--)
            {
                float segDist = Vector2.Distance(
                    new Vector2(samplePts[i].x, samplePts[i].z),
                    new Vector2(samplePts[i + 1].x, samplePts[i + 1].z));
                safeY[i] = Mathf.Max(safeY[i], safeY[i + 1] - (segDist * climbTan));
            }

            // Forward descent-slope envelope: glide smoothly back to 10 ft sea-skim after ridges (tan 15 deg).
            float descentTan = Mathf.Tan(15f * Mathf.Deg2Rad);
            for (int i = 1; i < n; i++)
            {
                float segDist = Vector2.Distance(
                    new Vector2(samplePts[i - 1].x, samplePts[i - 1].z),
                    new Vector2(samplePts[i].x, samplePts[i].z));
                safeY[i] = Mathf.Max(safeY[i], safeY[i - 1] - (segDist * descentTan));
            }

            for (int i = 0; i < n; i++)
            {
                precomputedRoutePoints.Add(new Vector3(samplePts[i].x, safeY[i], samplePts[i].z));
                precomputedRouteOverLand.Add(overLand[i]);
            }

            routeCursorIndex = 0;
            routePrecomputed = true;
            routeTargetAnchorPos = targetPos;
            lastRouteRecomputeTime = Time.time;
            MdsLog.Debug($"[SeaWraith] Precomputed {n}-node terrain-clearance route ({totalLen:0} m).");
        }

        private bool IsUpcomingRouteEnteringNewAaThreat(Vector3 targetPos)
        {
            CollectHostileAaThreats();
            if (hostileAaThreats.Count == 0)
            {
                return false;
            }

            int start = Mathf.Clamp(routeCursorIndex, 0, precomputedRoutePoints.Count - 1);
            int end = Mathf.Min(precomputedRoutePoints.Count - 1, start + 22);
            const float targetExclusionSq = 700f * 700f;

            for (int i = start; i <= end; i++)
            {
                Vector3 pt = precomputedRoutePoints[i];
                float tdx = pt.x - targetPos.x;
                float tdz = pt.z - targetPos.z;
                if ((tdx * tdx) + (tdz * tdz) <= targetExclusionSq)
                {
                    continue;
                }

                for (int j = 0; j < hostileAaThreats.Count; j++)
                {
                    AaThreatBubble th = hostileAaThreats[j];
                    float dx = pt.x - th.Pos.x;
                    float dz = pt.z - th.Pos.z;
                    float innerR = th.Radius * 0.65f;
                    if ((dx * dx) + (dz * dz) < innerR * innerR)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void CollectHostileAaThreats()
        {
            hostileAaThreats.Clear();
            List<Unit>? units = UnitRegistry.allUnits;
            if (units == null || units.Count == 0 || Missile == null)
            {
                return;
            }

            FactionHQ? ownerHq = Missile.NetworkHQ ?? Missile.MapHQ;
            for (int i = 0; i < units.Count; i++)
            {
                Unit? u = units[i];
                if (u == null || u.disabled || ReferenceEquals(u, Missile) || ReferenceEquals(u, TargetUnit))
                {
                    continue;
                }

                if (!GameInteropHelpers.IsHostileToAffinity(u, OwnerFaction, ownerHq))
                {
                    continue;
                }

                if (u.GetComponent<ModularSiteBehavior>() is ModularSiteBehavior mdsSite &&
                    mdsSite.Spec != null &&
                    mdsSite.Spec.Role != WeaponCascadeRole.AntiShipCoastal)
                {
                    float radius = Mathf.Clamp(mdsSite.Spec.MaxEngagementRangeKm * 1000f, 4800f, 14000f);
                    float weight = mdsSite.Spec.Role == WeaponCascadeRole.FireControl ||
                                   mdsSite.Spec.Role == WeaponCascadeRole.EarlyWarningRadar
                        ? 3.2f
                        : 4.8f;
                    hostileAaThreats.Add(new AaThreatBubble(u.transform.position, radius, weight));
                    continue;
                }

                if (u is GroundVehicle && u.definition is VehicleDefinition vdef)
                {
                    switch (vdef.vehicleType)
                    {
                        case VehicleType.R_SAM:
                            hostileAaThreats.Add(new AaThreatBubble(u.transform.position, 11500f, 4.8f));
                            break;
                        case VehicleType.IR_SAM:
                            hostileAaThreats.Add(new AaThreatBubble(u.transform.position, 6000f, 4.0f));
                            break;
                        case VehicleType.AAA:
                            hostileAaThreats.Add(new AaThreatBubble(u.transform.position, 4000f, 3.4f));
                            break;
                        case VehicleType.RDR:
                            hostileAaThreats.Add(new AaThreatBubble(u.transform.position, 9200f, 2.6f));
                            break;
                    }

                    continue;
                }

                if (u is Building && u.definition is BuildingDefinition bdef)
                {
                    if (bdef.buildingType == BuildingType.DEF)
                    {
                        hostileAaThreats.Add(new AaThreatBubble(u.transform.position, 7800f, 4.4f));
                    }
                    else if (bdef.buildingType == BuildingType.RDR || u.HasRadarEmission())
                    {
                        hostileAaThreats.Add(new AaThreatBubble(u.transform.position, 10000f, 2.8f));
                    }

                    continue;
                }

                if (u is Ship)
                {
                    float radius = u.HasRadarEmission() ? 9800f : 5500f;
                    hostileAaThreats.Add(new AaThreatBubble(u.transform.position, radius, 5.0f));
                }
            }
        }

        private Vector3[] SelectBestHorizontalRoute(Vector3 startPos, Vector3 targetPos, float seaY)
        {
            CollectHostileAaThreats();

            Vector3 startFlat = new Vector3(startPos.x, seaY, startPos.z);
            Vector3 targetFlat = new Vector3(targetPos.x, seaY, targetPos.z);
            Vector3[] direct = { startFlat, targetFlat };

            Vector3 delta = targetFlat - startFlat;
            float flatDist = delta.magnitude;
            if (flatDist < 1200f)
            {
                return direct;
            }

            float bestCost = EvaluateCandidatePolyline(direct, seaY, targetFlat, out float directMaxElev, out Vector3 peakPos);
            if (flatDist >= 2000f)
            {
                bestCost += flatDist * 0.42f;
            }

            Vector3 flatDir = delta / flatDist;
            Vector3 side = Vector3.Cross(Vector3.up, flatDir).normalized;

            Vector3[] bestRoute = direct;
            int bestFlankSign = chosenFlankSign;
            int salvoTieBreakSign = Missile != null && (Missile.persistentID.Id % 2 == 0) ? 1 : -1;

            Vector3 currentVelFlat = Vector3.zero;
            bool hasVelDir = false;
            if (routePrecomputed && Missile != null && Missile.rb != null)
            {
                Vector3 v = Missile.rb.velocity;
                v.y = 0f;
                if (v.sqrMagnitude > 25f)
                {
                    currentVelFlat = v.normalized;
                    hasVelDir = true;
                }
            }

            float[] widthRatios = { 0.18f, 0.30f, 0.44f, 0.60f };
            float minArcW = Mathf.Min(1100f, flatDist * 0.45f);
            float maxArcW = Mathf.Min(12000f, flatDist * 0.65f);
            int[] flankSigns = { 1, -1 };

            for (int fs = 0; fs < flankSigns.Length; fs++)
            {
                int flankSign = flankSigns[fs];
                for (int r = 0; r < widthRatios.Length; r++)
                {
                    float arcWidth = Mathf.Clamp(flatDist * widthRatios[r], minArcW, maxArcW);

                    Vector3[] lateHook = BuildCubicBezierArc(
                        startFlat, targetFlat, flatDir, side, flatDist, flankSign, arcWidth,
                        0.34f, 0.85f, 0.80f, 1.00f);
                    EvaluateFlankingCandidate(
                        lateHook, flankSign, seaY, targetFlat, flatDist, salvoTieBreakSign, hasVelDir, currentVelFlat,
                        ref bestCost, ref bestRoute, ref bestFlankSign);

                    if (flatDist >= 4500f)
                    {
                        Vector3[] deepBypass = BuildCubicBezierArc(
                            startFlat, targetFlat, flatDir, side, flatDist, flankSign, arcWidth,
                            0.28f, 0.98f, 0.88f, 1.02f);
                        EvaluateFlankingCandidate(
                            deepBypass, flankSign, seaY, targetFlat, flatDist, salvoTieBreakSign, hasVelDir, currentVelFlat,
                            ref bestCost, ref bestRoute, ref bestFlankSign);
                    }
                }
            }

            if (directMaxElev > 15f)
            {
                float peakFrac = Mathf.Clamp(Vector3.Dot(peakPos - startFlat, flatDir) / flatDist, 0.20f, 0.80f);
                float t1Peak = peakFrac * 0.70f;
                float t2Peak = Mathf.Clamp(peakFrac * 1.25f, 0.55f, 0.88f);
                float[] peakOffsets = { 2000f, 4000f, 6800f, 9500f };

                for (int fs = 0; fs < flankSigns.Length; fs++)
                {
                    int flankSign = flankSigns[fs];
                    for (int o = 0; o < peakOffsets.Length; o++)
                    {
                        if (peakOffsets[o] > flatDist * 0.85f)
                        {
                            continue;
                        }

                        Vector3[] peakBypass = BuildCubicBezierArc(
                            startFlat, targetFlat, flatDir, side, flatDist, flankSign, peakOffsets[o],
                            t1Peak, 0.95f, t2Peak, 1.00f);
                        EvaluateFlankingCandidate(
                            peakBypass, flankSign, seaY, targetFlat, flatDist, salvoTieBreakSign, hasVelDir, currentVelFlat,
                            ref bestCost, ref bestRoute, ref bestFlankSign);
                    }
                }
            }

            if (bestFlankSign != 0)
            {
                chosenFlankSign = bestFlankSign;
            }

            return bestRoute;
        }

        private void EvaluateFlankingCandidate(
            Vector3[] cand,
            int flankSign,
            float seaY,
            Vector3 targetFlat,
            float flatDist,
            int salvoTieBreakSign,
            bool hasVelDir,
            Vector3 currentVelFlat,
            ref float bestCost,
            ref Vector3[] bestRoute,
            ref int bestFlankSign)
        {
            float cost = EvaluateCandidatePolyline(cand, seaY, targetFlat, out _, out _);

            if (chosenFlankSign != 0)
            {
                if (flankSign == chosenFlankSign)
                {
                    cost *= 0.86f;
                }
            }
            else if (flankSign == salvoTieBreakSign)
            {
                cost *= 0.96f;
            }

            if (hasVelDir && cand.Length >= 2)
            {
                Vector3 firstSeg = cand[1] - cand[0];
                firstSeg.y = 0f;
                if (firstSeg.sqrMagnitude > 0.01f && Vector3.Dot(currentVelFlat, firstSeg.normalized) < -0.15f)
                {
                    cost += flatDist * 0.8f;
                }
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestRoute = cand;
                bestFlankSign = flankSign;
            }
        }

        private static Vector3[] BuildCubicBezierArc(
            Vector3 startFlat,
            Vector3 targetFlat,
            Vector3 flatDir,
            Vector3 side,
            float flatDist,
            int flankSign,
            float arcWidth,
            float t1,
            float w1,
            float t2,
            float w2,
            int segments = 8)
        {
            Vector3 p0 = startFlat;
            Vector3 p1 = startFlat + (flatDir * (flatDist * t1)) + (side * (flankSign * arcWidth * w1));
            Vector3 p2 = startFlat + (flatDir * (flatDist * t2)) + (side * (flankSign * arcWidth * w2));
            Vector3 p3 = targetFlat;

            var pts = new Vector3[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                float u = i / (float)segments;
                float inv = 1f - u;
                pts[i] = (inv * inv * inv * p0)
                       + (3f * inv * inv * u * p1)
                       + (3f * inv * u * u * p2)
                       + (u * u * u * p3);
            }

            return pts;
        }

        private float EvaluateCandidatePolyline(
            Vector3[] waypoints,
            float seaY,
            Vector3 targetFlat,
            out float maxElevAboveSea,
            out Vector3 peakPos)
        {
            maxElevAboveSea = 0f;
            peakPos = waypoints[0];
            float cost = 0f;
            const float probeStep = 300f;
            const float targetExclusionSq = 650f * 650f;
            int aaCount = hostileAaThreats.Count;

            for (int w = 0; w < waypoints.Length - 1; w++)
            {
                Vector3 a = waypoints[w];
                Vector3 b = waypoints[w + 1];
                Vector3 d = b - a;
                d.y = 0f;
                float segLen = d.magnitude;
                if (segLen < 1f)
                {
                    continue;
                }

                Vector3 dir = d / segLen;
                Vector3 side = Vector3.Cross(Vector3.up, dir).normalized;
                int steps = Mathf.Max(1, Mathf.CeilToInt(segLen / probeStep));
                float stepDist = segLen / steps;

                for (int s = 0; s <= steps; s++)
                {
                    Vector3 pt = a + (dir * (stepDist * s));
                    float hC = SampleTerrainY(pt.x, pt.z, seaY);
                    float hL = SampleTerrainY(pt.x - (side.x * 60f), pt.z - (side.z * 60f), seaY);
                    float hR = SampleTerrainY(pt.x + (side.x * 60f), pt.z + (side.z * 60f), seaY);
                    float elev = Mathf.Max(0f, Mathf.Max(hC, Mathf.Max(hL, hR)) - seaY);

                    if (elev > maxElevAboveSea)
                    {
                        maxElevAboveSea = elev;
                        peakPos = pt;
                    }

                    cost += stepDist;
                    if (elev > 1.0f)
                    {
                        cost += stepDist * (2.6f + (elev * 0.09f));
                    }

                    if (elev > 85f)
                    {
                        cost += stepDist * 6.5f;
                    }

                    if (aaCount > 0)
                    {
                        float tdx = pt.x - targetFlat.x;
                        float tdz = pt.z - targetFlat.z;
                        if ((tdx * tdx) + (tdz * tdz) > targetExclusionSq)
                        {
                            for (int j = 0; j < aaCount; j++)
                            {
                                AaThreatBubble th = hostileAaThreats[j];
                                float dx = pt.x - th.Pos.x;
                                float dz = pt.z - th.Pos.z;
                                float outerR = th.Radius * 1.18f;
                                float dSq = (dx * dx) + (dz * dz);
                                if (dSq >= outerR * outerR)
                                {
                                    continue;
                                }

                                float aaDist = Mathf.Sqrt(dSq);
                                if (aaDist < th.Radius)
                                {
                                    float pen = 1f - (aaDist / th.Radius);
                                    cost += stepDist * th.Weight * (2.0f + (pen * pen * 18.0f));
                                }
                                else
                                {
                                    cost += stepDist * th.Weight * 0.35f;
                                }
                            }
                        }
                    }
                }
            }

            cost += maxElevAboveSea * 45f;
            return cost;
        }

        private static float SampleTerrainY(float x, float z, float seaY)
        {
            var origin = new Vector3(x, seaY + 3200f, z);
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 3600f, TerrainClearanceMask, QueryTriggerInteraction.Ignore))
            {
                return Mathf.Max(seaY, hit.point.y);
            }

            return seaY;
        }

        private void UpdateFlightControlSurfaceAnimation(float dt)
        {
            if (rig == null || !Spec.IsAntiShip)
            {
                return;
            }

            if (!controlSurfacesCalibrated)
            {
                if (!finsDeployed || (animator != null && !animator.IsFullyDeployed))
                {
                    return;
                }

                CalibrateControlSurfaces();
            }

            Vector3 currPos = Missile.transform.position;
            Vector3 velDir;
            if (Missile.rb != null && Missile.rb.velocity.sqrMagnitude > 25f)
            {
                velDir = Missile.rb.velocity.normalized;
            }
            else if (hasPrevVisualPos)
            {
                Vector3 disp = currPos - prevVisualPos;
                velDir = disp.sqrMagnitude > 0.04f ? disp.normalized : Missile.transform.forward;
            }
            else
            {
                velDir = Missile.transform.forward;
            }

            prevVisualPos = currPos;
            hasPrevVisualPos = true;

            if (!hasPrevVisualVelDir)
            {
                prevVisualVelDir = velDir;
                hasPrevVisualVelDir = true;
                return;
            }

            Vector3 localPrev = Missile.transform.InverseTransformDirection(prevVisualVelDir);
            Vector3 localCurr = Missile.transform.InverseTransformDirection(velDir);
            float safeDt = Mathf.Max(0.005f, dt);

            float pitchDeltaDeg = Mathf.Asin(Mathf.Clamp(localCurr.y - localPrev.y, -0.5f, 0.5f)) * Mathf.Rad2Deg;
            float yawDeltaDeg = Mathf.Asin(Mathf.Clamp(localCurr.x - localPrev.x, -0.5f, 0.5f)) * Mathf.Rad2Deg;
            float pitchRateDegPerSec = pitchDeltaDeg / safeDt;
            float yawRateDegPerSec = yawDeltaDeg / safeDt;
            float flightPitchDeg = Mathf.Asin(Mathf.Clamp(velDir.y, -1f, 1f)) * Mathf.Rad2Deg;

            if (Missile.remoteSim)
            {
                Vector3 prevFlat = new Vector3(prevVisualVelDir.x, 0f, prevVisualVelDir.z);
                Vector3 currFlat = new Vector3(velDir.x, 0f, velDir.z);
                float signedYawRateDeg = (prevFlat.sqrMagnitude > 0.001f && currFlat.sqrMagnitude > 0.001f)
                    ? Vector3.SignedAngle(prevFlat.normalized, currFlat.normalized, Vector3.up) / safeDt
                    : 0f;
                float estBank = Mathf.Clamp(signedYawRateDeg * 1.15f, -45f, 45f);
                currentBankDeg = Mathf.Lerp(currentBankDeg, estBank, 1f - Mathf.Exp(-safeDt * 6.5f));
            }

            float targetPitchCmd = Mathf.Clamp((pitchRateDegPerSec / 14f) + (flightPitchDeg / 22f), -1f, 1f);
            float targetYawCmd = Mathf.Clamp(yawRateDegPerSec / 20f, -1f, 1f);
            float targetRollCmd = Mathf.Clamp((-currentBankDeg / 45f * 0.45f) + (yawRateDegPerSec / 35f * 0.55f), -1f, 1f);

            float finLerp = 1f - Mathf.Exp(-safeDt * 12f);
            smoothedPitchCmd = Mathf.Lerp(smoothedPitchCmd, targetPitchCmd, finLerp);
            smoothedYawCmd = Mathf.Lerp(smoothedYawCmd, targetYawCmd, finLerp);
            smoothedRollCmd = Mathf.Lerp(smoothedRollCmd, targetRollCmd, finLerp);

            for (int i = 0; i < rig.FoldingFins.Count; i++)
            {
                Transform fin = rig.FoldingFins[i];
                if (fin == null)
                {
                    continue;
                }

                Vector3 trailDir = finTrailingUpDirsInMissile[i];
                Vector3 rollTang = Vector3.Cross(Vector3.forward, finRadialDirsInMissile[i]).normalized;
                float mix = (Vector3.Dot(trailDir, Vector3.up) * smoothedPitchCmd) +
                            (Vector3.Dot(trailDir, Vector3.right) * smoothedYawCmd * 0.7f) +
                            (Vector3.Dot(trailDir, rollTang) * smoothedRollCmd * 0.55f);
                float deflDeg = Mathf.Clamp(mix * 32f, -34f, 34f);
                fin.localRotation = finRestLocalRots[i] * Quaternion.Euler(deflDeg, 0f, 0f);
            }

            float targetAoADeg = Mathf.Clamp((pitchRateDegPerSec * 0.34f) + (flightPitchDeg * 0.22f), -9.5f, 11.5f);
            smoothedVisualAoADeg = Mathf.Lerp(smoothedVisualAoADeg, targetAoADeg, 1f - Mathf.Exp(-safeDt * 9f));
            if (rig.RootObject != null)
            {
                rig.RootObject.transform.localRotation =
                    Quaternion.Euler(smoothedVisualAoADeg * aoaPitchSign, 0f, 0f) * rigBaseLocalRot;
            }

            float targetFlexDeg = Mathf.Clamp((pitchRateDegPerSec * 0.18f) + (flightPitchDeg * 0.09f), -4.5f, 6.5f);
            smoothedWingFlexDeg = Mathf.Lerp(smoothedWingFlexDeg, targetFlexDeg, 1f - Mathf.Exp(-safeDt * 10f));
            if (rig.WingLeft != null)
            {
                rig.WingLeft.localRotation = wingLeftRestLocalRot * Quaternion.Euler(0f, 0f, smoothedWingFlexDeg * wingLeftFlexSign);
            }

            if (rig.WingRight != null)
            {
                rig.WingRight.localRotation = wingRightRestLocalRot * Quaternion.Euler(0f, 0f, smoothedWingFlexDeg * wingRightFlexSign);
            }

            prevVisualVelDir = velDir;
            hasPrevVisualVelDir = true;
        }

        private void CalibrateControlSurfaces()
        {
            if (rig == null)
            {
                return;
            }

            int count = rig.FoldingFins.Count;
            finRestLocalRots = new Quaternion[count];
            finTrailingUpDirsInMissile = new Vector3[count];
            finRadialDirsInMissile = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                Transform fin = rig.FoldingFins[i];
                if (fin == null)
                {
                    continue;
                }

                finRestLocalRots[i] = fin.localRotation;
                Vector3 localPos = Missile.transform.InverseTransformPoint(fin.position);
                Vector2 radial2 = new Vector2(localPos.x, localPos.y);
                finRadialDirsInMissile[i] = radial2.sqrMagnitude > 0.0001f
                    ? new Vector3(radial2.x, radial2.y, 0f).normalized
                    : Vector3.right;

                Vector3 aftLocal = fin.InverseTransformDirection(-Missile.transform.forward).normalized;
                Vector3 movedAftLocal = Quaternion.Euler(5f, 0f, 0f) * aftLocal;
                Vector3 deltaWorld = fin.TransformDirection(movedAftLocal - aftLocal);
                finTrailingUpDirsInMissile[i] = deltaWorld.sqrMagnitude > 0.000001f
                    ? Missile.transform.InverseTransformDirection(deltaWorld).normalized
                    : Vector3.up;
            }

            if (rig.WingLeft != null)
            {
                wingLeftRestLocalRot = rig.WingLeft.localRotation;
                Vector3 outwardLocal = rig.WingLeft.InverseTransformDirection(-Missile.transform.right).normalized;
                Vector3 deltaWorld = rig.WingLeft.TransformDirection((Quaternion.Euler(0f, 0f, 5f) * outwardLocal) - outwardLocal);
                wingLeftFlexSign = Vector3.Dot(deltaWorld, Missile.transform.up) >= 0f ? 1f : -1f;
            }

            if (rig.WingRight != null)
            {
                wingRightRestLocalRot = rig.WingRight.localRotation;
                Vector3 outwardLocal = rig.WingRight.InverseTransformDirection(Missile.transform.right).normalized;
                Vector3 deltaWorld = rig.WingRight.TransformDirection((Quaternion.Euler(0f, 0f, 5f) * outwardLocal) - outwardLocal);
                wingRightFlexSign = Vector3.Dot(deltaWorld, Missile.transform.up) >= 0f ? 1f : -1f;
            }

            controlSurfacesCalibrated = true;
        }

        private void DetachBoosterStage()
        {
            if (rig == null || rig.BoosterStage == null)
            {
                return;
            }

            Transform booster = rig.BoosterStage;
            Vector3 inheritVel = Missile.rb != null ? Missile.rb.velocity * 0.55f : Vector3.zero;
            booster.SetParent(null, worldPositionStays: true);
            BoosterDropVisual drop = booster.gameObject.AddComponent<BoosterDropVisual>();
            drop.Velocity = inheritVel - (Vector3.up * 6f);
        }

        private void PulseActiveDirectionalJammer()
        {
            if (Missile == null || Missile.disabled || !GameInteropHelpers.IsServerAuthority())
            {
                return;
            }

            List<Unit>? units = UnitRegistry.allUnits;
            if (units == null || units.Count == 0)
            {
                return;
            }

            Vector3 myPos = Missile.transform.position;
            Vector3 myFwd = Missile.transform.forward;
            float maxRangeM = Mathf.Max(2000f, Spec.ActiveJammerRangeKm * 1000f);
            float maxRangeSq = maxRangeM * maxRangeM;
            FactionHQ? ownerHq = Missile.NetworkHQ ?? Missile.MapHQ;

            for (int i = 0; i < units.Count; i++)
            {
                Unit u = units[i];
                if (u == null || u.disabled || ReferenceEquals(u, Missile))
                {
                    continue;
                }

                if (!GameInteropHelpers.IsHostileToAffinity(u, OwnerFaction, ownerHq))
                {
                    continue;
                }

                bool isJamTarget = u is Ship || u is Missile || (u is Building && u.HasRadarEmission());
                if (!isJamTarget)
                {
                    continue;
                }

                Vector3 rel = u.transform.position - myPos;
                float dSq = rel.sqrMagnitude;
                if (dSq > maxRangeSq || dSq < 1f)
                {
                    continue;
                }

                float d = Mathf.Sqrt(dSq);
                bool inForwardBeam = Vector3.Dot(myFwd, rel / d) >= 0.50f;
                if (!inForwardBeam && d > 1200f)
                {
                    continue;
                }

                float atten = Mathf.Clamp(1f - ((d / maxRangeM) * 0.55f), 0.35f, 1.0f);
                try
                {
                    u.Jam(new Unit.JamEventArgs
                    {
                        jammingUnit = Missile,
                        jamAmount = Spec.ActiveJammerIntensity * atten
                    });
                }
                catch
                {
                    // ignored
                }
            }
        }

        private sealed class BoosterDropVisual : MonoBehaviour
        {
            internal Vector3 Velocity;
            private float life;

            private void Update()
            {
                float dt = Time.deltaTime;
                life += dt;
                if (life >= 4.0f || transform.position.y < Datum.LocalSeaY - 5f)
                {
                    Destroy(gameObject);
                    return;
                }

                Velocity += Physics.gravity * dt;
                transform.position += Velocity * dt;
                transform.Rotate(new Vector3(45f, 18f, 95f) * dt, Space.Self);
            }
        }

        private void CheckAbmIntercept()
        {
            if (interceptedTarget || TargetUnit is not Missile targetMissile || targetMissile.disabled)
            {
                return;
            }

            Vector3 currPos = Missile.transform.position;
            Vector3 currTargetPos = targetMissile.transform.position;
            float distSq;
            if (hasPrevPositions)
            {
                Vector3 rel0 = prevPos - prevTargetPos;
                Vector3 rel1 = currPos - currTargetPos;
                Vector3 d = rel1 - rel0;
                float dSq = d.sqrMagnitude;
                float t = dSq > 0.0001f ? Mathf.Clamp01(-Vector3.Dot(rel0, d) / dSq) : 0f;
                Vector3 closest = rel0 + (d * t);
                distSq = closest.sqrMagnitude;
            }
            else
            {
                distSq = (currTargetPos - currPos).sqrMagnitude;
            }

            if (distSq > 95f * 95f)
            {
                return;
            }

            interceptedTarget = true;
            float altAboveSea = currTargetPos.y - Datum.LocalSeaY;
            bool highAltitude = altAboveSea >= Spec.HighAltitudeInterceptMinMeters;

            if (MdsConfig.EnableHighAltitudeDisarm.Value && highAltitude && Spec.DisarmTargetWarheadOnIntercept)
            {
                bool disarmed = GameInteropHelpers.SafeDisarmWarhead(targetMissile);
                MdsLog.Info(
                    $"[ABM] '{Spec.DisplayName}' intercepted '{targetMissile.unitName}' at {altAboveSea:0} m " +
                    $"(warheadArmed={!disarmed}).");
            }

            // Neutralize incoming warhead cleanly without nuclear detonation.
            targetMissile.Detonate(Vector3.up, hitArmor: true, hitTerrain: false);
            Missile.Detonate(Vector3.down, hitArmor: true, hitTerrain: false);
        }

        private void OnMissileDisabled(Unit _)
        {
            CustomMissileTracker.Unregister(this);
        }

        private void OnDestroy()
        {
            if (Missile != null)
            {
                Missile.onDisableUnit -= OnMissileDisabled;
            }

            CustomMissileTracker.Unregister(this);
        }

        internal static string? ResolveAssetPath(ContractorPackage package, string primaryRelative, string[] fallbacks)
        {
            if (package == null || string.IsNullOrWhiteSpace(package.AssetRootDirectory))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(primaryRelative))
            {
                string candidate = Path.IsPathRooted(primaryRelative)
                    ? primaryRelative
                    : Path.Combine(package.AssetRootDirectory, primaryRelative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            if (fallbacks != null)
            {
                for (int i = 0; i < fallbacks.Length; i++)
                {
                    string rel = fallbacks[i];
                    if (string.IsNullOrWhiteSpace(rel))
                    {
                        continue;
                    }

                    string candidate = Path.IsPathRooted(rel)
                        ? rel
                        : Path.Combine(package.AssetRootDirectory, rel);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }
    }
}
