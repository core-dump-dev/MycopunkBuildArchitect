using System;
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
        public const string PluginVersion = "0.3.0";

        internal static ManualLogSource Log = null!;
        internal static BuildArchitectPlugin? Instance { get; private set; }

        private Harmony? _harmony;
        internal GearDetailsWindow? CurrentWindow;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            _harmony = new Harmony(PluginGuid);
            try
            {
                var t = AccessTools.TypeByName("GearDetailsWindow");
                if (t == null) { Logger.LogWarning("GearDetailsWindow not found"); return; }
                var open = AccessTools.Method(t, "OnOpen");
                var close = AccessTools.Method(t, "OnCloseCallback");
                if (open != null) _harmony.Patch(open, postfix: new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(OnWindowOpened)));
                if (close != null) _harmony.Patch(close, postfix: new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(OnWindowClosed)));
            }
            catch (Exception ex) { Logger.LogError("Patch failed: " + ex); }
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        private static void OnWindowOpened(object __instance)
        {
            if (Instance == null) return;
            Instance.CurrentWindow = __instance as GearDetailsWindow;
        }

        private static void OnWindowClosed() { if (Instance != null) Instance.CurrentWindow = null; }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[Key.F7].wasPressedThisFrame) ArchitectUI.ToggleVisibility();
        }

        private void OnGUI() { ArchitectUI.DrawIfVisible(); }
    }
}