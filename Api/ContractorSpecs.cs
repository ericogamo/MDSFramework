using System;
using System.Collections.Generic;

namespace MDSFramework.Api
{
    public sealed class ContractorPackage
    {
        private readonly List<ModularUnitSpec> units = new List<ModularUnitSpec>();
        private readonly List<CustomMissileSpec> missiles = new List<CustomMissileSpec>();

        public ContractorPackage()
        {
        }

        public ContractorPackage(
            IEnumerable<ModularUnitSpec>? units,
            IEnumerable<CustomMissileSpec>? missiles)
        {
            if (units != null)
            {
                foreach (ModularUnitSpec u in units)
                {
                    if (u != null)
                    {
                        this.units.Add(u);
                    }
                }
            }

            if (missiles != null)
            {
                foreach (CustomMissileSpec m in missiles)
                {
                    if (m != null)
                    {
                        this.missiles.Add(m);
                    }
                }
            }
        }

        public string PackageId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public FactionAffinity DefaultFaction { get; set; }
        public string AssetRootDirectory { get; set; } = string.Empty;
        public SmartEngagementProfile DefaultWtaProfile { get; set; } = SmartEngagementProfile.CreateDefault();
        public IReadOnlyList<ModularUnitSpec> Units => units;
        public IReadOnlyList<CustomMissileSpec> Missiles => missiles;

        // Additive metadata for contractor customization without breaking binary compatibility.
        public string Description { get; set; } = string.Empty;
        public string BrandingDirectory { get; set; } = string.Empty;

        public void AddUnit(ModularUnitSpec unit)
        {
            if (unit == null)
            {
                throw new ArgumentNullException(nameof(unit));
            }

            units.Add(unit);
        }

        public void AddMissile(CustomMissileSpec missile)
        {
            if (missile == null)
            {
                throw new ArgumentNullException(nameof(missile));
            }

            missiles.Add(missile);
        }
    }

    public sealed class ModularUnitSpec
    {
        public string UnitId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public WeaponCascadeRole Role { get; set; }
        public string BaseDefinitionCloneKey { get; set; } = "Emplacement1_23mm";
        public string GlbRelativePath { get; set; } = string.Empty;
        public float StowedRcs { get; set; } = 0.08f;
        public float DeployedRcs { get; set; } = 0.85f;
        public bool RequiresFcuLink { get; set; }
        public EccmSensorProfile SensorProfile { get; set; } = new EccmSensorProfile();
        public DeploymentLogisticsSpec Logistics { get; set; } = new DeploymentLogisticsSpec();

        // Additive data-driven combat, animation, and fallback parameters.
        public string[] FallbackGlbRelativePaths { get; set; } = Array.Empty<string>();
        public string FallbackMeshArchetype { get; set; } = "container";
        public string DecalRelativePath { get; set; } = string.Empty;
        public string PrimaryMissileId { get; set; } = string.Empty;
        public string SecondaryMissileId { get; set; } = string.Empty;
        public float MinEngagementRangeKm { get; set; } = 0.2f;
        public float MaxEngagementRangeKm { get; set; } = 12f;
        public int MagazineCapacity { get; set; } = 8;
        public int PrimaryMagazineCapacity { get; set; } = -1;
        public int SecondaryMagazineCapacity { get; set; } = 0;
        public float FireCooldownSeconds { get; set; } = 2.5f;
        public float DeployDurationSeconds { get; set; } = 5.0f;
        public float TurretTraverseDegPerSec { get; set; } = 90f;
        public float TurretElevationDegPerSec { get; set; } = 75f;
        public float MinElevationDeg { get; set; } = -10f;
        public float MaxElevationDeg { get; set; } = 85f;
        public float GunMuzzleVelocityMps { get; set; } = 1050f;
        public float GunDamagePerBurst { get; set; } = 32f;
        public string GunProjectileGlbRelativePath { get; set; } = string.Empty;
        public float GunProjectileVisualScale { get; set; } = 5.0f;
        public string GunFireAudioRelativePath { get; set; } = string.Empty;
        public float GunFireAudioVolume { get; set; } = 1.0f;
        public float GunFireAudioPitchMin { get; set; } = 0.96f;
        public float GunFireAudioPitchMax { get; set; } = 1.04f;
        public float GunFireAudioMinDistance { get; set; } = 30f;
        public float GunFireAudioMaxDistance { get; set; } = 2400f;
        public float GunBurstShotIntervalSeconds { get; set; } = 0.06f;
        public float GunRearmCostPerRound { get; set; } = 0.12f;
        public string WeaponIconRelativePath { get; set; } = string.Empty;
        public bool IsCommandCenter { get; set; }
        public float NetworkRelayRadiusKm { get; set; } = 45f;
        public float NetworkEccmBonus { get; set; } = 0.10f;
        public float NetworkDetectionBonusMultiplier { get; set; } = 1.12f;
        public string DestroyedGlbRelativePath { get; set; } = string.Empty;
        public string DebrisKitGlbRelativePath { get; set; } = string.Empty;
        public float DestroyAnimationDurationSeconds { get; set; } = 2.5f;
        public int DestructionDebrisCount { get; set; } = 6;
        public float LaserPowerFactor { get; set; } = 1.0f;
        public bool VerticalLaunch { get; set; }
        public bool SpinRadarDish { get; set; }
        public float RadarSpinDegPerSec { get; set; } = 90f;
        public float FcuLinkRadiusKm { get; set; } = 15f;
        public float ModelYawOffsetDeg { get; set; }
        public bool LaunchAlongSocketForward { get; set; }
        public bool IsMobileVehicle { get; set; }
        public float WheelRadiusMeters { get; set; } = 0.67f;
        public bool IsContainerHauler { get; set; }
        public float CargoDeckHeightMeters { get; set; } = 0f;
        public float CargoDeckOffsetZMeters { get; set; } = 0f;
        public float ContainerLengthMeters { get; set; } = 6.058f;
        public float ContainerWidthMeters { get; set; } = 2.438f;
        public float ContainerHeightMeters { get; set; } = 2.591f;
    }

    public sealed class SmartEngagementProfile
    {
        public string ProfileId { get; set; } = "default";
        public float IdleTickHz { get; set; } = 1f;
        public float ActiveTickHz { get; set; } = 4f;
        public bool PreferLaserBeforeMissiles { get; set; } = true;
        public float LaserDwellSeconds { get; set; } = 1.5f;
        public bool UseSalvoDeconfliction { get; set; } = true;
        public int MaxSimultaneousPerThreat { get; set; } = 2;
        public float AutoPopUpRangeKm { get; set; } = 28f;
        public WeaponCascadeRole[] CascadePriority { get; set; } =
        {
            WeaponCascadeRole.LaserHel,
            WeaponCascadeRole.CloseInGun,
            WeaponCascadeRole.ShortMedSam,
            WeaponCascadeRole.LongRangeSam,
            WeaponCascadeRole.AbmVls
        };

        // Additive doctrine parameters.
        public float StowAfterIdleSeconds { get; set; } = 15f;
        public bool DeprioritizeDetectedDecoys { get; set; } = true;

        public static SmartEngagementProfile CreateDefault()
        {
            return new SmartEngagementProfile();
        }
    }

    public sealed class EccmSensorProfile
    {
        public bool IsFireControlUnit { get; set; }
        public SensorBand[] SupportedBands { get; set; } = { SensorBand.XBand };
        public float JamTolerance { get; set; } = 0.65f;
        public bool EnableTwoFcuBlinking { get; set; }
        public bool EnableHomeOnJamTriangulation { get; set; }
        public float EoIrEmergencyRangeKm { get; set; } = 4.5f;
        public float DetectionRangeKm { get; set; } = 35f;

        // Additive timing parameters for radar blinking & ARAD EMCON.
        public float BlinkOnSeconds { get; set; } = 6f;
        public float BlinkOffSeconds { get; set; } = 4f;
        public float AradEmconShutdownSeconds { get; set; } = 8f;
    }

    public sealed class CustomMissileSpec
    {
        public string MissileId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string GlbRelativePath { get; set; } = string.Empty;
        public MissileFlightProfile FlightProfile { get; set; } = MissileFlightProfile.Direct;
        public float ThermalHardening { get; set; } = 1.0f;
        public bool IsDecoy { get; set; }
        public bool EnableFoldingFins { get; set; }
        public bool SupportsActiveToPassiveHoj { get; set; }
        public bool IsAbmInterceptor { get; set; }
        public float MaxEngagementRangeKm { get; set; } = 45f;
        public float BlastYield { get; set; } = 40f;

        // Additive visual & guidance metadata.
        public string[] FallbackGlbRelativePaths { get; set; } = Array.Empty<string>();
        public string FallbackMeshArchetype { get; set; } = "default";
        public string BaseMissileCloneKey { get; set; } = "SAM_Radar2";
        public string DecalRelativePath { get; set; } = string.Empty;
        public float ModelYawOffsetDeg { get; set; } = 180f;
        public float CruiseSpeedMps { get; set; } = 1250f;
        public float MaxSpeedMps { get; set; } = 0f;
        public float MaxMach { get; set; } = 0f;
        public float LoftApexMeters { get; set; } = 6000f;
        public float SeaSkimAltitudeMeters { get; set; } = 22f;
        public float FinDeployDelaySeconds { get; set; } = 0.35f;
        public float HighAltitudeInterceptMinMeters { get; set; } = 800f;
        public bool DisarmTargetWarheadOnIntercept { get; set; }
        public float RearmCostPerRound { get; set; } = 4f;
        public string IconRelativePath { get; set; } = string.Empty;
        public bool IsAntiShip { get; set; }
        public float TargetMach { get; set; } = 0f;
        public float StealthRcsOverride { get; set; } = 0f;
        public float SeekerEccmJamTolerance { get; set; } = 0.55f;
        public bool HasActiveJammer { get; set; }
        public float ActiveJammerIntensity { get; set; } = 0.35f;
        public float ActiveJammerRangeKm { get; set; } = 12f;
        public float ActiveJammerPulseIntervalSeconds { get; set; } = 0.65f;
        public bool EnableTerminalEvasiveJink { get; set; }
        public float EvasiveJinkStartRangeKm { get; set; } = 5.5f;
        public float EvasiveJinkAmplitudeMeters { get; set; } = 110f;
        public float EvasiveJinkPeriodSeconds { get; set; } = 2.4f;
        public float BoosterDropSeconds { get; set; } = 0f;
        public bool EnableAircraftMounting { get; set; }
        public string[] AircraftMountCloneKeys { get; set; } = Array.Empty<string>();
        public float AircraftMountMassKg { get; set; } = 416f;
        public string AircraftMountShortName { get; set; } = string.Empty;
    }

    public sealed class DeploymentLogisticsSpec
    {
        public int RequiredTechTier { get; set; } = 1;
        public float CreditCost { get; set; } = 100f;
        public string CapCategory { get; set; } = "Ground";
        public int CapWeight { get; set; } = 1;
        public float EngineerBuildSeconds { get; set; } = 15f;
        public bool SupportsTarantulaSlingKit { get; set; } = true;
        public float SlingKitMassKg { get; set; } = 3200f;

        // Additive aircraft airdrop configuration.
        public bool SupportsAircraftAirdrop { get; set; } = true;
        public float AirdropAltitudeMeters { get; set; } = 260f;
        public bool AllowLightCargoBays { get; set; }
    }
}
