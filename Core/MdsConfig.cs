using BepInEx.Configuration;

namespace MDSFramework;

internal static class MdsConfig
{
    // 01 General
    internal static ConfigEntry<bool> ModEnabled { get; private set; } = null!;
    internal static ConfigEntry<bool> VerboseLogging { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableMissionEditorCatalog { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableDebugHudOverlay { get; private set; } = null!;
    internal static ConfigEntry<bool> VanillaFreeSandboxSpawn { get; private set; } = null!;
    internal static ConfigEntry<int> VanillaStartingAbmStockpile { get; private set; } = null!;

    // 02 Smart WTA
    internal static ConfigEntry<bool> EnableSmartWta { get; private set; } = null!;
    internal static ConfigEntry<float> IdleTickHzOverride { get; private set; } = null!;
    internal static ConfigEntry<float> ActiveTickHzOverride { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableAutoPopUp { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableLaserDwell { get; private set; } = null!;

    // 03 ECCM & EW
    internal static ConfigEntry<bool> EnableEccmNetwork { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableTwoFcuBlinking { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableAradAutoEmcon { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableHojTriangulation { get; private set; } = null!;

    // 04 ABM & Missiles
    internal static ConfigEntry<bool> EnableAbmEarlyWarning { get; private set; } = null!;
    internal static ConfigEntry<float> AbmBlastYieldThreshold { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableHighAltitudeDisarm { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableCustomGlbVisuals { get; private set; } = null!;

    // 05 Airdrop & Cargo
    internal static ConfigEntry<bool> EnableNativeAircraftAirdrop { get; private set; } = null!;
    internal static ConfigEntry<bool> AllowAllCargoBays { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoDeployBrieflyOnTouchdown { get; private set; } = null!;

    internal static void Initialize(ConfigFile config)
    {
        // 01 General
        ModEnabled = config.Bind(
            "01 General",
            "ModEnabled",
            true,
            "Enable the Modern Defence System (MDS) Framework.");

        VerboseLogging = config.Bind(
            "01 General",
            "VerboseLogging",
            false,
            "Write detailed diagnostics to BepInEx/LogOutput.log.");

        EnableMissionEditorCatalog = config.Bind(
            "01 General",
            "EnableMissionEditorCatalog",
            true,
            "Register contractor units and missiles into Nuclear Option's Encyclopedia and Mission Editor for standalone mission authoring.");

        EnableDebugHudOverlay = config.Bind(
            "01 General",
            "EnableDebugHudOverlay",
            false,
            "Show compact live Smart-WTA, FCU blinking, HOJ, and ABM early-warning HUD strip.");

        VanillaFreeSandboxSpawn = config.Bind(
            "01 General",
            "VanillaFreeSandboxSpawn",
            true,
            "When no RTS bridge is attached, allow deploying contractor units in vanilla sandbox without charging FactionHQ funds.");

        VanillaStartingAbmStockpile = config.Bind(
            "01 General",
            "VanillaStartingAbmStockpile",
            16,
            new ConfigDescription(
                "Initial ABM interceptor stockpile per faction when running standalone without an RTS ABM plant bridge.",
                new AcceptableValueRange<int>(2, 128)));

        // 02 Smart WTA
        EnableSmartWta = config.Bind(
            "02 Smart WTA",
            "EnableSmartWta",
            true,
            "Enable the zero-allocation Smart Weapon-Target Assignment cascade engine.");

        IdleTickHzOverride = config.Bind(
            "02 Smart WTA",
            "IdleTickHzOverride",
            0f,
            new ConfigDescription(
                "Optional override for Smart WTA idle tick frequency in Hz (0 = use contractor profile default, typically 1 Hz).",
                new AcceptableValueRange<float>(0f, 5f)));

        ActiveTickHzOverride = config.Bind(
            "02 Smart WTA",
            "ActiveTickHzOverride",
            0f,
            new ConfigDescription(
                "Optional override for Smart WTA active tick frequency in Hz (0 = use contractor profile default, typically 4 Hz).",
                new AcceptableValueRange<float>(0f, 10f)));

        EnableAutoPopUp = config.Bind(
            "02 Smart WTA",
            "EnableAutoPopUp",
            true,
            "Automatically deploy stowed stealth containers when threats enter AutoPopUpRangeKm and stow when clear.");

        EnableLaserDwell = config.Bind(
            "02 Smart WTA",
            "EnableLaserDwell",
            true,
            "Enforce HEL laser dwell time scaled by target ThermalHardening.");

        // 03 ECCM & EW
        EnableEccmNetwork = config.Bind(
            "03 ECCM & EW",
            "EnableEccmNetwork",
            true,
            "Enable multi-band ECCM sensor network, EMCON states, and FCU-to-effector linking.");

        EnableTwoFcuBlinking = config.Bind(
            "03 ECCM & EW",
            "EnableTwoFcuBlinking",
            true,
            "Enable staggered 2-FCU radar blinking and auto-handoff against jamming and ARAD threats.");

        EnableAradAutoEmcon = config.Bind(
            "03 ECCM & EW",
            "EnableAradAutoEmcon",
            true,
            "Automatically switch targeted FCUs to EMCON / EO-IR emergency backup when targeted by ARAD seekers.");

        EnableHojTriangulation = config.Bind(
            "03 ECCM & EW",
            "EnableHojTriangulation",
            true,
            "Triangulate hostile jamming pods across linked FCU/ELINT nodes for Home-on-Jam targeting.");

        // 04 ABM & Missiles
        EnableAbmEarlyWarning = config.Bind(
            "04 ABM & Missiles",
            "EnableAbmEarlyWarning",
            true,
            "Monitor BallisticMissileGuidance and high-yield (>200t) launches for early warning and automatic VLS pop-up.");

        AbmBlastYieldThreshold = config.Bind(
            "04 ABM & Missiles",
            "AbmBlastYieldThreshold",
            200f,
            new ConfigDescription(
                "Minimum blastYield considered a strategic/nuclear threat requiring ABM early warning.",
                new AcceptableValueRange<float>(50f, 5000f)));

        EnableHighAltitudeDisarm = config.Bind(
            "04 ABM & Missiles",
            "EnableHighAltitudeDisarm",
            true,
            "Disarm intercepted ballistic/nuclear warheads (warhead.Armed = false) during high-altitude ABM intercepts.");

        EnableCustomGlbVisuals = config.Bind(
            "04 ABM & Missiles",
            "EnableCustomGlbVisuals",
            true,
            "Load contractor hierarchical GLB models, TRS animations, and decals at runtime (uses procedural fallbacks if disabled or missing).");

        // 05 Airdrop & Cargo
        EnableNativeAircraftAirdrop = config.Bind(
            "05 Airdrop & Cargo",
            "EnableNativeAircraftAirdrop",
            true,
            "Register airdrop-capable MDS containers into Nuclear Option aircraft cargo bays (Alkyon AB-4, VL-49 Tarantula).");

        AllowAllCargoBays = config.Bind(
            "05 Airdrop & Cargo",
            "AllowAllCargoBays",
            false,
            "Allow mounting MDS 20ft containers on smaller pallet bays (e.g., UH-90 Ibis) in addition to container cargo bays.");

        AutoDeployBrieflyOnTouchdown = config.Bind(
            "05 Airdrop & Cargo",
            "AutoDeployBrieflyOnTouchdown",
            true,
            "Unfold container clamshell briefly after parachute touchdown before Smart-WTA auto-stow takes over.");
    }
}
