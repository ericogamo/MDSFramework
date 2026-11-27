using System;
using HarmonyLib;
using MDSFramework.Missiles;
using UnityEngine;

namespace MDSFramework.Patches
{
    [HarmonyPatch(
        typeof(Missile),
        nameof(Missile.TakeDamage),
        new[] { typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(PersistentID) })]
    internal static class CustomMissileTakeDamageScopePatch
    {
        [ThreadStatic]
        private static int takeDamageDepth;

        internal static bool IsInsideTakeDamage => takeDamageDepth > 0;

        private static bool Prefix(out bool __state)
        {
            if (!GameInteropHelpers.IsServerAuthority())
            {
                __state = false;
                return false;
            }

            takeDamageDepth++;
            __state = true;
            return true;
        }

        private static void Finalizer(bool __state)
        {
            if (__state && takeDamageDepth > 0)
            {
                takeDamageDepth--;
            }
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.Detonate), new[] { typeof(Vector3), typeof(bool), typeof(bool) })]
    internal static class CustomMissileDetonateGuardPatch
    {
        private static bool Prefix(Missile __instance, bool hitArmor, bool hitTerrain)
        {
            if (CustomMissileTakeDamageScopePatch.IsInsideTakeDamage)
            {
                return true;
            }

            if (__instance != null &&
                CustomMissileTracker.TryGet(__instance, out CustomMissileBehavior custom) &&
                custom.ShouldSuppressPrematureSelfDestruct(hitArmor, hitTerrain))
            {
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), "ARHSeeker_OnJam")]
    internal static class ArhSeekerOnJamPatch
    {
        private static bool Prefix(ARHSeeker __instance, Unit.JamEventArgs e, out bool __state)
        {
            __state = false;
            if (!GameInteropHelpers.IsServerAuthority() || __instance == null)
            {
                return true;
            }

            Missile? missile = GameInteropHelpers.SeekerMissileField?.GetValue(__instance) as Missile;
            if (missile == null || !CustomMissileTracker.TryGet(missile, out CustomMissileBehavior custom))
            {
                return true;
            }

            if (!custom.Spec.SupportsActiveToPassiveHoj)
            {
                return true;
            }

            if (e.jammingUnit == null || e.jammingUnit.disabled ||
                (custom.Spec.IsAntiShip && e.jammingUnit is not Ship) ||
                !GameInteropHelpers.IsHostileToAffinity(e.jammingUnit, custom.OwnerFaction, missile.NetworkHQ ?? missile.MapHQ))
            {
                return false;
            }

            __state = true;
            return true;
        }

        private static void Postfix(ARHSeeker __instance, bool __state)
        {
            if (!__state || __instance == null)
            {
                return;
            }

            Missile? missile = GameInteropHelpers.SeekerMissileField?.GetValue(__instance) as Missile;
            if (missile == null || !CustomMissileTracker.TryGet(missile, out CustomMissileBehavior custom))
            {
                return;
            }

            if (GameInteropHelpers.ArhJamAccumulationField?.GetValue(__instance) is float acc &&
                GameInteropHelpers.ArhJamToleranceField?.GetValue(__instance) is float tol &&
                acc >= tol)
            {
                custom.PassiveHojActive = true;
                missile.NetworkseekerMode = Missile.SeekerMode.passive;
            }
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), "DatalinkMode")]
    internal static class ArhSeekerDatalinkHojPatch
    {
        private static bool Prefix(ARHSeeker __instance)
        {
            if (__instance == null)
            {
                return true;
            }

            Missile? missile = GameInteropHelpers.SeekerMissileField?.GetValue(__instance) as Missile;
            if (missile == null || !CustomMissileTracker.TryGet(missile, out CustomMissileBehavior custom))
            {
                return true;
            }

            if (!custom.PassiveHojActive)
            {
                return true;
            }

            if (missile.seekerMode != Missile.SeekerMode.passive)
            {
                missile.NetworkseekerMode = Missile.SeekerMode.passive;
            }

            // Passive HoJ missiles guide off ELINT triangulation / inertial memory rather than HQ tracking database.
            return false;
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), "SlowChecks")]
    internal static class ArhSeekerSlowChecksBlindGuardPatch
    {
        private static bool Prefix(ARHSeeker __instance)
        {
            if (__instance == null)
            {
                return true;
            }

            Missile? missile = GameInteropHelpers.SeekerMissileField?.GetValue(__instance) as Missile;
            if (missile == null || !CustomMissileTracker.TryGet(missile, out CustomMissileBehavior custom))
            {
                return true;
            }

            Unit? targetUnit = GameInteropHelpers.SeekerTargetUnitField?.GetValue(__instance) as Unit;
            bool unTrackedBlind = targetUnit != null &&
                                  missile.NetworkHQ != null &&
                                  missile.NetworkHQ.GetTrackingData(targetUnit.persistentID) == null;
            if (!custom.PassiveHojActive && !unTrackedBlind)
            {
                return true;
            }

            if (missile.disabled)
            {
                return false;
            }

            float selfDestructSpeed = GameInteropHelpers.ArhSelfDestructSpeedField?.GetValue(__instance) is float sds
                ? sds
                : 75f;
            bool shouldSelfDestruct =
                (missile.timeSinceSpawn > 10f || (!missile.EngineOn() && missile.timeSinceSpawn > 2f)) &&
                (missile.LosingGround() ||
                 missile.MissedTarget() ||
                 missile.speed < selfDestructSpeed ||
                 (targetUnit == null && (!custom.PassiveHojActive || custom.HasExceededBlindCoastTime)));

            if (shouldSelfDestruct && missile.rb != null)
            {
                missile.Detonate(missile.rb.velocity, hitArmor: false, hitTerrain: false);
            }

            if (missile.NetworkHQ != null && targetUnit != null && !targetUnit.disabled && targetUnit.NetworkHQ != missile.NetworkHQ)
            {
                TrackingInfo? trackingData = missile.NetworkHQ.GetTrackingData(targetUnit.persistentID);
                bool multipleInbound = targetUnit is Aircraft && trackingData != null && trackingData.missileAttacks > 1;
                GameInteropHelpers.ArhMultipleInboundField?.SetValue(__instance, multipleInbound);
            }

            float loftAmount = GameInteropHelpers.ArhLoftAmountField?.GetValue(__instance) is float la ? la : 0f;
            if (loftAmount > 0f && missile.rb != null)
            {
                GlobalPosition knownPos = GameInteropHelpers.ArhKnownPosField?.GetValue(__instance) is GlobalPosition kp
                    ? kp
                    : missile.GlobalPosition() + (missile.transform.forward * 10000f);
                Vector3 vector = knownPos - missile.GlobalPosition();
                if (targetUnit != null && missile.NetworkHQ != null && missile.NetworkHQ.TryGetKnownPosition(targetUnit, out GlobalPosition hqPos))
                {
                    vector = hqPos - missile.GlobalPosition();
                }

                float closingSpeed = vector.sqrMagnitude > 0.01f
                    ? Vector3.Dot(vector.normalized, missile.rb.velocity)
                    : 0f;
                float dist = vector.magnitude;
                GameInteropHelpers.ArhTargetDistField?.SetValue(__instance, dist);
                GameInteropHelpers.ArhTimeToTargetField?.SetValue(__instance, dist / Mathf.Max(closingSpeed, 10f));
            }

            return false;
        }
    }
}
