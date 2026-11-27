using System;
using MDSFramework.Api;
using MDSFramework.Deployment;
using UnityEngine;

namespace MDSFramework.Missiles
{
    internal static class CustomMissileLauncher
    {
        internal static Missile? LaunchCustomMissile(
            ContractorPackage package,
            CustomMissileSpec missileSpec,
            Unit launcherUnit,
            Transform launchTransform,
            Unit targetUnit,
            bool verticalLaunch,
            bool usePassiveHoj,
            ModularUnitSpec? launcherSiteSpec = null)
        {
            if (Spawner.i == null || launcherUnit == null || targetUnit == null || targetUnit.disabled)
            {
                return null;
            }

            if (missileSpec.IsAntiShip && targetUnit is not Ship)
            {
                MdsLog.Debug($"[AntiShip] Blocked surface launch of '{missileSpec.DisplayName}' against non-ship target '{targetUnit.unitName}'.");
                return null;
            }

            if (missileSpec.IsAntiVehicle && targetUnit is not GroundVehicle)
            {
                MdsLog.Debug($"[AntiVehicle] Blocked loitering launch of '{missileSpec.DisplayName}' against non-vehicle target '{targetUnit.unitName}'.");
                return null;
            }

            bool hasUnitRadar = launcherUnit.radar != null;
            bool requiresArhSeeker = missileSpec.SupportsActiveToPassiveHoj ||
                                     missileSpec.RequiresTargetEcmToTrack ||
                                     missileSpec.IsAbmInterceptor ||
                                     !hasUnitRadar;

            MissileDefinition? baseDef = null;
            if (!missileSpec.IsAntiShip && !missileSpec.IsAntiVehicle)
            {
                if (!requiresArhSeeker && !string.IsNullOrWhiteSpace(missileSpec.BaseMissileCloneKey))
                {
                    MissileDefinition? requestedDef = GameInteropHelpers.ResolveEncyclopediaMissileDef(
                        missileSpec.BaseMissileCloneKey,
                        preferBallisticOrSam: missileSpec.MaxEngagementRangeKm >= 20f);
                    if (requestedDef != null &&
                        requestedDef.unitPrefab != null &&
                        requestedDef.unitPrefab.GetComponent<SARHSeeker>() != null)
                    {
                        baseDef = requestedDef;
                    }
                }

                baseDef ??= GameInteropHelpers.ResolveEncyclopediaArhMissileDef(missileSpec.BaseMissileCloneKey);
            }
            else
            {
                baseDef = GameInteropHelpers.ResolveEncyclopediaMissileDef(
                    missileSpec.BaseMissileCloneKey,
                    preferBallisticOrSam: false);
            }

            if (baseDef == null || baseDef.unitPrefab == null)
            {
                MdsLog.Warn($"Cannot launch '{missileSpec.MissileId}': no vanilla MissileDefinition base found.");
                return null;
            }

            Vector3 toTarget = targetUnit.transform.position - launchTransform.position;
            Vector3 flatDir = new Vector3(toTarget.x, 0f, toTarget.z);
            float flatMag = flatDir.magnitude;
            if (flatMag < 0.01f)
            {
                Vector3 fwdFlat = new Vector3(launchTransform.forward.x, 0f, launchTransform.forward.z);
                flatDir = fwdFlat.sqrMagnitude > 0.01f ? fwdFlat.normalized : Vector3.forward;
            }
            else
            {
                flatDir /= flatMag;
            }

            Vector3 spawnPos;
            Quaternion spawnRot;
            Vector3 initialVel;
            if (verticalLaunch)
            {
                spawnPos = launchTransform.position + (Vector3.up * 2.2f);
                spawnRot = Quaternion.LookRotation(Vector3.up, flatDir);
                initialVel = Vector3.up * 45f;
            }
            else if (launcherSiteSpec != null && launcherSiteSpec.LaunchAlongSocketForward && launchTransform.forward.sqrMagnitude > 0.01f)
            {
                Vector3 cellFwd = launchTransform.forward.normalized;
                spawnPos = launchTransform.position + (cellFwd * 0.9f);
                spawnRot = Quaternion.LookRotation(cellFwd, Vector3.up);
                initialVel = cellFwd * 48f;
            }
            else
            {
                float rawPitchDeg = Mathf.Atan2(toTarget.y, Mathf.Max(0.1f, flatMag)) * Mathf.Rad2Deg;
                float launchPitchDeg = Mathf.Clamp(rawPitchDeg, 45f, 75f);
                float pitchRad = launchPitchDeg * Mathf.Deg2Rad;
                Vector3 launchDir = (flatDir * Mathf.Cos(pitchRad) + Vector3.up * Mathf.Sin(pitchRad)).normalized;

                spawnPos = launchTransform.position + (launchDir * 2.2f) + (Vector3.up * 1.0f);
                spawnRot = Quaternion.LookRotation(launchDir, Vector3.up);
                initialVel = launchDir * 75f;
            }

            MissileDefinition? customDef = MdsEncyclopediaCatalog.TryGetMissileDefinition(missileSpec.MissileId, out MissileDefinition catalogMissileDef)
                ? catalogMissileDef
                : null;

            Missile? spawned = null;
            bool pushedScope = false;
            try
            {
                Patches.PendingMdsSpawnScope.PushMissile(package, missileSpec, customDef ?? baseDef);
                pushedScope = true;
                spawned = Spawner.i.SpawnMissile(baseDef, spawnPos, spawnRot, initialVel, targetUnit, launcherUnit);
            }
            catch (Exception ex)
            {
                MdsLog.Warn($"Spawner.SpawnMissile threw for '{missileSpec.MissileId}': {ex.Message}");
                return null;
            }
            finally
            {
                if (pushedScope)
                {
                    Patches.PendingMdsSpawnScope.Pop();
                }
            }

            if (spawned == null)
            {
                return null;
            }

            if (customDef != null)
            {
                spawned.definition = customDef;
            }

            spawned.NetworkunitName = missileSpec.DisplayName;

            FactionAffinity ownerAffinity = GameInteropHelpers.ToFactionAffinity(launcherUnit.NetworkHQ ?? launcherUnit.MapHQ);
            CustomMissileBehavior behavior = spawned.gameObject.AddComponent<CustomMissileBehavior>();
            behavior.Initialize(package, missileSpec, spawned, targetUnit, ownerAffinity, usePassiveHoj, verticalLaunch);

            MdsLog.Debug(
                $"[Missile] Launched '{missileSpec.DisplayName}' ({missileSpec.FlightProfile}) -> '{targetUnit.unitName}' " +
                $"(HOJ={usePassiveHoj}, ABM={missileSpec.IsAbmInterceptor}).");

            return spawned;
        }
    }
}
