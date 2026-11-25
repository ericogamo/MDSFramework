using HarmonyLib;
using MDSFramework.Missiles;
using UnityEngine;

namespace MDSFramework.Patches
{
    [HarmonyPatch(typeof(Laser), "FixedUpdate")]
    internal static class LaserThermalHardeningPatch
    {
        private struct SavedLaserStats
        {
            internal bool Modified;
            internal float OriginalBlastDamage;
            internal float OriginalFireDamage;
        }

        private static void Prefix(Laser __instance, out SavedLaserStats __state)
        {
            __state = default;
            if (!MdsConfig.EnableLaserDwell.Value || __instance == null)
            {
                return;
            }

            Unit? target = GameInteropHelpers.LaserCurrentTargetField?.GetValue(__instance) as Unit;
            if (target == null || !CustomMissileTracker.TryGet(target, out CustomMissileBehavior custom))
            {
                return;
            }

            float hardening = custom.ThermalHardening;
            if (Mathf.Abs(hardening - 1.0f) < 0.02f)
            {
                return;
            }

            float scale = 1f / Mathf.Max(0.25f, hardening);
            if (GameInteropHelpers.LaserBlastDamageField != null && GameInteropHelpers.LaserFireDamageField != null)
            {
                object? bRaw = GameInteropHelpers.LaserBlastDamageField.GetValue(__instance);
                object? fRaw = GameInteropHelpers.LaserFireDamageField.GetValue(__instance);
                if (bRaw is float b && fRaw is float f)
                {
                    __state = new SavedLaserStats
                    {
                        Modified = true,
                        OriginalBlastDamage = b,
                        OriginalFireDamage = f
                    };
                    GameInteropHelpers.LaserBlastDamageField.SetValue(__instance, b * scale);
                    GameInteropHelpers.LaserFireDamageField.SetValue(__instance, f * scale);
                }
            }
        }

        private static void Postfix(Laser __instance, SavedLaserStats __state)
        {
            if (!__state.Modified || __instance == null)
            {
                return;
            }

            GameInteropHelpers.LaserBlastDamageField?.SetValue(__instance, __state.OriginalBlastDamage);
            GameInteropHelpers.LaserFireDamageField?.SetValue(__instance, __state.OriginalFireDamage);
        }
    }
}
