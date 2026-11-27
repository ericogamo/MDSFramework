using System.Collections.Generic;
using HarmonyLib;
using MDSFramework.Api;
using MDSFramework.Deployment;
using MDSFramework.Missiles;
using MDSFramework.Sensors;
using Mirage;
using NuclearOption.MissionEditorScripts.Buttons;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace MDSFramework.Patches
{
    internal readonly struct PendingMdsSpawnContext
    {
        internal PendingMdsSpawnContext(
            ContractorPackage package,
            ModularUnitSpec? unitSpec,
            CustomMissileSpec? missileSpec,
            UnitDefinition? definition,
            bool isCargoContainer,
            bool autoAttachSiteOnSpawn = false)
        {
            Package = package;
            UnitSpec = unitSpec;
            MissileSpec = missileSpec;
            Definition = definition;
            IsCargoContainer = isCargoContainer;
            AutoAttachSiteOnSpawn = autoAttachSiteOnSpawn;
        }

        internal ContractorPackage Package { get; }
        internal ModularUnitSpec? UnitSpec { get; }
        internal CustomMissileSpec? MissileSpec { get; }
        internal UnitDefinition? Definition { get; }
        internal bool IsCargoContainer { get; }
        internal bool AutoAttachSiteOnSpawn { get; }
    }

    internal static class PendingMdsSpawnScope
    {
        private static readonly Stack<PendingMdsSpawnContext> Stack = new Stack<PendingMdsSpawnContext>();

        internal static void PushUnit(
            ContractorPackage package,
            ModularUnitSpec spec,
            UnitDefinition? definition,
            bool isCargoContainer = false,
            bool autoAttachSiteOnSpawn = false)
        {
            Stack.Push(new PendingMdsSpawnContext(package, spec, null, definition, isCargoContainer, autoAttachSiteOnSpawn));
        }

        internal static void PushMissile(
            ContractorPackage package,
            CustomMissileSpec spec,
            MissileDefinition? definition)
        {
            Stack.Push(new PendingMdsSpawnContext(package, null, spec, definition, isCargoContainer: false));
        }

        internal static void Pop()
        {
            if (Stack.Count > 0)
            {
                Stack.Pop();
            }
        }

        internal static void Clear()
        {
            Stack.Clear();
        }

        internal static bool TryPeek(out PendingMdsSpawnContext ctx)
        {
            if (Stack.Count > 0)
            {
                ctx = Stack.Peek();
                return true;
            }

            ctx = default;
            return false;
        }
    }

    internal static class MissionEditorPatchHelpers
    {
        internal static bool TryPushScopeForDefinition(UnitDefinition? def)
        {
            if (def == null)
            {
                return false;
            }

            if (MdsEncyclopediaCatalog.TryResolveUnitDefinition(def, out ContractorPackage pkg, out ModularUnitSpec spec, out bool isCargo))
            {
                PendingMdsSpawnScope.PushUnit(pkg, spec, def, isCargo);
                return true;
            }

            if (def is MissileDefinition mDef &&
                MdsEncyclopediaCatalog.TryResolveMissileDefinition(mDef, out ContractorPackage mPkg, out CustomMissileSpec mSpec))
            {
                PendingMdsSpawnScope.PushMissile(mPkg, mSpec, mDef);
                return true;
            }

            return false;
        }

        internal static bool TryPushScopeForUnitKey(string? jsonKey)
        {
            if (MdsEncyclopediaCatalog.TryResolveUnitKey(
                    jsonKey,
                    out ContractorPackage pkg,
                    out ModularUnitSpec spec,
                    out UnitDefinition def,
                    out bool isCargo))
            {
                PendingMdsSpawnScope.PushUnit(pkg, spec, def, isCargo);
                return true;
            }

            return false;
        }

        internal static void AttachPreviewForDefinition(GameObject? target, UnitDefinition? def, bool? forceStartDeployed = null)
        {
            if (target == null || def == null)
            {
                return;
            }

            if (MdsEncyclopediaCatalog.TryResolveUnitDefinition(def, out ContractorPackage pkg, out ModularUnitSpec spec, out bool isCargo))
            {
                bool deployed = forceStartDeployed ?? !isCargo;
                ModularSiteManager.AttachVisualPreviewRig(target, pkg, spec, startDeployed: deployed);
                return;
            }

            if (def is MissileDefinition mDef &&
                MdsEncyclopediaCatalog.TryResolveMissileDefinition(mDef, out ContractorPackage mPkg, out CustomMissileSpec mSpec))
            {
                ModularSiteManager.AttachMissileVisualPreview(target, mPkg, mSpec);
            }
        }

        internal static void ActivateMdsUnitOrPreview(
            Unit? spawned,
            UnitDefinition? def,
            ContractorPackage pkg,
            ModularUnitSpec spec,
            bool isCargo,
            string? fallbackFactionName = null,
            bool consumedRtsCap = false)
        {
            if (spawned == null)
            {
                return;
            }

            if (def != null)
            {
                spawned.definition = def;
            }

            if (GameManager.gameState == GameState.Editor || GameManager.gameState == GameState.Encyclopedia)
            {
                ModularSiteManager.AttachVisualPreviewRig(spawned.gameObject, pkg, spec, startDeployed: !isCargo);
                MdsWeaponStationBuilder.ApplyWeaponStations(spawned, pkg, spec, site: null);
                return;
            }

            if (!GameInteropHelpers.IsGameplayActive() || !GameInteropHelpers.IsServerAuthority())
            {
                return;
            }

            FactionHQ? hq = spawned.NetworkHQ ??
                            spawned.MapHQ ??
                            (!string.IsNullOrEmpty(fallbackFactionName) ? FactionRegistry.HqFromName(fallbackFactionName!) : null);
            FactionAffinity faction = GameInteropHelpers.ToFactionAffinity(hq);

            if (isCargo && spawned is Container container)
            {
                if (container.GetComponent<AirdropContainerBehavior>() == null)
                {
                    AirdropContainerBehavior behavior = container.gameObject.AddComponent<AirdropContainerBehavior>();
                    behavior.ConsumedRtsCap = consumedRtsCap;
                    behavior.Initialize(pkg, spec, container, faction, ensureParachute: false);
                }
            }
            else
            {
                ModularSiteManager.Instance?.AttachSiteToExistingUnit(
                    spawned,
                    pkg,
                    spec,
                    def,
                    faction,
                    hq,
                    consumedRtsCap: consumedRtsCap,
                    startDeployed: ModularSiteManager.ShouldStartDeployed(spec));
            }
        }

        internal static void ActivateMdsMissileOrPreview(
            Missile? spawned,
            MissileDefinition? def,
            ContractorPackage pkg,
            CustomMissileSpec spec,
            string? fallbackFactionName = null)
        {
            if (spawned == null)
            {
                return;
            }

            if (def != null)
            {
                spawned.definition = def;
            }

            if (GameManager.gameState == GameState.Editor || GameManager.gameState == GameState.Encyclopedia)
            {
                ModularSiteManager.AttachMissileVisualPreview(spawned.gameObject, pkg, spec);
                return;
            }

            if (!GameInteropHelpers.IsGameplayActive() || !GameInteropHelpers.IsServerAuthority())
            {
                return;
            }

            if (spawned.GetComponent<CustomMissileBehavior>() == null)
            {
                FactionHQ? hq = spawned.NetworkHQ ??
                                spawned.MapHQ ??
                                (!string.IsNullOrEmpty(fallbackFactionName) ? FactionRegistry.HqFromName(fallbackFactionName!) : null);
                FactionAffinity ownerFaction = GameInteropHelpers.ToFactionAffinity(hq);

                CustomMissileBehavior behavior = spawned.gameObject.AddComponent<CustomMissileBehavior>();
                behavior.Initialize(pkg, spec, spawned, targetUnit: null, ownerFaction, startPassiveHoj: false);
            }
        }

        internal static void HandleSavedUnitSpawned(SavedUnit? saved, Unit? spawned)
        {
            if (saved == null || spawned == null)
            {
                return;
            }

            if (!MdsEncyclopediaCatalog.TryResolveUnitKey(
                    saved.type,
                    out ContractorPackage pkg,
                    out ModularUnitSpec spec,
                    out UnitDefinition def,
                    out bool isCargo))
            {
                return;
            }

            ActivateMdsUnitOrPreview(spawned, def, pkg, spec, isCargo, saved.faction);
        }

        internal static void HandleSavedMissileSpawned(SavedMissile? saved, Missile? spawned)
        {
            if (saved == null || spawned == null)
            {
                return;
            }

            if (!MdsEncyclopediaCatalog.TryResolveMissileKey(
                    saved.type,
                    out ContractorPackage pkg,
                    out CustomMissileSpec spec,
                    out MissileDefinition def))
            {
                return;
            }

            ActivateMdsMissileOrPreview(spawned, def, pkg, spec, saved.faction);
        }

        internal static void ApplyClientVisualSync(Unit? unit)
        {
            if (unit == null || unit.IsServer)
            {
                return;
            }

            if (MdsEncyclopediaCatalog.TryResolveUnitByDisplayName(
                    unit.unitName,
                    out ContractorPackage pkg,
                    out ModularUnitSpec spec,
                    out UnitDefinition def,
                    out bool isCargo))
            {
                bool effectiveCargo = isCargo || unit is Container;
                if (effectiveCargo &&
                    !isCargo &&
                    AirdropCatalogManager.TryGetEntryByUnitId(spec.UnitId, out AirdropCatalogEntry airdropEntry))
                {
                    pkg = airdropEntry.Package;
                    spec = airdropEntry.Spec;
                    def = airdropEntry.CargoDefinition;
                }

                unit.definition = def;
                ModularSiteManager.RemoveVanillaRearmer(unit, spec);
                ModularSiteManager.ConfigureRepairabilityAndMapIcon(unit, null);
                MdsClientSiteProxy? proxy = ModularSiteManager.AttachClientSiteRig(
                    unit,
                    pkg,
                    spec,
                    startDeployed: !effectiveCargo && ModularSiteManager.ShouldStartDeployed(spec));
                MdsWeaponStationBuilder.ApplyWeaponStations(unit, pkg, spec, site: null, clientProxy: proxy);

                if (unit.disabled)
                {
                    Transform? intactRig = unit.transform.Find($"MDS_Rig_{spec.UnitId}");
                    if (intactRig != null)
                    {
                        intactRig.gameObject.SetActive(false);
                    }

                    ModularSiteManager.ReapplyHiddenVanillaVisuals(unit);
                }
            }
        }

        internal static void ApplyClientMissileVisualSync(Missile? missile)
        {
            if (missile == null || missile.IsServer || missile.disabled)
            {
                return;
            }

            if (MdsEncyclopediaCatalog.TryResolveMissileByDisplayName(
                    missile.unitName,
                    out ContractorPackage pkg,
                    out CustomMissileSpec spec,
                    out MissileDefinition def))
            {
                missile.definition = def;
                if (missile.GetComponent<CustomMissileBehavior>() == null)
                {
                    CustomMissileBehavior beh = missile.gameObject.AddComponent<CustomMissileBehavior>();
                    beh.InitializeClientVisualOnly(pkg, spec, missile);
                }
            }
        }
    }

    [HarmonyPatch(typeof(ServerObjectManager), nameof(ServerObjectManager.Spawn), new[] { typeof(NetworkIdentity) })]
    internal static class ServerObjectManagerMdsSpawnPatch
    {
        private static void Prefix(NetworkIdentity identity)
        {
            if (!PendingMdsSpawnScope.TryPeek(out PendingMdsSpawnContext ctx) ||
                identity == null ||
                !identity.TryGetComponent(out Unit unit))
            {
                return;
            }

            if (ctx.Definition != null)
            {
                unit.definition = ctx.Definition;
            }

            if (ctx.UnitSpec != null)
            {
                unit.NetworkunitName = ctx.IsCargoContainer
                    ? $"[MDS Cargo] {ctx.UnitSpec.DisplayName}"
                    : ctx.UnitSpec.DisplayName;
                unit.RCS = Mathf.Max(0.02f, ctx.UnitSpec.StowedRcs);
            }
            else if (ctx.MissileSpec != null)
            {
                unit.NetworkunitName = ctx.MissileSpec.DisplayName;
                if (unit is Missile m && ctx.MissileSpec.BlastYield > 0f)
                {
                    GameInteropHelpers.MissileBlastYieldField?.SetValue(m, ctx.MissileSpec.BlastYield);
                }
            }
        }
    }

    [HarmonyPatch(typeof(NewUnitPanel), "SpawnUnit", new[] { typeof(UnitDefinition) })]
    internal static class NewUnitPanelSpawnUnitPatch
    {
        private static void Postfix(object __instance, UnitDefinition unitDefinition)
        {
            if (__instance == null || unitDefinition == null)
            {
                return;
            }

            if (GameInteropHelpers.NewUnitPanelPlacingObjectField?.GetValue(__instance) is GameObject placingObject)
            {
                MissionEditorPatchHelpers.AttachPreviewForDefinition(placingObject, unitDefinition);
            }
        }
    }

    [HarmonyPatch(typeof(Spawner), nameof(Spawner.SpawnFromUnitDefinitionInEditor))]
    internal static class SpawnerEditorPlacementPatch
    {
        private static void Prefix(UnitDefinition placingDefinition, out bool __state)
        {
            __state = MissionEditorPatchHelpers.TryPushScopeForDefinition(placingDefinition);
        }

        private static void Postfix(UnitDefinition placingDefinition, Unit __result, bool __state)
        {
            if (!__state || __result == null)
            {
                return;
            }

            if (MdsEncyclopediaCatalog.TryResolveUnitDefinition(placingDefinition, out ContractorPackage pkg, out ModularUnitSpec spec, out bool isCargo))
            {
                MissionEditorPatchHelpers.ActivateMdsUnitOrPreview(__result, placingDefinition, pkg, spec, isCargo);
            }
            else if (placingDefinition is MissileDefinition mDef &&
                     __result is Missile missile &&
                     MdsEncyclopediaCatalog.TryResolveMissileDefinition(mDef, out ContractorPackage mPkg, out CustomMissileSpec mSpec))
            {
                MissionEditorPatchHelpers.ActivateMdsMissileOrPreview(missile, mDef, mPkg, mSpec);
            }
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
            {
                PendingMdsSpawnScope.Pop();
            }
        }
    }

    [HarmonyPatch(typeof(Spawner), "TrySpawnBuilding", new[] { typeof(SavedBuilding), typeof(Building) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
    internal static class SpawnerTrySpawnBuildingPatch
    {
        private static void Prefix(SavedBuilding savedBuilding, out bool __state)
        {
            __state = MissionEditorPatchHelpers.TryPushScopeForUnitKey(savedBuilding?.type);
        }

        private static void Postfix(SavedBuilding savedBuilding, ref Building? spawnedBuilding, bool __result, bool __state)
        {
            if (__state && __result && spawnedBuilding != null)
            {
                MissionEditorPatchHelpers.HandleSavedUnitSpawned(savedBuilding, spawnedBuilding);
            }
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
            {
                PendingMdsSpawnScope.Pop();
            }
        }
    }

    [HarmonyPatch(typeof(Spawner), "TrySpawnContainer", new[] { typeof(SavedContainer), typeof(Container) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
    internal static class SpawnerTrySpawnContainerPatch
    {
        private static void Prefix(SavedContainer savedContainer, out bool __state)
        {
            __state = MissionEditorPatchHelpers.TryPushScopeForUnitKey(savedContainer?.type);
        }

        private static void Postfix(SavedContainer savedContainer, ref Container? spawnedContainer, bool __result, bool __state)
        {
            if (__state && __result && spawnedContainer != null)
            {
                MissionEditorPatchHelpers.HandleSavedUnitSpawned(savedContainer, spawnedContainer);
            }
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
            {
                PendingMdsSpawnScope.Pop();
            }
        }
    }

    [HarmonyPatch(typeof(Spawner), nameof(Spawner.TrySpawnVehicle), new[] { typeof(SavedVehicle), typeof(GroundVehicle) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
    internal static class SpawnerTrySpawnVehiclePatch
    {
        private static void Prefix(SavedVehicle savedVehicle, out bool __state)
        {
            __state = MissionEditorPatchHelpers.TryPushScopeForUnitKey(savedVehicle?.type);
        }

        private static void Postfix(SavedVehicle savedVehicle, ref GroundVehicle? spawnedVehicle, bool __result, bool __state)
        {
            if (__state && __result && spawnedVehicle != null)
            {
                MissionEditorPatchHelpers.HandleSavedUnitSpawned(savedVehicle, spawnedVehicle);
            }
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
            {
                PendingMdsSpawnScope.Pop();
            }
        }
    }

    [HarmonyPatch(typeof(Spawner), "TrySpawnMissile", new[] { typeof(SavedMissile), typeof(Missile) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
    internal static class SpawnerTrySpawnMissilePatch
    {
        private static void Prefix(SavedMissile savedMissile, out bool __state)
        {
            __state = false;
            if (savedMissile != null &&
                MdsEncyclopediaCatalog.TryResolveMissileKey(
                    savedMissile.type,
                    out ContractorPackage pkg,
                    out CustomMissileSpec spec,
                    out MissileDefinition def))
            {
                PendingMdsSpawnScope.PushMissile(pkg, spec, def);
                __state = true;
            }
        }

        private static void Postfix(SavedMissile savedMissile, ref Missile? spawnedMissile, bool __result, bool __state)
        {
            if (__state && __result && spawnedMissile != null)
            {
                MissionEditorPatchHelpers.HandleSavedMissileSpawned(savedMissile, spawnedMissile);
            }
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
            {
                PendingMdsSpawnScope.Pop();
            }
        }
    }

    [HarmonyPatch(typeof(EncyclopediaBrowser), "SpawnUnit", new[] { typeof(UnitDefinition) })]
    internal static class EncyclopediaBrowserSpawnUnitPatch
    {
        private static void Prefix(UnitDefinition definition, out bool __state)
        {
            __state = MissionEditorPatchHelpers.TryPushScopeForDefinition(definition);
        }

        private static void Postfix(EncyclopediaBrowser __instance, UnitDefinition definition, bool __state)
        {
            if (!__state || __instance == null || __instance.spawnedUnitObject == null)
            {
                return;
            }

            MissionEditorPatchHelpers.AttachPreviewForDefinition(__instance.spawnedUnitObject, definition, forceStartDeployed: true);
            if (__instance.spawnedUnitObject.TryGetComponent(out Unit encUnit) &&
                MdsEncyclopediaCatalog.TryResolveUnitDefinition(definition, out ContractorPackage pkg, out ModularUnitSpec spec, out _))
            {
                MdsWeaponStationBuilder.ApplyWeaponStations(encUnit, pkg, spec, site: null);
                GameInteropHelpers.EncyclopediaBrowserUpdateWeaponDisplayMethod?.Invoke(__instance, new object[] { encUnit });
            }

            GameInteropHelpers.EncyclopediaBrowserSpawnedPartsField?.SetValue(
                __instance,
                __instance.spawnedUnitObject.GetComponentsInChildren<Transform>());
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
            {
                PendingMdsSpawnScope.Pop();
            }
        }
    }

    [HarmonyPatch(typeof(Building), "OnStartClient")]
    internal static class BuildingMdsClientVisualPatch
    {
        private static void Prefix(Building __instance)
        {
            if (__instance == null || __instance.IsServer)
            {
                return;
            }

            if (MdsEncyclopediaCatalog.TryResolveUnitByDisplayName(__instance.unitName, out _, out _, out _, out _))
            {
                ModularSiteManager.ConfigureBuildingClientFlagsPreStart(__instance);
            }
        }

        private static void Postfix(Building __instance)
        {
            MissionEditorPatchHelpers.ApplyClientVisualSync(__instance);
        }
    }

    [HarmonyPatch(typeof(Building), nameof(Building.UnitDisabled), new[] { typeof(bool), typeof(bool) })]
    internal static class BuildingUnitDisabledClientRepairPatch
    {
        private static void Postfix(Building __instance, bool oldState, bool newState)
        {
            if (__instance == null || __instance.IsServer || !(oldState && !newState))
            {
                return;
            }

            __instance.GetComponent<MdsClientDestructionWatcher>()?.HandleRepaired();
        }
    }

    [HarmonyPatch(typeof(Container), "OnStartClient")]
    internal static class ContainerMdsClientVisualPatch
    {
        private static void Postfix(Container __instance)
        {
            MissionEditorPatchHelpers.ApplyClientVisualSync(__instance);
        }
    }

    [HarmonyPatch(typeof(GroundVehicle), "OnStartClient")]
    internal static class GroundVehicleMdsClientVisualPatch
    {
        private static void Postfix(GroundVehicle __instance)
        {
            MissionEditorPatchHelpers.ApplyClientVisualSync(__instance);
        }
    }

    [HarmonyPatch(typeof(Missile), "OnStartClient")]
    internal static class MissileMdsClientVisualPatch
    {
        private static void Postfix(Missile __instance)
        {
            MissionEditorPatchHelpers.ApplyClientMissileVisualSync(__instance);
        }
    }

    [HarmonyPatch(typeof(VehicleDepot), nameof(VehicleDepot.TrySpawnVehicle), new[] { typeof(VehicleDefinition) })]
    internal static class VehicleDepotTrySpawnVehiclePatch
    {
        private static bool Prefix(VehicleDepot __instance, VehicleDefinition? vehicleDefinition, ref bool __result, out bool __state)
        {
            if (vehicleDefinition != null &&
                MdsEncyclopediaCatalog.TryResolveUnitDefinition(vehicleDefinition, out ContractorPackage pkg, out ModularUnitSpec spec, out bool isCargo))
            {
                if (GameInteropHelpers.IsGameplayActive() &&
                    GameInteropHelpers.IsServerAuthority() &&
                    spec.Logistics != null)
                {
                    FactionHQ? hq = __instance != null ? (__instance.NetworkHQ ?? __instance.MapHQ) : null;
                    FactionAffinity faction = GameInteropHelpers.ToFactionAffinity(hq);
                    if (!RtsBridgeHost.Current.Caps.CanConsumeCap(
                            faction,
                            spec.Logistics.CapCategory,
                            spec.Logistics.CapWeight,
                            out _))
                    {
                        MdsLog.Warn($"[VehicleDepot] Unit cap full for '{spec.UnitId}' ({faction}); blocking depot spawn.");
                        __result = false;
                        __state = false;
                        return false;
                    }
                }

                PendingMdsSpawnScope.PushUnit(pkg, spec, vehicleDefinition, isCargo, autoAttachSiteOnSpawn: true);
                __state = true;
                return true;
            }

            __state = false;
            return true;
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
            {
                PendingMdsSpawnScope.Pop();
            }
        }
    }

    [HarmonyPatch(
        typeof(Spawner),
        nameof(Spawner.SpawnVehicle),
        new[]
        {
            typeof(GameObject),
            typeof(GlobalPosition),
            typeof(Quaternion),
            typeof(Vector3),
            typeof(FactionHQ),
            typeof(string),
            typeof(float),
            typeof(bool),
            typeof(Player)
        })]
    internal static class SpawnerSpawnVehicleMdsPatch
    {
        private static void Postfix(GroundVehicle? __result)
        {
            if (__result != null &&
                PendingMdsSpawnScope.TryPeek(out PendingMdsSpawnContext ctx) &&
                ctx.AutoAttachSiteOnSpawn &&
                ctx.UnitSpec != null)
            {
                bool consumedCap = false;
                if (GameInteropHelpers.IsGameplayActive() &&
                    GameInteropHelpers.IsServerAuthority() &&
                    ctx.UnitSpec.Logistics != null)
                {
                    FactionAffinity faction = GameInteropHelpers.ToFactionAffinity(__result.NetworkHQ ?? __result.MapHQ);
                    consumedCap = RtsBridgeHost.Current.Caps.TryConsumeCap(
                        faction,
                        ctx.UnitSpec.Logistics.CapCategory,
                        ctx.UnitSpec.Logistics.CapWeight);
                }

                MissionEditorPatchHelpers.ActivateMdsUnitOrPreview(
                    __result,
                    ctx.Definition,
                    ctx.Package,
                    ctx.UnitSpec,
                    ctx.IsCargoContainer,
                    consumedRtsCap: consumedCap);
            }
        }
    }
}
