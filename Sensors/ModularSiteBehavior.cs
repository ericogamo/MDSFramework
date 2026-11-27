using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Deployment;
using UnityEngine;

namespace MDSFramework.Sensors
{
    internal enum FcuEmconState
    {
        Normal = 0,
        BlinkingActive = 1,
        BlinkingSilent = 2,
        AradShutdown = 3,
        EoIrEmergencyOnly = 4
    }

    internal sealed class ModularSiteBehavior : MonoBehaviour
    {
        private static readonly FieldInfo? TargetDetectorPartField = AccessTools.Field(typeof(TargetDetector), "part");
        private static readonly FieldInfo? TargetDetectorMagnificationField = AccessTools.Field(typeof(TargetDetector), "magnification");
        private static readonly FieldInfo? TargetDetectorMaxSpeedField = AccessTools.Field(typeof(TargetDetector), "maxSpeed");
        private static readonly FieldInfo? TargetDetectorRotatorsField = AccessTools.Field(typeof(TargetDetector), "rotators");
        private static readonly MethodInfo? TargetDetectorAwakeMethod = AccessTools.Method(typeof(TargetDetector), "Awake");
        private static readonly MethodInfo? TargetDetectorOnInitializeMethod = AccessTools.Method(typeof(TargetDetector), "TargetDetector_OnInitialize");
        private static readonly FieldInfo? RadarConeField = AccessTools.Field(typeof(Radar), "radarCone");
        private static readonly FieldInfo? RadarPowerDrawField = AccessTools.Field(typeof(Radar), "powerDraw");
        private static readonly MethodInfo? RadarAwakeMethod = AccessTools.Method(typeof(Radar), "Awake");

        internal string SiteId = string.Empty;
        internal ContractorPackage Package = null!;
        internal ModularUnitSpec Spec = null!;
        internal FactionAffinity Faction;
        internal FactionHQ? Hq;
        internal Unit? Unit;
        internal GlbInstantiatedRig? Rig;
        internal GlbKeyframedAnimator? Animator;
        internal GlbTurretAimController? TurretAim;

        internal Radar? NativeRadar;
        internal TargetDetector? NativeDetector;

        internal FcuEmconState EmconState = FcuEmconState.Normal;
        internal bool SuspendedInTransit;
        internal bool ConsumedRtsCap = true;

        internal float AradShutdownUntil;
        internal float ForcedActiveUntil;
        internal float LastJamTime = -999f;
        internal float CurrentJamLevel;
        internal Unit? LastJammerUnit;

        internal int PrimaryAmmoRemaining;
        internal int SecondaryAmmoRemaining;
        internal int PrimaryMagazineSize { get; private set; }
        internal int SecondaryMagazineSize { get; private set; }
        internal int AmmoRemaining => PrimaryAmmoRemaining + SecondaryAmmoRemaining;
        internal float CooldownRemaining;
        internal float LastThreatSeenTime;
        internal bool PopUpRequested;

        internal PersistentID ActiveHelTargetId;
        internal float ActiveHelDwellSeconds;

        internal readonly List<ModularSiteBehavior> LinkedFcus = new List<ModularSiteBehavior>(4);
        internal readonly List<MdsWeaponStationProxy> WeaponProxies = new List<MdsWeaponStationProxy>(2);
        internal int SupportedBandMask { get; private set; }

        private bool initiallyConsumedRtsCap;
        private GameObject? nativeSensorHost;
        private bool hadNativeRadar;
        private bool hadNativeDetectorOnly;
        private Transform? cachedScannerTf;
        private UnitPart? cachedSensorPart;
        private float cachedCheckInterval = 1.0f;
        private float cachedAlertCheckInterval = 0.5f;
        private float cachedVisualRange;
        private float cachedMagnification = 1f;
        private float cachedMaxSpeed = 1000f;
        private object? cachedRotators;
        private bool cachedShared;
        private RadarParams cachedRadarParams;
        private float cachedRadarCone;
        private float cachedPowerDraw;

        private float currentAppliedRcs = 1f;
        private bool lastReportedRadarActive;
        private bool destroyedNotified;
        private UnitPart[]? cachedParts;
        private Building? subscribedBuilding;
        private GameObject? activeWreckRoot;
        private Vector3 lastVehiclePos;
        private bool hasLastVehiclePos;
        internal AudioSource[]? GunAudioVoices;
        internal int GunAudioVoiceCursor;

        internal bool IsVehicleMoving { get; private set; }
        internal bool IsAlive => this != null && Unit != null && !Unit.disabled && !destroyedNotified;
        internal bool IsDeployed => !IsVehicleMoving && (Animator == null || Animator.IsFullyDeployed);
        internal bool IsRadarPrimaryHub => Spec != null && (Spec.Role == WeaponCascadeRole.FireControl || Spec.Role == WeaponCascadeRole.EarlyWarningRadar);
        public bool IsCommandCenter => Spec != null && (Spec.Role == WeaponCascadeRole.CommandControl || Spec.IsCommandCenter);
        public bool IsFireControlRadar => Spec != null &&
            (Spec.Role == WeaponCascadeRole.FireControl ||
             Spec.Role == WeaponCascadeRole.EarlyWarningRadar ||
             (Spec.SensorProfile != null && Spec.SensorProfile.IsFireControlUnit));

        public bool LocalHoldFire { get; private set; }

        public bool IsHoldFireActive =>
            LocalHoldFire ||
            ((Unit is GroundVehicle || Unit is Ship) &&
             RtsBridgeHost.QueryUnitHoldFire != null &&
             RtsBridgeHost.QueryUnitHoldFire(Unit));

        public bool IsLogisticsHauler => Spec != null && (Spec.Role == WeaponCascadeRole.LogisticsHauler || Spec.IsContainerHauler);
        public bool RequiresFcuLink => Spec != null && Spec.RequiresFcuLink;
        public bool HasActiveFcuLink => HasFireControlGuidance;
        public float FcuLinkRadiusKm => Spec != null ? Spec.FcuLinkRadiusKm : 18f;
        public float EoIrBackupRangeKm => Spec?.SensorProfile != null ? Spec.SensorProfile.EoIrEmergencyRangeKm : 0f;
        public bool IsWeaponSite => Spec != null &&
            Spec.Role != WeaponCascadeRole.FireControl &&
            Spec.Role != WeaponCascadeRole.EarlyWarningRadar &&
            Spec.Role != WeaponCascadeRole.CommandControl &&
            !IsLogisticsHauler;

        public bool IsInCommandNet { get; internal set; }
        public int CommandNetFcuCount { get; internal set; }
        public int CommandNetActiveFcuCount { get; internal set; }
        public int CommandNetEffectorCount { get; internal set; }
        public int CommandNetId { get; internal set; }
        internal float EffectiveNetworkEccmBonus { get; set; }
        internal float EffectiveNetworkDetectionMultiplier { get; set; } = 1f;

        public void SetHoldFire(bool holdFire)
        {
            LocalHoldFire = holdFire;
            if (holdFire)
            {
                TurretAim?.ClearAim();
                TurretAim?.HideBeam();
                ActiveHelTargetId = default;
                ActiveHelDwellSeconds = 0f;
            }
        }

        internal void SyncWeaponStationsAmmo()
        {
            for (int i = 0; i < WeaponProxies.Count; i++)
            {
                if (WeaponProxies[i] != null)
                {
                    WeaponProxies[i].SyncToStation();
                }
            }

            RequestRearmIfNeeded();
        }

        internal void NotifyRearmed(int roundsAdded)
        {
            if (roundsAdded <= 0 || Spec == null)
            {
                return;
            }

            float reloadPause = Spec.Role == WeaponCascadeRole.CloseInGun ? 8f : 18f;
            CooldownRemaining = Mathf.Max(CooldownRemaining, reloadPause);
        }

        internal void RequestRearmIfNeeded()
        {
            if (Unit == null || Unit.disabled || !Unit.IsServer || Unit.NetworkHQ == null)
            {
                return;
            }

            if (SuspendedInTransit || IsVehicleMoving)
            {
                if (Unit.HasRequestedRearm)
                {
                    Unit.HasRequestedRearm = false;
                    Unit.NetworkHQ.RearmMissionController?.DeregisterNeedsRearm(Unit);
                }

                return;
            }

            bool anyMissing = false;
            bool needsRearm = false;
            for (int i = 0; i < WeaponProxies.Count; i++)
            {
                MdsWeaponStationProxy? proxy = WeaponProxies[i];
                if (proxy == null || !proxy.Rearmable)
                {
                    continue;
                }

                proxy.SyncToStation();
                int full = proxy.GetFullAmmo();
                if (full <= 0)
                {
                    continue;
                }

                int cur = proxy.GetAmmoTotal();
                if (cur < full)
                {
                    anyMissing = true;
                }

                int threshold = full <= 1 ? 0 : Mathf.FloorToInt(full * 0.5f);
                if (cur <= threshold)
                {
                    needsRearm = true;
                }
            }

            if (needsRearm)
            {
                if (Unit is Container)
                {
                    Unit.radarAlt = 0f;
                    Unit.speed = 0f;
                }

                if (!Unit.HasRequestedRearm)
                {
                    Unit.RequestRearm();
                }
            }
            else if (!anyMissing && Unit.HasRequestedRearm)
            {
                Unit.HasRequestedRearm = false;
                Unit.NetworkHQ.RearmMissionController?.DeregisterNeedsRearm(Unit);
            }
        }

        internal bool IsRadarCapable =>
            Spec != null &&
            !IsCommandCenter &&
            !IsLogisticsHauler &&
            Spec.SensorProfile != null &&
            (Spec.SensorProfile.IsFireControlUnit ||
             Spec.Role == WeaponCascadeRole.EarlyWarningRadar ||
             SensorBandPhysics.HasBand(SupportedBandMask, SensorBand.XBand) ||
             SensorBandPhysics.HasBand(SupportedBandMask, SensorBand.SBand) ||
             SensorBandPhysics.HasBand(SupportedBandMask, SensorBand.VhfLongWave));

        internal bool IsRadarEmitting
        {
            get
            {
                if (!IsAlive || SuspendedInTransit || !IsRadarCapable || !IsDeployed)
                {
                    return false;
                }

                return EmconState == FcuEmconState.Normal || EmconState == FcuEmconState.BlinkingActive;
            }
        }

        internal bool HasFireControlGuidance
        {
            get
            {
                if (!IsAlive)
                {
                    return false;
                }

                if (!Spec.RequiresFcuLink)
                {
                    return true;
                }

                for (int i = 0; i < LinkedFcus.Count; i++)
                {
                    ModularSiteBehavior fcu = LinkedFcus[i];
                    if (fcu != null && fcu.IsAlive && fcu.IsRadarEmitting)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        internal float CurrentEffectiveRcs
        {
            get
            {
                float progress = Animator != null ? Animator.NormalizedProgress : 1f;
                return Mathf.Lerp(Mathf.Max(0.02f, Spec.StowedRcs), Mathf.Max(0.05f, Spec.DeployedRcs), progress);
            }
        }

        internal void Initialize(
            string siteId,
            ContractorPackage package,
            ModularUnitSpec spec,
            FactionAffinity faction,
            FactionHQ? hq,
            Unit unit,
            GlbInstantiatedRig? rig,
            GlbKeyframedAnimator? animator,
            GlbTurretAimController? turretAim)
        {
            SiteId = siteId;
            Package = package;
            Spec = spec;
            Faction = faction;
            Hq = hq;
            Unit = unit;
            Rig = rig;
            Animator = animator;
            TurretAim = turretAim;

            PrimaryMagazineSize = spec.PrimaryMagazineCapacity >= 0
                ? spec.PrimaryMagazineCapacity
                : Math.Max(0, spec.MagazineCapacity);
            SecondaryMagazineSize = Math.Max(0, spec.SecondaryMagazineCapacity);
            PrimaryAmmoRemaining = PrimaryMagazineSize;
            SecondaryAmmoRemaining = SecondaryMagazineSize;
            SupportedBandMask = SensorBandPhysics.BuildBandBitmask(spec.SensorProfile?.SupportedBands);
            PopUpRequested = IsRadarPrimaryHub || IsCommandCenter || (animator != null && animator.TargetDeployed);
            LastThreatSeenTime = Time.time;

            NativeRadar = unit.GetComponentInChildren<Radar>(true);
            NativeDetector = unit.GetComponentInChildren<TargetDetector>(true);
            cachedParts = unit.GetComponentsInChildren<UnitPart>(true);
            initiallyConsumedRtsCap = ConsumedRtsCap;
            SnapshotNativeSensorTemplate();

            ApplyNativeSensorProfile();
            currentAppliedRcs = unit.RCS;
            SyncDynamicRcs();

            unit.onDisableUnit += OnUnitDisabled;
            if (unit is Building building)
            {
                subscribedBuilding = building;
                building.OnRepair -= HandleBuildingRepaired;
                building.OnRepair += HandleBuildingRepaired;
            }
        }

        public void SetHaulerOutriggersDeployed(bool deployed)
        {
            RequestDeployState(deployed);
        }

        public bool TryGetCargoDeckLocalOffset(out Vector3 localOffset)
        {
            if (!IsLogisticsHauler || Unit == null || Spec == null)
            {
                localOffset = Vector3.zero;
                return false;
            }

            float targetBottomY = Rig?.RootObject != null
                ? Rig.RootObject.transform.localPosition.y + Rig.Bounds.min.y
                : -0.954f;
            float deckHeight = Spec.CargoDeckHeightMeters > 0.1f ? Spec.CargoDeckHeightMeters : 1.715f;
            localOffset = new Vector3(0f, targetBottomY + deckHeight, Spec.CargoDeckOffsetZMeters);
            return true;
        }

        internal void RequestDeployState(bool deploy)
        {
            if (SuspendedInTransit || IsVehicleMoving)
            {
                deploy = false;
            }

            PopUpRequested = deploy;
            if (Animator == null)
            {
                return;
            }

            if (deploy)
            {
                Animator.SetTargetDeployed(true);
                return;
            }

            // Wait for turret return-to-rest before stowing clamshell roof.
            if (TurretAim != null && !TurretAim.ReadyToStow)
            {
                TurretAim.ClearAim();
                TurretAim.HideBeam();
                return;
            }

            Animator.SetTargetDeployed(false);
        }

        internal void TickSite(float deltaTime)
        {
            if (!IsAlive)
            {
                return;
            }

            if (CooldownRemaining > 0f)
            {
                CooldownRemaining = Mathf.Max(0f, CooldownRemaining - deltaTime);
            }

            if (Spec.IsMobileVehicle && Unit is GroundVehicle && Unit != null)
            {
                Vector3 currPos = Unit.transform.position;
                bool rbMoving = Unit.rb != null && Unit.rb.velocity.sqrMagnitude > 0.25f;
                bool posMoving = hasLastVehiclePos && (currPos - lastVehiclePos).sqrMagnitude > 0.04f;
                lastVehiclePos = currPos;
                hasLastVehiclePos = true;
                IsVehicleMoving = rbMoving || posMoving;
                if (IsVehicleMoving)
                {
                    PopUpRequested = false;
                }
            }
            else
            {
                IsVehicleMoving = false;
            }

            if (CurrentJamLevel > 0f && Time.time - LastJamTime > 2.0f)
            {
                CurrentJamLevel = Mathf.Max(0f, CurrentJamLevel - (deltaTime * 0.45f));
            }

            bool deployDesired = IsCommandCenter
                ? !SuspendedInTransit
                : IsRadarPrimaryHub
                    ? (!SuspendedInTransit && EmconState != FcuEmconState.AradShutdown)
                    : (!SuspendedInTransit && !IsVehicleMoving && PopUpRequested);
            bool stowRequested = !deployDesired;

            if (IsHoldFireActive)
            {
                TurretAim?.ClearAim();
                TurretAim?.HideBeam();
                ActiveHelTargetId = default;
                ActiveHelDwellSeconds = 0f;
            }

            if (subscribedBuilding != null && !subscribedBuilding.needsRepair && IsAnyPartDamaged())
            {
                subscribedBuilding.Building_OnPartDamage(default);
            }

            if (stowRequested && TurretAim != null && !TurretAim.ReadyToStow)
            {
                TurretAim.ClearAim();
                TurretAim.HideBeam();
            }

            TurretAim?.TickAim(deltaTime, IsRadarEmitting, stowRequested);

            if (Animator != null)
            {
                if (!stowRequested || TurretAim == null || TurretAim.ReadyToStow)
                {
                    Animator.SetTargetDeployed(deployDesired);
                }

                bool allowTurretAim = Animator.IsFullyDeployed && deployDesired;
                Animator.TickAnimation(deltaTime, allowTurretAim);
            }

            SyncDynamicRcs();
            SyncNativeRadarState();

            if (!SuspendedInTransit && !IsVehicleMoving && Unit is Container)
            {
                Unit.radarAlt = 0f;
                Unit.speed = 0f;
            }

            RequestRearmIfNeeded();
        }

        internal void NotifyJammed(Unit? jammerUnit, float jamAmount)
        {
            if (!IsRadarEmitting)
            {
                return;
            }

            LastJamTime = Time.time;
            LastJammerUnit = jammerUnit;
            float baseTol = Spec.SensorProfile != null ? Mathf.Clamp01(Spec.SensorProfile.JamTolerance) : 0.5f;
            float tol = Mathf.Clamp(baseTol + EffectiveNetworkEccmBonus, 0f, 0.92f);
            float effectiveIncrease = jamAmount * (1f - (tol * 0.65f));
            CurrentJamLevel = Mathf.Clamp01(Mathf.Max(CurrentJamLevel, effectiveIncrease));
        }

        internal UnitPart? GetPrimaryUnitPart()
        {
            if (cachedParts != null)
            {
                for (int i = 0; i < cachedParts.Length; i++)
                {
                    if (cachedParts[i] != null)
                    {
                        return cachedParts[i];
                    }
                }
            }

            return Unit != null && Unit.partLookup != null && Unit.partLookup.Count > 0 ? Unit.partLookup[0] : null;
        }

        private void ApplyNativeSensorProfile()
        {
            if (Spec.SensorProfile == null)
            {
                return;
            }

            if (NativeRadar != null)
            {
                if (IsCommandCenter)
                {
                    NativeRadar.activated = false;
                    NativeRadar.enabled = false;
                }
                else
                {
                    if (GameInteropHelpers.RadarJamToleranceField != null)
                    {
                        GameInteropHelpers.RadarJamToleranceField.SetValue(NativeRadar, Spec.SensorProfile.JamTolerance);
                    }

                    RadarParams rp = NativeRadar.RadarParameters;
                    rp.maxRange = Mathf.Max(2000f, Spec.SensorProfile.DetectionRangeKm * 1000f);
                    NativeRadar.RadarParameters = rp;
                }
            }

            if (NativeDetector != null)
            {
                if (GameInteropHelpers.TargetDetectorSharedField != null)
                {
                    GameInteropHelpers.TargetDetectorSharedField.SetValue(NativeDetector, Spec.SensorProfile.IsFireControlUnit);
                }

                if (GameInteropHelpers.TargetDetectorVisualRangeField != null)
                {
                    GameInteropHelpers.TargetDetectorVisualRangeField.SetValue(
                        NativeDetector,
                        Mathf.Max(1500f, Spec.SensorProfile.EoIrEmergencyRangeKm * 1000f));
                }
            }
        }

        private void SyncDynamicRcs()
        {
            if (Unit == null)
            {
                return;
            }

            float targetRcs = CurrentEffectiveRcs;
            float delta = targetRcs - currentAppliedRcs;
            if (Mathf.Abs(delta) > 0.01f)
            {
                Unit.ModifyRCS(delta);
                if (Unit.RCS < 0.02f)
                {
                    Unit.RCS = targetRcs;
                }

                currentAppliedRcs = targetRcs;
            }
        }

        private void SyncNativeRadarState()
        {
            bool isTrueRadarSite = Spec.SensorProfile != null &&
                                   (Spec.SensorProfile.IsFireControlUnit || Spec.Role == WeaponCascadeRole.EarlyWarningRadar);
            bool activeNow = IsRadarEmitting;
            if (NativeRadar != null)
            {
                NativeRadar.activated = isTrueRadarSite && activeNow;
            }
            else if (NativeDetector != null)
            {
                NativeDetector.activated = activeNow || (IsDeployed && Spec.SensorProfile != null && Spec.SensorProfile.EoIrEmergencyRangeKm > 0f);
            }

            if (activeNow != lastReportedRadarActive)
            {
                lastReportedRadarActive = activeNow;
                Vector3 pos = Unit != null ? Unit.transform.position : transform.position;
                RtsBridgeHost.Current.AiThreats.ReportDeployedModularSite(SiteId, Faction, Spec.Role, pos, activeNow);
            }
        }

        internal void HandleBuildingRepaired()
        {
            if (Unit == null)
            {
                return;
            }

            destroyedNotified = false;
            MdsDestructionManager.CleanupWreck(activeWreckRoot);
            activeWreckRoot = null;
            Unit.transform.position = Unit.startPosition.ToLocalPosition();

            Collider[] cols = Unit.GetComponentsInChildren<Collider>(true);
            Transform? rigTf = Rig?.RootObject != null
                ? Rig.RootObject.transform
                : Unit.transform.Find($"MDS_Rig_{Spec.UnitId}");
            for (int i = 0; i < cols.Length; i++)
            {
                Collider? col = cols[i];
                if (col != null && (rigTf == null || !col.transform.IsChildOf(rigTf)))
                {
                    col.enabled = true;
                }
            }

            if (!UnitRegistry.allUnits.Contains(Unit))
            {
                Unit.RegisterUnit(null);
            }

            Unit.onDisableUnit -= OnUnitDisabled;
            Unit.onDisableUnit += OnUnitDisabled;

            if (Rig != null && Rig.RootObject != null)
            {
                Rig.RootObject.SetActive(true);
            }

            if (Unit.NetworkHQ != null)
            {
                Hq = Unit.NetworkHQ;
                FactionAffinity resolvedFaction = GameInteropHelpers.ToFactionAffinity(Hq);
                if (resolvedFaction != FactionAffinity.Any)
                {
                    Faction = resolvedFaction;
                }
            }

            if (initiallyConsumedRtsCap &&
                !ConsumedRtsCap &&
                Spec?.Logistics != null &&
                GameInteropHelpers.IsServerAuthority() &&
                RtsBridgeHost.Current.Caps.TryConsumeCap(Faction, Spec.Logistics.CapCategory, Spec.Logistics.CapWeight))
            {
                ConsumedRtsCap = true;
            }

            RestoreNativeSensorOnRepair();
            ApplyNativeSensorProfile();
            SyncNativeRadarState();

            ModularSiteManager.ReapplyHiddenVanillaVisuals(Unit);
            SensorNetworkManager.Instance?.RegisterSite(this);
            if (Spec != null)
            {
                RtsBridgeHost.Current.AiThreats.ReportDeployedModularSite(
                    SiteId,
                    Faction,
                    Spec.Role,
                    Unit.transform.position,
                    IsRadarEmitting);
                lastReportedRadarActive = IsRadarEmitting;
            }

            MdsLog.Info($"Repaired modular site '{SiteId}' ({Spec?.DisplayName}).");
        }

        private void SnapshotNativeSensorTemplate()
        {
            TargetDetector? source = NativeRadar != null ? (TargetDetector)NativeRadar : NativeDetector;
            if (source == null)
            {
                return;
            }

            hadNativeRadar = NativeRadar != null;
            hadNativeDetectorOnly = !hadNativeRadar && NativeDetector != null;
            nativeSensorHost = source.gameObject;
            cachedScannerTf = GameInteropHelpers.TargetDetectorScannerField?.GetValue(source) as Transform;
            cachedSensorPart = TargetDetectorPartField?.GetValue(source) as UnitPart;
            if (GameInteropHelpers.TargetDetectorCheckIntervalField?.GetValue(source) is float checkIv && checkIv > 0f)
            {
                cachedCheckInterval = checkIv;
            }

            if (GameInteropHelpers.TargetDetectorAlertIntervalField?.GetValue(source) is float alertIv && alertIv >= 0f)
            {
                cachedAlertCheckInterval = alertIv;
            }

            if (GameInteropHelpers.TargetDetectorVisualRangeField?.GetValue(source) is float visRange)
            {
                cachedVisualRange = visRange;
            }

            if (TargetDetectorMagnificationField?.GetValue(source) is float mag)
            {
                cachedMagnification = mag;
            }

            if (TargetDetectorMaxSpeedField?.GetValue(source) is float maxSpd)
            {
                cachedMaxSpeed = maxSpd;
            }

            cachedRotators = TargetDetectorRotatorsField?.GetValue(source);
            if (GameInteropHelpers.TargetDetectorSharedField?.GetValue(source) is bool sharedVal)
            {
                cachedShared = sharedVal;
            }

            if (NativeRadar != null)
            {
                cachedRadarParams = NativeRadar.RadarParameters;
                if (RadarConeField?.GetValue(NativeRadar) is float cone)
                {
                    cachedRadarCone = cone;
                }

                if (RadarPowerDrawField?.GetValue(NativeRadar) is float draw)
                {
                    cachedPowerDraw = draw;
                }
            }
        }

        private void RestoreNativeSensorOnRepair()
        {
            if (Unit == null)
            {
                return;
            }

            if (hadNativeRadar && NativeRadar == null)
            {
                GameObject holder = new GameObject("MDS_RestoredRadar");
                holder.SetActive(false);
                holder.transform.SetParent(Unit.transform, false);
                Radar restored = holder.AddComponent<Radar>();
                ApplyCachedTargetDetectorFields(restored);
                RadarConeField?.SetValue(restored, cachedRadarCone);
                RadarPowerDrawField?.SetValue(restored, cachedPowerDraw);
                restored.RadarParameters = cachedRadarParams;
                holder.SetActive(true);
                TargetDetectorOnInitializeMethod?.Invoke(restored, null);
                NativeRadar = restored;
                NativeDetector = restored;
            }
            else if (hadNativeDetectorOnly && NativeDetector == null)
            {
                GameObject holder = new GameObject("MDS_RestoredDetector");
                holder.SetActive(false);
                holder.transform.SetParent(Unit.transform, false);
                TargetDetector restored = holder.AddComponent<TargetDetector>();
                ApplyCachedTargetDetectorFields(restored);
                holder.SetActive(true);
                TargetDetectorOnInitializeMethod?.Invoke(restored, null);
                NativeDetector = restored;
            }
        }

        private void ApplyCachedTargetDetectorFields(TargetDetector detector)
        {
            if (Unit == null)
            {
                return;
            }

            GameInteropHelpers.TargetDetectorAttachedUnitField?.SetValue(detector, Unit);
            GameInteropHelpers.TargetDetectorScannerField?.SetValue(
                detector,
                cachedScannerTf != null ? cachedScannerTf : Unit.transform);
            TargetDetectorPartField?.SetValue(detector, cachedSensorPart != null ? cachedSensorPart : GetPrimaryUnitPart());
            GameInteropHelpers.TargetDetectorCheckIntervalField?.SetValue(detector, cachedCheckInterval);
            GameInteropHelpers.TargetDetectorAlertIntervalField?.SetValue(detector, cachedAlertCheckInterval);
            GameInteropHelpers.TargetDetectorVisualRangeField?.SetValue(detector, cachedVisualRange);
            TargetDetectorMagnificationField?.SetValue(detector, cachedMagnification);
            TargetDetectorMaxSpeedField?.SetValue(detector, cachedMaxSpeed);
            GameInteropHelpers.TargetDetectorSharedField?.SetValue(detector, cachedShared);

            if (TargetDetectorRotatorsField != null)
            {
                object? rotatorsValue = cachedRotators;
                if (rotatorsValue == null && TargetDetectorRotatorsField.FieldType.GetElementType() is Type elemType)
                {
                    rotatorsValue = Array.CreateInstance(elemType, 0);
                }

                TargetDetectorRotatorsField.SetValue(detector, rotatorsValue);
            }
        }

        private bool IsAnyPartDamaged()
        {
            if (cachedParts != null)
            {
                for (int i = 0; i < cachedParts.Length; i++)
                {
                    if (cachedParts[i] != null && cachedParts[i].hitPoints < 99.5f)
                    {
                        return true;
                    }
                }
            }

            if (Unit != null && Unit.partLookup != null)
            {
                for (int i = 0; i < Unit.partLookup.Count; i++)
                {
                    UnitPart? p = Unit.partLookup[i];
                    if (p != null && p.hitPoints < 99.5f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void OnUnitDisabled(Unit disabledUnit)
        {
            TurretAim?.ClearAim();
            TurretAim?.HideBeam();
            ActiveHelTargetId = default;
            ActiveHelDwellSeconds = 0f;
            if (Rig != null && Rig.RootObject != null)
            {
                Rig.RootObject.SetActive(false);
            }

            if (activeWreckRoot == null && !MdsRuntimeController.IsResettingScene && Unit != null && Package != null && Spec != null)
            {
                Vector3 wreckPos = Unit is Building ? Unit.startPosition.ToLocalPosition() : Unit.transform.position;
                activeWreckRoot = MdsDestructionManager.SpawnDestructionSequence(
                    Unit,
                    Package,
                    Spec,
                    wreckPos,
                    Unit.transform.rotation);
            }

            NotifyDestroyedOnce();
        }

        private void OnDestroy()
        {
            if (subscribedBuilding != null)
            {
                subscribedBuilding.OnRepair -= HandleBuildingRepaired;
                subscribedBuilding = null;
            }

            if (Unit != null)
            {
                Unit.onDisableUnit -= OnUnitDisabled;
            }

            NotifyDestroyedOnce();
        }

        private void NotifyDestroyedOnce()
        {
            if (destroyedNotified)
            {
                return;
            }

            destroyedNotified = true;
            if (!MdsRuntimeController.IsResettingScene)
            {
                SensorNetworkManager.Instance?.UnregisterSite(this);
            }
        }
    }
}
