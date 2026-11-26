using MDSFramework.Api;
using UnityEngine;

namespace MDSFramework.Wta
{
    internal enum WtaEngagementKind
    {
        None = 0,
        LaserHel = 1,
        GunCiws = 2,
        CustomMissile = 3,
        AbmVls = 4
    }

    internal struct ThreatSnapshot
    {
        internal int UnitIndex;
        internal PersistentID UnitId;
        internal Vector3 WorldPos;
        internal Vector3 Velocity;
        internal float AltitudeMeters;
        internal float Rcs;
        internal float BlastYield;
        internal float ThermalHardening;
        internal bool IsMissile;
        internal bool IsAircraft;
        internal bool IsBallistic;
        internal bool IsNuclear;
        internal bool IsArad;
        internal bool IsJammer;
        internal bool IsActivelyJamming;
        internal bool IsJammingFriendlyFcu;
        internal bool IsEmittingRadar;
        internal bool IsDecoy;
        internal bool IsShellOrSmallMunition;
        internal bool IsArtilleryShell;
        internal bool IsShip;
        internal bool IsGroundVehicle;
        internal float TargetMaxHp;
        internal int ExistingMissileAttacks;
        internal int ActiveHojMissileAttacks;
        internal int AssignedThisTick;
        internal int AssignedHojThisTick;
        internal float PriorityScore;
    }

    internal struct EffectorSnapshot
    {
        internal int SiteIndex;
        internal WeaponCascadeRole Role;
        internal Vector3 WorldPos;
        internal float MinRangeMeters;
        internal float MaxRangeMeters;
        internal float FullMaxRangeMeters;
        internal bool IsDeployed;
        internal bool IsReadyToFire;
        internal bool HasFcuGuidance;
        internal bool HasPassiveHojMissile;
        internal float EoIrBackupRangeMeters;
        internal int BandMask;
        internal float JamLevel;
        internal float JamTolerance;
        internal bool IsInCommandNet;
        internal int CommandNetId;
        internal float AmmoRatio;
        internal bool CanEngageGroundVehicles;
    }

    internal struct EngagementOrder
    {
        internal int EffectorSiteIndex;
        internal int ThreatIndex;
        internal WtaEngagementKind Kind;
        internal bool UsePassiveHoj;
        internal bool UseSecondaryMissile;
    }
}
