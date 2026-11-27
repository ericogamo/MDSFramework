using System;
using HarmonyLib;
using MDSFramework.Missiles;
using UnityEngine;

namespace MDSFramework.Patches
{
    [HarmonyPatch(typeof(Laser), "FixedUpdate")]
    internal static class LaserThermalHardeningScopePatch
    {
        [ThreadStatic]
        private static int laserFixedUpdateDepth;

        internal static bool IsInsideLaserFixedUpdate => laserFixedUpdateDepth > 0;

        private static void Prefix()
        {
            laserFixedUpdateDepth++;
        }

        private static void Finalizer()
        {
            if (laserFixedUpdateDepth > 0)
            {
                laserFixedUpdateDepth--;
            }
        }
    }

    [HarmonyPatch(
        typeof(Missile),
        nameof(Missile.TakeDamage),
        new[] { typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(PersistentID) })]
    internal static class LaserThermalHardeningPatch
    {
        private static void Prefix(Missile __instance, ref float blastDamage, ref float fireDamage)
        {
            if (!LaserThermalHardeningScopePatch.IsInsideLaserFixedUpdate ||
                !GameInteropHelpers.IsServerAuthority() ||
                !MdsConfig.EnableLaserDwell.Value ||
                __instance == null)
            {
                return;
            }

            if (!CustomMissileTracker.TryGet(__instance, out CustomMissileBehavior custom))
            {
                return;
            }

            float hardening = custom.ThermalHardening;
            if (Mathf.Abs(hardening - 1.0f) < 0.02f)
            {
                return;
            }

            // Apply hardening on the actual hit missile and only to damage exceeding armor thresholds,
            // so per-tick laser damage is not pushed below armor thresholds into complete immunity.
            ArmorProperties? armor = __instance.GetArmorProperties();
            float blastArmor = armor != null ? armor.blastArmor : 0f;
            float fireArmor = armor != null ? armor.fireArmor : 0f;
            float invHardening = 1f / Mathf.Max(0.25f, hardening);

            if (blastDamage > blastArmor)
            {
                blastDamage = blastArmor + ((blastDamage - blastArmor) * invHardening);
            }

            if (fireDamage > fireArmor)
            {
                fireDamage = fireArmor + ((fireDamage - fireArmor) * invHardening);
            }
        }
    }
}
