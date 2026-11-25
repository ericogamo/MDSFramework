using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MDSFramework.Api;
using NuclearOption.Networking;
using UnityEngine;

namespace MDSFramework;

internal static class GameInteropHelpers
{
    internal static readonly FieldInfo? RadarJamToleranceField = AccessTools.Field(typeof(Radar), "jamTolerance");
    internal static readonly FieldInfo? RadarJamAccumulationField = AccessTools.Field(typeof(Radar), "jamAccumulation");
    internal static readonly FieldInfo? TargetDetectorSharedField = AccessTools.Field(typeof(TargetDetector), "shared");
    internal static readonly FieldInfo? TargetDetectorVisualRangeField = AccessTools.Field(typeof(TargetDetector), "visualRange");
    internal static readonly FieldInfo? TargetDetectorCheckIntervalField = AccessTools.Field(typeof(TargetDetector), "checkInterval");
    internal static readonly FieldInfo? TargetDetectorAlertIntervalField = AccessTools.Field(typeof(TargetDetector), "alertCheckInterval");
    internal static readonly FieldInfo? TargetDetectorAttachedUnitField = AccessTools.Field(typeof(TargetDetector), "attachedUnit");
    internal static readonly FieldInfo? TargetDetectorScannerField = AccessTools.Field(typeof(TargetDetector), "scanner");

    internal static readonly FieldInfo? MissileWarheadField = AccessTools.Field(typeof(Missile), "warhead");
    internal static readonly FieldInfo? MissileBlastYieldField = AccessTools.Field(typeof(Missile), "blastYield");
    internal static readonly FieldInfo? MissileSeekerField = AccessTools.Field(typeof(Missile), "seeker");
    internal static readonly FieldInfo? MissileTargetField = AccessTools.Field(typeof(Missile), "target");
    internal static readonly FieldInfo? MissileInfoField = AccessTools.Field(typeof(Missile), "info");
    internal static readonly FieldInfo? MissileMotorsField = AccessTools.Field(typeof(Missile), "motors");
    internal static readonly FieldInfo? MissileHitpointsField = AccessTools.Field(typeof(Missile), "hitpoints");

    internal static readonly FieldInfo? ArhHomeOnJamField = AccessTools.Field(typeof(ARHSeeker), "homeOnJam");
    internal static readonly FieldInfo? ArhJamToleranceField = AccessTools.Field(typeof(ARHSeeker), "jamTolerance");
    internal static readonly FieldInfo? ArhJamAccumulationField = AccessTools.Field(typeof(ARHSeeker), "jamAccumulation");
    internal static readonly FieldInfo? ArhIsJammedField = AccessTools.Field(typeof(ARHSeeker), "isJammed");
    internal static readonly FieldInfo? ArhLoftAmountField = AccessTools.Field(typeof(ARHSeeker), "loftAmount");
    internal static readonly FieldInfo? ArhKnownPosField = AccessTools.Field(typeof(ARHSeeker), "knownPos");
    internal static readonly FieldInfo? ArhKnownVelField = AccessTools.Field(typeof(ARHSeeker), "knownVel");
    internal static readonly FieldInfo? ArhRadarLockField = AccessTools.Field(typeof(ARHSeeker), "radarLockEstablished");
    internal static readonly FieldInfo? ArhMaxDatalinkAngleField = AccessTools.Field(typeof(ARHSeeker), "maxDatalinkAngle");
    internal static readonly FieldInfo? ArhMaxTrackingAngleField = AccessTools.Field(typeof(ARHSeeker), "maxTrackingAngle");
    internal static readonly FieldInfo? ArhSelfDestructSpeedField = AccessTools.Field(typeof(ARHSeeker), "selfDestructAtSpeed");
    internal static readonly FieldInfo? ArhGuidanceDelayField = AccessTools.Field(typeof(ARHSeeker), "guidanceDelay");
    internal static readonly FieldInfo? ArhArmDelayField = AccessTools.Field(typeof(ARHSeeker), "armDelay");
    internal static readonly FieldInfo? ArhTargetDistField = AccessTools.Field(typeof(ARHSeeker), "targetDist");
    internal static readonly FieldInfo? ArhTimeToTargetField = AccessTools.Field(typeof(ARHSeeker), "timeToTarget");
    internal static readonly FieldInfo? ArhMultipleInboundField = AccessTools.Field(typeof(ARHSeeker), "multipleInbound");

    internal static readonly FieldInfo? ArmTargetedRadarField = AccessTools.Field(typeof(ARMSeeker), "targetedRadar");
    internal static readonly FieldInfo? SeekerMissileField = AccessTools.Field(typeof(MissileSeeker), "missile");
    internal static readonly FieldInfo? SeekerTargetUnitField = AccessTools.Field(typeof(MissileSeeker), "targetUnit");

    internal static readonly FieldInfo? LaserPowerField = AccessTools.Field(typeof(Laser), "power");
    internal static readonly FieldInfo? LaserFireDamageField = AccessTools.Field(typeof(Laser), "fireDamage");
    internal static readonly FieldInfo? LaserBlastDamageField = AccessTools.Field(typeof(Laser), "blastDamage");
    internal static readonly FieldInfo? LaserCurrentTargetField = AccessTools.Field(typeof(Laser), "currentTarget");

    internal static readonly FieldInfo? ContainerParachuteSystemField = AccessTools.Field(typeof(Container), "parachuteSystem");
    internal static readonly FieldInfo? WeaponMountDisabledField = AccessTools.Field(typeof(WeaponMount), "disabled");
    internal static readonly FieldInfo? WeaponMountEventField = AccessTools.Field(typeof(WeaponMount), "isEventContent");
    internal static readonly FieldInfo? UnitDefinitionDisabledField = AccessTools.Field(typeof(UnitDefinition), "disabled");
    internal static readonly FieldInfo? UnitDefinitionEventField = AccessTools.Field(typeof(UnitDefinition), "isEventContent");
    internal static readonly FieldInfo? EncyclopediaAircraftAndVehiclesCacheField = AccessTools.Field(typeof(Encyclopedia), "aircraftAndVehiclesCache");
    internal static readonly FieldInfo? NewUnitPanelUnitProvidersField = AccessTools.Field(typeof(NuclearOption.MissionEditorScripts.Buttons.NewUnitPanel), "unitProviders");
    internal static readonly FieldInfo? NewUnitPanelPlacingObjectField = AccessTools.Field(typeof(NuclearOption.MissionEditorScripts.Buttons.NewUnitPanel), "placingObject");
    internal static readonly FieldInfo? EncyclopediaBrowserSpawnedPartsField = AccessTools.Field(typeof(EncyclopediaBrowser), "spawnedParts");
    internal static readonly MethodInfo? EncyclopediaBrowserUpdateWeaponDisplayMethod = AccessTools.Method(typeof(EncyclopediaBrowser), "UpdateWeaponDisplay", new[] { typeof(Unit) });

    internal static readonly FieldInfo? BuildingRepairTimeField = AccessTools.Field(typeof(Building), "repairTime");
    internal static readonly FieldInfo? BuildingNeedsRepairField = AccessTools.Field(typeof(Building), "needsRepair");
    internal static readonly FieldInfo? BuildingHideOnMapField = AccessTools.Field(typeof(Building), "hideOnMap");
    internal static readonly FieldInfo? BuildingWreckageField = AccessTools.Field(typeof(Building), "wreckage");

    private static class GroundVehicleFieldCache
    {
        internal static readonly FieldInfo? Wheels = AccessTools.Field(typeof(GroundVehicle), "wheels");
        internal static readonly FieldInfo? WheelRadius = AccessTools.Field(typeof(GroundVehicle), "wheelRadius");
    }

    internal static FieldInfo? GroundVehicleWheelsField => GroundVehicleFieldCache.Wheels;
    internal static FieldInfo? GroundVehicleWheelRadiusField => GroundVehicleFieldCache.WheelRadius;
    internal static readonly FieldInfo? OpticalCruiseAltTargetField = AccessTools.Field(typeof(OpticalSeekerCruiseMissile), "altitudeTarget");
    internal static readonly FieldInfo? OpticalCruiseTermRangeField = AccessTools.Field(typeof(OpticalSeekerCruiseMissile), "terminalRange");
    internal static readonly FieldInfo? OpticalCruiseFinDelayField = AccessTools.Field(typeof(OpticalSeekerCruiseMissile), "finDelay");
    internal static readonly FieldInfo? OpticalCruiseGuidanceDelayField = AccessTools.Field(typeof(OpticalSeekerCruiseMissile), "guidanceDelay");
    internal static readonly FieldInfo? HardpointPylonMountField = AccessTools.Field(typeof(Hardpoint).GetNestedType("HardpointPylon", BindingFlags.NonPublic | BindingFlags.Public), "mount");
    internal static readonly FieldInfo? MountedMissileMountField = AccessTools.Field(typeof(Weapon), "mount");
    internal static readonly FieldInfo? WeaponStationTypeLookupField = AccessTools.Field(typeof(WeaponStation), "TypeLookup");
    internal static readonly FieldInfo? WeaponManagerAircraftField = AccessTools.Field(typeof(WeaponManager), "aircraft");

    private static readonly string[] BuildingFallbacks =
    {
        "Emplacement1_23mm",
        "Emplacement1_MANPADS",
        "Emplacement1_ATGM",
        "radarStation1",
        "controlTower1",
    };

    private static readonly string[] MissileFallbacks =
    {
        "SAM_Radar2",
        "SAM_Radar1",
        "missile_radar1",
        "SAM_IR1",
        "AAM_ARH1",
    };

    internal static bool IsGameplayActive()
    {
        if (Encyclopedia.i == null)
        {
            return false;
        }

        GameState state = GameManager.gameState;
        return state == GameState.SinglePlayer || state == GameState.Multiplayer;
    }

    internal static bool IsServerAuthority()
    {
        if (GameManager.gameState == GameState.SinglePlayer)
        {
            return true;
        }

        return GameManager.gameState == GameState.Multiplayer &&
               NetworkManagerNuclearOption.i != null &&
               NetworkManagerNuclearOption.i.Server != null &&
               NetworkManagerNuclearOption.i.Server.Active;
    }

    internal static bool TryGetLocalPlayerFaction(out FactionAffinity faction, out FactionHQ? hq)
    {
        if (GameManager.GetLocalHQ(out FactionHQ localHq) && localHq != null)
        {
            hq = localHq;
            faction = ToFactionAffinity(localHq);
            return faction != FactionAffinity.Any;
        }

        hq = null;
        faction = FactionAffinity.Any;
        return false;
    }

    internal static FactionAffinity ResolveConcreteFaction(FactionAffinity requested, out FactionHQ? hq)
    {
        hq = TryResolveFactionHQ(requested);
        if (requested != FactionAffinity.Any)
        {
            return requested;
        }

        return ToFactionAffinity(hq);
    }

    internal static FactionAffinity ToFactionAffinity(FactionHQ? hq)
    {
        if (hq == null || hq.faction == null)
        {
            return FactionAffinity.Any;
        }

        return ParseFactionName(hq.faction.factionName, hq.faction.factionTag);
    }

    internal static FactionAffinity ParseFactionName(string? factionName, string? factionTag = null)
    {
        string combined = $"{factionName} {factionTag}";
        if (combined.IndexOf("Boscali", StringComparison.OrdinalIgnoreCase) >= 0 ||
            combined.IndexOf("BDF", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return FactionAffinity.Boscali;
        }

        if (combined.IndexOf("Primeva", StringComparison.OrdinalIgnoreCase) >= 0 ||
            combined.IndexOf("PALA", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return FactionAffinity.Primeva;
        }

        return FactionAffinity.Any;
    }

    internal static FactionHQ? TryResolveFactionHQ(FactionAffinity affinity)
    {
        if (affinity == FactionAffinity.Any)
        {
            if (GameManager.GetLocalHQ(out FactionHQ localHq) && localHq != null)
            {
                return localHq;
            }
        }

        try
        {
            foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
            {
                if (hq == null)
                {
                    continue;
                }

                if (affinity == FactionAffinity.Any || ToFactionAffinity(hq) == affinity)
                {
                    return hq;
                }
            }
        }
        catch (Exception ex)
        {
            MdsLog.Debug($"FactionRegistry lookup failed: {ex.Message}");
        }

        if (GameManager.GetLocalHQ(out FactionHQ fallbackHq))
        {
            return fallbackHq;
        }

        return null;
    }

    internal static Airbase? FindNearestAirbase(FactionHQ? hq, Vector3 worldPos)
    {
        if (hq == null)
        {
            return null;
        }

        Airbase? best = null;
        float bestDistSq = float.MaxValue;
        foreach (Airbase ab in hq.GetAirbases())
        {
            if (ab == null || ab.disabled)
            {
                continue;
            }

            float d = (ab.transform.position - worldPos).sqrMagnitude;
            if (d < bestDistSq)
            {
                bestDistSq = d;
                best = ab;
            }
        }

        return best;
    }

    internal static GlobalPosition WorldToGlobal(Vector3 localPos)
    {
        return GlobalPositionExtensions.ToGlobalPosition(localPos);
    }

    internal static Vector3 GlobalToWorld(GlobalPosition globalPos)
    {
        return GlobalPositionExtensions.ToLocalPosition(globalPos);
    }

    internal static bool TrySnapToDryTerrain(Vector3 worldPos, out Vector3 grounded)
    {
        float seaY = Datum.LocalSeaY;
        var rayOrigin = new Vector3(worldPos.x, Mathf.Max(worldPos.y + 400f, seaY + 2500f), worldPos.z);
        RaycastHit[] hits = Physics.RaycastAll(rayOrigin, Vector3.down, 5000f, (int)PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore);

        bool hasTerrain = false;
        Vector3 bestTerrain = Vector3.zero;
        bool hasFlatStatic = false;
        Vector3 bestFlatStatic = Vector3.zero;

        for (int i = 0; i < hits.Length; i++)
        {
            Collider? col = hits[i].collider;
            if (col == null)
            {
                continue;
            }

            if (col.GetComponentInParent<Unit>() != null ||
                IsUnderTransformPrefix(col.transform, "MDS_Rig_") ||
                IsUnderTransformPrefix(col.transform, "GCE_"))
            {
                continue;
            }

            bool isTerrain = string.Equals(col.GetType().Name, "TerrainCollider", StringComparison.Ordinal) ||
                             (GameAssets.i != null && col.sharedMaterial == GameAssets.i.terrainMaterial) ||
                             col.name.StartsWith("terrain", StringComparison.OrdinalIgnoreCase);

            Vector3 pt = hits[i].point;
            if (isTerrain)
            {
                if (!hasTerrain || pt.y > bestTerrain.y)
                {
                    bestTerrain = pt;
                    hasTerrain = true;
                }
            }
            else if (col.bounds.size.y < 6f)
            {
                if (!hasFlatStatic || pt.y > bestFlatStatic.y)
                {
                    bestFlatStatic = pt;
                    hasFlatStatic = true;
                }
            }
        }

        if (hasTerrain || hasFlatStatic)
        {
            if (hasTerrain && (!hasFlatStatic || bestTerrain.y >= bestFlatStatic.y))
            {
                grounded = bestTerrain;
            }
            else
            {
                grounded = bestFlatStatic;
            }

            return grounded.y > seaY + 0.25f;
        }

        grounded = new Vector3(worldPos.x, Mathf.Max(worldPos.y, seaY), worldPos.z);
        return false;
    }

    private static bool IsUnderTransformPrefix(Transform? tf, string prefix)
    {
        while (tf != null)
        {
            if (tf.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            tf = tf.parent;
        }

        return false;
    }

    internal static Vector3 SnapToTerrain(Vector3 worldPos)
    {
        if (TrySnapToDryTerrain(worldPos, out Vector3 grounded))
        {
            return grounded;
        }

        float seaY = Datum.LocalSeaY;
        if (grounded.y < seaY)
        {
            grounded.y = seaY;
        }

        return grounded;
    }

    internal static bool IsMdsSyntheticKey(string? key)
    {
        return !string.IsNullOrEmpty(key) &&
               key!.StartsWith("mds_", StringComparison.OrdinalIgnoreCase);
    }

    internal static GameObject? ResolveEncyclopediaUnitPrefab(string? cloneKey, out bool isBuilding)
    {
        return ResolveEncyclopediaUnitPrefab(cloneKey, out isBuilding, out _);
    }

    internal static GameObject? ResolveEncyclopediaUnitPrefab(string? cloneKey, out bool isBuilding, out bool isVehicle)
    {
        isBuilding = true;
        isVehicle = false;
        UnitDefinition? template = ResolveEncyclopediaUnitTemplate(cloneKey);
        if (template != null && template.unitPrefab != null)
        {
            isVehicle = template is VehicleDefinition;
            isBuilding = template is BuildingDefinition ||
                         (!isVehicle && template.unitPrefab.GetComponent<Building>() != null);
            return template.unitPrefab;
        }

        Encyclopedia? enc = Encyclopedia.i;
        if (enc != null &&
            !string.IsNullOrWhiteSpace(cloneKey) &&
            !IsMdsSyntheticKey(cloneKey) &&
            enc.TryGetPrefab(cloneKey!, out GameObject? directPrefab) &&
            directPrefab != null)
        {
            isVehicle = directPrefab.GetComponent<GroundVehicle>() != null;
            isBuilding = !isVehicle && directPrefab.GetComponent<Building>() != null;
            return directPrefab;
        }

        return null;
    }

    internal static UnitDefinition? ResolveEncyclopediaUnitTemplate(string? cloneKey)
    {
        Encyclopedia? enc = Encyclopedia.i;
        if (enc == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(cloneKey) &&
            !IsMdsSyntheticKey(cloneKey) &&
            TryFindUnitTemplateExact(enc, cloneKey!, out UnitDefinition? exact))
        {
            return exact;
        }

        for (int i = 0; i < BuildingFallbacks.Length; i++)
        {
            if (TryFindUnitTemplateExact(enc, BuildingFallbacks[i], out UnitDefinition? fallback))
            {
                return fallback;
            }
        }

        if (enc.buildings != null)
        {
            for (int i = 0; i < enc.buildings.Count; i++)
            {
                BuildingDefinition? b = enc.buildings[i];
                if (b != null && b.unitPrefab != null && !IsMdsSyntheticKey(b.jsonKey))
                {
                    return b;
                }
            }
        }

        return null;
    }

    private static bool TryFindUnitTemplateExact(Encyclopedia enc, string key, out UnitDefinition? template)
    {
        template = null;

        if (enc.vehicles != null)
        {
            for (int i = 0; i < enc.vehicles.Count; i++)
            {
                VehicleDefinition? v = enc.vehicles[i];
                if (v == null || v.unitPrefab == null || IsMdsSyntheticKey(v.jsonKey))
                {
                    continue;
                }

                if (MatchesUnitKey(v, key))
                {
                    template = v;
                    return true;
                }
            }
        }

        if (enc.buildings != null)
        {
            for (int i = 0; i < enc.buildings.Count; i++)
            {
                BuildingDefinition? b = enc.buildings[i];
                if (b == null || b.unitPrefab == null || IsMdsSyntheticKey(b.jsonKey))
                {
                    continue;
                }

                if (MatchesUnitKey(b, key))
                {
                    template = b;
                    return true;
                }
            }
        }

        if (enc.otherUnits != null)
        {
            for (int i = 0; i < enc.otherUnits.Count; i++)
            {
                UnitDefinition? u = enc.otherUnits[i];
                if (u == null || u.unitPrefab == null || IsMdsSyntheticKey(u.jsonKey))
                {
                    continue;
                }

                if (MatchesUnitKey(u, key))
                {
                    template = u;
                    return true;
                }
            }
        }

        return false;
    }

    internal static MissileDefinition? ResolveEncyclopediaArhMissileDef(string? preferredCloneKey)
    {
        Encyclopedia? enc = Encyclopedia.i;
        if (enc == null || enc.missiles == null || enc.missiles.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredCloneKey) && !IsMdsSyntheticKey(preferredCloneKey))
        {
            MissileDefinition? exact = FindMissileInList(enc.missiles, preferredCloneKey!);
            if (exact != null && exact.unitPrefab != null && exact.unitPrefab.GetComponent<ARHSeeker>() != null)
            {
                return exact;
            }
        }

        string[] arhFallbacks = { "SAM_Radar2", "AAM_ARH1" };
        for (int i = 0; i < arhFallbacks.Length; i++)
        {
            MissileDefinition? fb = FindMissileInList(enc.missiles, arhFallbacks[i]);
            if (fb != null && fb.unitPrefab != null && fb.unitPrefab.GetComponent<ARHSeeker>() != null)
            {
                return fb;
            }
        }

        for (int i = 0; i < enc.missiles.Count; i++)
        {
            MissileDefinition? m = enc.missiles[i];
            if (m != null && m.unitPrefab != null && !IsMdsSyntheticKey(m.jsonKey) &&
                m.unitPrefab.GetComponent<ARHSeeker>() != null)
            {
                return m;
            }
        }

        return ResolveEncyclopediaMissileDef(preferredCloneKey, preferBallisticOrSam: true);
    }

    internal static MissileDefinition? ResolveEncyclopediaMissileDef(string? cloneKey, bool preferBallisticOrSam)
    {
        Encyclopedia? enc = Encyclopedia.i;
        if (enc == null || enc.missiles == null || enc.missiles.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(cloneKey) && !IsMdsSyntheticKey(cloneKey))
        {
            MissileDefinition? exact = FindMissileInList(enc.missiles, cloneKey!);
            if (exact != null)
            {
                return exact;
            }
        }

        for (int i = 0; i < MissileFallbacks.Length; i++)
        {
            MissileDefinition? fb = FindMissileInList(enc.missiles, MissileFallbacks[i]);
            if (fb != null)
            {
                return fb;
            }
        }

        for (int i = 0; i < enc.missiles.Count; i++)
        {
            MissileDefinition? m = enc.missiles[i];
            if (m == null || m.unitPrefab == null || IsMdsSyntheticKey(m.jsonKey))
            {
                continue;
            }

            if (preferBallisticOrSam && m.unitName != null &&
                (m.unitName.IndexOf("SAM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 m.unitName.IndexOf("Radar", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return m;
            }
        }

        for (int i = 0; i < enc.missiles.Count; i++)
        {
            MissileDefinition? m = enc.missiles[i];
            if (m != null && m.unitPrefab != null && !IsMdsSyntheticKey(m.jsonKey))
            {
                return m;
            }
        }

        return null;
    }

    private static MissileDefinition? FindMissileInList(List<MissileDefinition> list, string key)
    {
        for (int i = 0; i < list.Count; i++)
        {
            MissileDefinition? m = list[i];
            if (m != null && m.unitPrefab != null && !IsMdsSyntheticKey(m.jsonKey) && MatchesUnitKey(m, key))
            {
                return m;
            }
        }

        for (int i = 0; i < list.Count; i++)
        {
            MissileDefinition? m = list[i];
            if (m == null || m.unitPrefab == null || IsMdsSyntheticKey(m.jsonKey))
            {
                continue;
            }

            if ((!string.IsNullOrEmpty(m.jsonKey) && m.jsonKey.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrEmpty(m.unitName) && m.unitName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return m;
            }
        }

        return null;
    }

    private static bool MatchesUnitKey(UnitDefinition def, string key)
    {
        return string.Equals(def.jsonKey, key, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(def.unitName, key, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(def.name, key, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsHostileFaction(FactionHQ? a, FactionHQ? b)
    {
        return a != null && b != null && !ReferenceEquals(a, b);
    }

    internal static bool IsHostileToAffinity(Unit? unit, FactionAffinity myAffinity, FactionHQ? myHq)
    {
        if (unit == null || unit.disabled)
        {
            return false;
        }

        FactionHQ? unitHq = unit.NetworkHQ ?? unit.MapHQ;
        if (unitHq == null)
        {
            return false;
        }

        if (myHq != null)
        {
            return !ReferenceEquals(unitHq, myHq);
        }

        if (myAffinity == FactionAffinity.Any)
        {
            return false;
        }

        FactionAffinity unitAffinity = ToFactionAffinity(unitHq);
        return unitAffinity != FactionAffinity.Any && unitAffinity != myAffinity;
    }

    internal static bool IsAircraftSplashed(Unit? unit)
    {
        if (unit == null || unit.disabled)
        {
            return true;
        }

        if (unit.unitState == Unit.UnitState.Abandoned ||
            unit.unitState == Unit.UnitState.Destroyed ||
            unit.unitState == Unit.UnitState.Returned)
        {
            return true;
        }

        if (unit is not Aircraft aircraft)
        {
            return false;
        }

        if (aircraft.HasEjected())
        {
            return true;
        }

        if (aircraft.cockpit == null ||
            aircraft.cockpit.IsDetached() ||
            aircraft.cockpit.hitPoints <= 0f ||
            (aircraft.cockpit.xform != null && aircraft.cockpit.xform.position.y < Datum.LocalSeaY))
        {
            return true;
        }

        if (aircraft.pilots != null && aircraft.pilots.Length > 0)
        {
            bool anyLivePilot = false;
            for (int i = 0; i < aircraft.pilots.Length; i++)
            {
                Pilot? pilot = aircraft.pilots[i];
                if (pilot != null && !pilot.dead && !pilot.ejected)
                {
                    anyLivePilot = true;
                    break;
                }
            }

            if (!anyLivePilot)
            {
                return true;
            }
        }

        if (aircraft.partDamageTracker != null)
        {
            float detached = aircraft.partDamageTracker.GetDetachedRatio();
            bool isAi = aircraft.Player == null;
            if (isAi && aircraft.radarAlt > 40f && detached > 0.12f)
            {
                return true;
            }

            if (detached >= 0.35f)
            {
                return true;
            }
        }

        return false;
    }

    internal static float SafeGetBlastYield(Missile missile)
    {
        if (MissileBlastYieldField != null)
        {
            try
            {
                object? raw = MissileBlastYieldField.GetValue(missile);
                if (raw is float y)
                {
                    return y;
                }
            }
            catch
            {
                // ignored
            }
        }

        try
        {
            return missile.GetYield();
        }
        catch
        {
            return 0f;
        }
    }

    internal static float SafeGetHitpoints(Missile? missile)
    {
        if (missile == null || MissileHitpointsField == null)
        {
            return 100f;
        }

        try
        {
            if (MissileHitpointsField.GetValue(missile) is float hp)
            {
                return hp;
            }
        }
        catch
        {
            // ignored
        }

        return 100f;
    }

    internal static MissileSeeker? SafeGetSeeker(Missile missile)
    {
        if (MissileSeekerField == null)
        {
            return missile.GetComponent<MissileSeeker>();
        }

        try
        {
            return MissileSeekerField.GetValue(missile) as MissileSeeker ?? missile.GetComponent<MissileSeeker>();
        }
        catch
        {
            return missile.GetComponent<MissileSeeker>();
        }
    }

    internal static bool SafeDisarmWarhead(Missile targetMissile)
    {
        if (targetMissile == null || MissileWarheadField == null)
        {
            return false;
        }

        try
        {
            object? warheadObj = MissileWarheadField.GetValue(targetMissile);
            if (warheadObj == null)
            {
                return false;
            }

            FieldInfo? armedField = AccessTools.Field(warheadObj.GetType(), "Armed");
            if (armedField != null)
            {
                armedField.SetValue(warheadObj, false);
                return true;
            }
        }
        catch (Exception ex)
        {
            MdsLog.Debug($"SafeDisarmWarhead failed: {ex.Message}");
        }

        return false;
    }
}
