using System;
using System.Collections.Generic;
using MDSFramework.Api;
using MDSFramework.Assets3D;
using MDSFramework.Deployment;
using UnityEngine;

namespace MDSFramework.Sensors
{
    /// <summary>
    /// Client-side mirror of ModularSiteBehavior driven by MdsNetworkSync snapshots.
    /// Ensures clients see live deploy/stow animations, turret tracking, EMCON radar spins,
    /// synced ammo counts, dynamic RCS, and Commander UI inspection status without running Host WTA.
    /// </summary>
    public sealed class MdsClientSiteProxy : MonoBehaviour
    {
        private static readonly Dictionary<uint, MdsClientSiteProxy> ByPersistentId = new Dictionary<uint, MdsClientSiteProxy>();

        internal ContractorPackage Package { get; private set; } = null!;
        internal ModularUnitSpec Spec { get; private set; } = null!;
        internal Unit Unit { get; private set; } = null!;
        internal GlbInstantiatedRig? Rig { get; private set; }
        internal GlbKeyframedAnimator? Animator { get; private set; }
        internal GlbTurretAimController? TurretAim { get; private set; }
        internal readonly List<MdsWeaponStationProxy> WeaponProxies = new List<MdsWeaponStationProxy>(2);

        private uint registeredPid;
        private bool hasReceivedSnapshot;
        private bool targetDeployed;
        private bool radarEmitting;
        private bool holdFire;
        private bool suspendedInTransit;
        private bool inCommandNet;
        private bool hasActiveFcuLink;
        private bool isVehicleMoving;
        private int primaryAmmo;
        private int secondaryAmmo;
        private int primaryCap;
        private int secondaryCap;
        private int fcuCount;
        private int activeFcuCount;
        private int effectorCount;
        private int commandNetId;
        private bool containerLandedApplied;

        public bool IsDeployed => !isVehicleMoving && (Animator == null || Animator.IsFullyDeployed);
        public bool IsRadarEmitting => radarEmitting && !suspendedInTransit && Unit != null && !Unit.disabled;
        public bool IsHoldFireActive => holdFire;
        public bool LocalHoldFire => holdFire;
        public bool IsCommandCenter => Spec != null && (Spec.Role == WeaponCascadeRole.CommandControl || Spec.IsCommandCenter);
        public bool IsFireControlRadar => Spec != null &&
            (Spec.Role == WeaponCascadeRole.FireControl ||
             Spec.Role == WeaponCascadeRole.EarlyWarningRadar ||
             (Spec.SensorProfile != null && Spec.SensorProfile.IsFireControlUnit));
        public bool IsLogisticsHauler => Spec != null && (Spec.Role == WeaponCascadeRole.LogisticsHauler || Spec.IsContainerHauler);
        public bool IsWeaponSite => Spec != null &&
            Spec.Role != WeaponCascadeRole.FireControl &&
            Spec.Role != WeaponCascadeRole.EarlyWarningRadar &&
            Spec.Role != WeaponCascadeRole.CommandControl &&
            !IsLogisticsHauler;
        public bool RequiresFcuLink => Spec != null && Spec.RequiresFcuLink;
        public bool HasActiveFcuLink => !RequiresFcuLink || hasActiveFcuLink;
        public float FcuLinkRadiusKm => Spec != null ? Spec.FcuLinkRadiusKm : 18f;
        public float EoIrBackupRangeKm => Spec?.SensorProfile != null ? Spec.SensorProfile.EoIrEmergencyRangeKm : 0f;
        public bool IsInCommandNet => inCommandNet;
        public int CommandNetFcuCount => fcuCount;
        public int CommandNetActiveFcuCount => activeFcuCount;
        public int CommandNetEffectorCount => effectorCount;
        public int CommandNetId => commandNetId;
        public int PrimaryAmmoRemaining => primaryAmmo;
        public int SecondaryAmmoRemaining => secondaryAmmo;
        public int PrimaryMagazineSize => primaryCap;
        public int SecondaryMagazineSize => secondaryCap;
        public bool SuspendedInTransit => suspendedInTransit;

        internal static MdsClientSiteProxy? FindByPersistentId(uint pidId)
        {
            if (pidId == 0)
            {
                return null;
            }

            if (ByPersistentId.TryGetValue(pidId, out MdsClientSiteProxy? proxy) && proxy != null && proxy.Unit != null)
            {
                return proxy;
            }

            return null;
        }

        internal static void ResetSession()
        {
            ByPersistentId.Clear();
        }

        internal void Initialize(
            Unit unit,
            ContractorPackage package,
            ModularUnitSpec spec,
            GlbInstantiatedRig? rig,
            GlbKeyframedAnimator? animator,
            GlbTurretAimController? turretAim,
            bool initialDeployed)
        {
            Unit = unit;
            Package = package;
            Spec = spec;
            Rig = rig;
            Animator = animator;
            TurretAim = turretAim;
            targetDeployed = initialDeployed;
            radarEmitting = initialDeployed && (IsFireControlRadar || (spec.SensorProfile != null && spec.SpinRadarDish));
            hasActiveFcuLink = !RequiresFcuLink;

            primaryCap = spec.PrimaryMagazineCapacity >= 0
                ? spec.PrimaryMagazineCapacity
                : Math.Max(0, spec.MagazineCapacity);
            secondaryCap = Math.Max(0, spec.SecondaryMagazineCapacity);
            primaryAmmo = primaryCap;
            secondaryAmmo = secondaryCap;

            if (unit != null && unit.persistentID.IsValid)
            {
                registeredPid = unit.persistentID.Id;
                ByPersistentId[registeredPid] = this;
            }
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

        internal void ApplyNetworkSnapshot(
            int flags,
            float animPct,
            float yawDeg,
            float pitchDeg,
            float rcs,
            int ammo1,
            int ammo2,
            int fcuCnt,
            int activeFcuCnt,
            int effCnt,
            int netId)
        {
            if (Unit == null)
            {
                return;
            }

            bool deployDesired = (flags & (1 << 0)) != 0;
            radarEmitting = (flags & (1 << 1)) != 0;
            holdFire = (flags & (1 << 2)) != 0;
            suspendedInTransit = (flags & (1 << 3)) != 0;
            inCommandNet = (flags & (1 << 4)) != 0;
            hasActiveFcuLink = (flags & (1 << 5)) != 0;
            isVehicleMoving = (flags & (1 << 6)) != 0;
            bool animTarget = (flags & (1 << 7)) != 0;

            targetDeployed = deployDesired || animTarget;
            fcuCount = fcuCnt;
            activeFcuCount = activeFcuCnt;
            effectorCount = effCnt;
            commandNetId = netId;

            Unit.RCS = Mathf.Max(0.02f, rcs);

            if (Animator != null)
            {
                if (!hasReceivedSnapshot || Mathf.Abs(Animator.NormalizedProgress - animPct) > 0.45f)
                {
                    Animator.ForceProgress(animPct);
                }

                Animator.SetTargetDeployed(animTarget);
            }

            if (TurretAim != null && !holdFire && !suspendedInTransit && targetDeployed)
            {
                TurretAim.SetReplicatedAngles(yawDeg, pitchDeg);
            }
            else
            {
                TurretAim?.ClearAim();
                if (holdFire || suspendedInTransit)
                {
                    TurretAim?.HideBeam();
                }
            }

            primaryAmmo = Mathf.Clamp(ammo1, 0, Math.Max(0, primaryCap));
            secondaryAmmo = Mathf.Clamp(ammo2, 0, Math.Max(0, secondaryCap));
            for (int i = 0; i < WeaponProxies.Count; i++)
            {
                MdsWeaponStationProxy? proxy = WeaponProxies[i];
                if (proxy != null)
                {
                    int val = proxy.IsSecondarySlot ? secondaryAmmo : primaryAmmo;
                    proxy.SetReplicatedAmmo(val);
                }
            }

            hasReceivedSnapshot = true;
        }

        internal void ApplyContainerLanded(float yawDeg, float lengthMeters)
        {
            if (Unit == null || containerLandedApplied)
            {
                return;
            }

            containerLandedApplied = true;
            CargoDeploymentSystem[] crates = Unit.GetComponentsInChildren<CargoDeploymentSystem>(true);
            for (int i = 0; i < crates.Length; i++)
            {
                if (crates[i] != null && crates[i].gameObject != Unit.gameObject)
                {
                    Destroy(crates[i].gameObject);
                }
            }

            ModularSiteManager.RemoveVanillaRearmer(Unit, Spec);
            ModularSiteManager.DisableVanillaAutoWeapons(Unit.gameObject, Spec.Role);

            if (Unit is Container container && lengthMeters > 6.5f)
            {
                BoxCollider? box = container.GetComponent<BoxCollider>();
                if (box != null)
                {
                    float width = Mathf.Max(2.2f, Spec.ContainerWidthMeters);
                    float height = Mathf.Max(2.2f, Spec.ContainerHeightMeters);
                    box.size = new Vector3(width, height, lengthMeters);
                }
            }

            Unit.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);
            Physics.SyncTransforms();
        }

        private void Update()
        {
            if (Unit == null || Unit.disabled)
            {
                return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f)
            {
                return;
            }

            if (Unit.persistentID.IsValid && registeredPid != Unit.persistentID.Id)
            {
                if (registeredPid != 0)
                {
                    ByPersistentId.Remove(registeredPid);
                }

                registeredPid = Unit.persistentID.Id;
                ByPersistentId[registeredPid] = this;
            }

            bool stowRequested = !targetDeployed || suspendedInTransit || isVehicleMoving;
            if (Animator != null)
            {
                bool allowTurretOverrides = !stowRequested && Animator.IsFullyDeployed;
                Animator.TickAnimation(dt, allowTurretOverrides);
            }

            TurretAim?.TickReplicatedClient(dt, IsRadarEmitting, stowRequested);
        }

        private void OnDestroy()
        {
            if (registeredPid != 0 && ByPersistentId.TryGetValue(registeredPid, out MdsClientSiteProxy? cur) && ReferenceEquals(cur, this))
            {
                ByPersistentId.Remove(registeredPid);
            }
        }
    }
}
