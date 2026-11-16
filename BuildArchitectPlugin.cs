using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Pigeon.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MycopunkBuildArchitect
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [MycoMod(null, ModFlags.IsClientSide)]
    public sealed class BuildArchitectPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "mycopunk.buildarchitect";
        public const string PluginName = "Mycopunk Build Architect";
        public const string PluginVersion = "0.4.1";

        internal static ManualLogSource Log = null!;
        internal static BuildArchitectPlugin? Instance { get; private set; }

        private Harmony? _harmony;
        internal Window? CurrentWindow;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            _harmony = new Harmony(PluginGuid);
            try
            {
                PatchWindow("GearDetailsWindow");
                PatchWindow("OuroGearWindow");
            }
            catch (Exception ex) { Logger.LogError("Patch failed: " + ex); }
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        private void PatchWindow(string typeName)
        {
            var t = AccessTools.TypeByName(typeName);
            if (t == null) { Logger.LogWarning(typeName + " not found"); return; }
            var open = AccessTools.Method(t, "OnOpen");
            var close = AccessTools.Method(t, "OnCloseCallback");
            if (open != null)
                _harmony!.Patch(open, postfix: new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(OnWindowOpened)));
            if (close != null)
                _harmony!.Patch(close, postfix: new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(OnWindowClosed)));
            Logger.LogInfo("Patched " + typeName);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            try { _harmony?.UnpatchSelf(); } catch { }
            ArchitectUI.Dispose();
        }

        private static void OnWindowOpened(object __instance)
        {
            if (Instance == null) return;
            Instance.CurrentWindow = __instance as Window;
        }

        private static void OnWindowClosed()
        {
            if (Instance == null) return;
            Instance.CurrentWindow = null;
        }

        // ---------- универсальные хелперы ----------

        internal static IUpgradable? GetGear(Window? window)
        {
            if (window == null) return null;
            if (window is GearDetailsWindow gdw)
            {
                try { return gdw.UpgradablePrefab; } catch { return null; }
            }
            try
            {
                var f = window.GetType().GetField("selectedGear",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                return f?.GetValue(window) as IUpgradable;
            }
            catch { return null; }
        }

        internal static ModuleEquipSlots? GetEquipSlots(Window? window)
        {
            if (window == null) return null;
            try
            {
                var f = window.GetType().GetField("equipSlots",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                return f?.GetValue(window) as ModuleEquipSlots;
            }
            catch { return null; }
        }

        internal static void NotifyUpgradesChanged(Window? window)
        {
            if (window == null) return;
            try
            {
                window.GetType()
                      .GetMethod("OnUpgradesChanged", BindingFlags.Public | BindingFlags.Instance)
                      ?.Invoke(window, null);
            }
            catch (Exception ex) { Log.LogWarning("NotifyUpgradesChanged: " + ex.Message); }
        }

        internal static bool IsOuroWindow(Window? window)
        {
            return window != null && window.GetType().Name == "OuroGearWindow";
        }

        internal static bool IsOuroAutoApply(Window? window)
        {
            if (!IsOuroWindow(window)) return false;
            try
            {
                var t = AccessTools.TypeByName("OuroObjective");
                var p = t?.GetProperty("AutoApplyUpgrades", BindingFlags.Public | BindingFlags.Static);
                return p != null && (bool)(p.GetValue(null) ?? false);
            }
            catch { return false; }
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[Key.F7].wasPressedThisFrame) ArchitectUI.ToggleVisibility();
        }

        private void OnGUI() { ArchitectUI.DrawIfVisible(); }
    }
}