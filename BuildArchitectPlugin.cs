using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MycopunkBuildArchitect
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class BuildArchitectPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "mycopunk.buildarchitect";
        public const string PluginName = "Mycopunk Build Architect";
        public const string PluginVersion = "0.2.0";

        internal static ManualLogSource Log = null!;
        internal static BuildArchitectPlugin? Instance { get; private set; }

        private Harmony? _harmony;

        internal object? CurrentUpgradeWindow;
        internal object? CurrentGear;
        internal bool IsWindowOpen => CurrentUpgradeWindow != null;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            _harmony = new Harmony(PluginGuid);
            PatchGameWindows();

            Log.LogInfo($"{PluginName} v{PluginVersion} (Standalone Solver) loaded.");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        private void PatchGameWindows()
        {
            var gearDetailsType = AccessTools.TypeByName("GearDetailsWindow");
            if (gearDetailsType == null)
            {
                Log.LogWarning("GearDetailsWindow not found.");
                return;
            }

            var onOpen = AccessTools.Method(gearDetailsType, "OnOpen");
            var onClose = AccessTools.Method(gearDetailsType, "OnCloseCallback");

            if (onOpen != null)
                _harmony!.Patch(onOpen, postfix: new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(OnGearWindowOpened)));
            if (onClose != null)
                _harmony!.Patch(onClose, postfix: new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(OnGearWindowClosed)));
        }

        private static void OnGearWindowOpened(object __instance)
        {
            if (Instance == null) return;
            Instance.CurrentUpgradeWindow = __instance;

            var gearProp = __instance.GetType().GetProperty("UpgradablePrefab", BindingFlags.Instance | BindingFlags.Public);
            Instance.CurrentGear = gearProp?.GetValue(__instance);

            Log.LogInfo("Gear window opened.");
        }

        private static void OnGearWindowClosed()
        {
            if (Instance == null) return;
            Instance.CurrentUpgradeWindow = null;
            Instance.CurrentGear = null;
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard[Key.F7].wasPressedThisFrame)
                ArchitectUI.ToggleVisibility();
        }
    }
}