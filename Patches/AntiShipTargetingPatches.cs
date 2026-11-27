using System.Collections.Generic;
using HarmonyLib;
using MDSFramework.Api;
using UnityEngine;

namespace MDSFramework.Patches
{
    internal static class AntiShipTargetingPatches
    {
        private static float lastHudWarningTime = -10f;

        internal static bool IsValidAntiShipTarget(Unit? target)
        {
            return target != null && !target.disabled && target is Ship;
        }

        internal static void ReportShipsOnlyIfLocalPlayer(Unit? owner, string? weaponName)
        {
            if (owner == null ||
                !GameManager.GetLocalAircraft(out Aircraft localAircraft) ||
                !ReferenceEquals(owner, localAircraft))
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now - lastHudWarningTime < 1.5f)
            {
                return;
            }

            lastHudWarningTime = now;
            string label = string.IsNullOrWhiteSpace(weaponName) ? "SeaWraith" : weaponName!;
            if (SceneSingleton<AircraftActionsReport>.i != null)
            {
                SceneSingleton<AircraftActionsReport>.i.ReportText($"{label}: Ships only!", 3f);
            }
        }
    }

    [HarmonyPatch(
        typeof(WeaponStation),
        nameof(WeaponStation.CalcOpportunityThreat),
        new[] { typeof(UnitDefinition), typeof(Unit) })]
    internal static class WeaponStationCalcOpportunityThreatAntiShipPatch
    {
        private static void Postfix(
            WeaponStation __instance,
            UnitDefinition definition,
            ref OpportunityThreat __result)
        {
            if (__instance == null ||
                __instance.WeaponInfo == null ||
                definition == null ||
                definition is ShipDefinition ||
                !ContractorRegistry.IsAntiShipWeaponInfo(__instance.WeaponInfo))
            {
                return;
            }

            OpportunityThreat zeroOpp = new OpportunityThreat(0f, __result.threat);
            __result = zeroOpp;

            if (GameInteropHelpers.WeaponStationTypeLookupField?.GetValue(__instance) is Dictionary<UnitDefinition, OpportunityThreat> lookup &&
                lookup.ContainsKey(definition))
            {
                lookup[definition] = zeroOpp;
            }
        }
    }

    [HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.Fire), new System.Type[0])]
    internal static class WeaponManagerFireAntiShipPatch
    {
        private static bool Prefix(WeaponManager __instance)
        {
            if (__instance == null || __instance.currentWeaponStation == null)
            {
                return true;
            }

            WeaponInfo? info = __instance.currentWeaponStation.WeaponInfo;
            if (info == null || !ContractorRegistry.IsAntiShipWeaponInfo(info))
            {
                return true;
            }

            List<Unit>? targets = __instance.GetTargetList();
            if (targets != null && targets.Count > 0 && AntiShipTargetingPatches.IsValidAntiShipTarget(targets[0]))
            {
                return true;
            }

            Unit? owner = GameInteropHelpers.WeaponManagerAircraftField?.GetValue(__instance) as Unit;
            string label = string.IsNullOrEmpty(info.shortName) ? info.weaponName : info.shortName;
            AntiShipTargetingPatches.ReportShipsOnlyIfLocalPlayer(owner, label);
            return false;
        }
    }

    [HarmonyPatch(
        typeof(WeaponStation),
        nameof(WeaponStation.LaunchMount),
        new[] { typeof(Unit), typeof(Unit), typeof(GlobalPosition) })]
    internal static class WeaponStationLaunchMountAntiShipPatch
    {
        private static bool Prefix(WeaponStation __instance, Unit owner, Unit target)
        {
            if (__instance == null ||
                __instance.WeaponInfo == null ||
                !ContractorRegistry.IsAntiShipWeaponInfo(__instance.WeaponInfo))
            {
                return true;
            }

            if (owner != null &&
                !owner.IsServer &&
                !owner.HasAuthority &&
                GameManager.gameState != GameState.SinglePlayer)
            {
                return true;
            }

            if (AntiShipTargetingPatches.IsValidAntiShipTarget(target))
            {
                return true;
            }

            string label = string.IsNullOrEmpty(__instance.WeaponInfo.shortName)
                ? __instance.WeaponInfo.weaponName
                : __instance.WeaponInfo.shortName;
            AntiShipTargetingPatches.ReportShipsOnlyIfLocalPlayer(owner, label);
            return false;
        }
    }

    [HarmonyPatch(
        typeof(WeaponStation),
        nameof(WeaponStation.Fire),
        new[] { typeof(Unit), typeof(Unit) })]
    internal static class WeaponStationFireAntiShipPatch
    {
        private static bool Prefix(WeaponStation __instance, Unit owner, Unit target)
        {
            if (__instance == null ||
                __instance.WeaponInfo == null ||
                !ContractorRegistry.IsAntiShipWeaponInfo(__instance.WeaponInfo))
            {
                return true;
            }

            if (owner != null &&
                !owner.IsServer &&
                !owner.HasAuthority &&
                GameManager.gameState != GameState.SinglePlayer)
            {
                return true;
            }

            return AntiShipTargetingPatches.IsValidAntiShipTarget(target);
        }
    }

    [HarmonyPatch(
        typeof(CombatAI),
        nameof(CombatAI.LookForMissileTargets),
        new[] { typeof(Aircraft), typeof(Unit), typeof(WeaponStation), typeof(List<Unit>) })]
    internal static class CombatAiLookForMissileTargetsAntiShipPatch
    {
        private static bool Prefix(
            Unit currentTarget,
            WeaponStation weaponStation,
            List<Unit> outTargets,
            ref int __result)
        {
            if (weaponStation == null ||
                weaponStation.WeaponInfo == null ||
                !ContractorRegistry.IsAntiShipWeaponInfo(weaponStation.WeaponInfo))
            {
                return true;
            }

            if (AntiShipTargetingPatches.IsValidAntiShipTarget(currentTarget))
            {
                return true;
            }

            outTargets?.Clear();
            __result = 0;
            return false;
        }

        private static void Postfix(
            WeaponStation weaponStation,
            List<Unit> outTargets,
            ref int __result)
        {
            if (outTargets == null ||
                weaponStation == null ||
                weaponStation.WeaponInfo == null ||
                !ContractorRegistry.IsAntiShipWeaponInfo(weaponStation.WeaponInfo))
            {
                return;
            }

            for (int i = outTargets.Count - 1; i >= 0; i--)
            {
                if (!AntiShipTargetingPatches.IsValidAntiShipTarget(outTargets[i]))
                {
                    outTargets.RemoveAt(i);
                }
            }

            __result = outTargets.Count;
        }
    }
}
