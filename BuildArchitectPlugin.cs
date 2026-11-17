using System;
using System.Collections.Generic;
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
        public const string PluginVersion = "0.6.0";

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
            try
            {
                PatchWindowClicks("GearDetailsWindow");
                PatchWindowClicks("OuroGearWindow");
            }
            catch (Exception ex) { Logger.LogError("Click patch failed: " + ex); }
            try { PatchUiRaycaster(); }
            catch (Exception ex) { Logger.LogError("UIRaycaster patch failed: " + ex); }
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

        private void PatchWindowClicks(string typeName)
        {
            var t = AccessTools.TypeByName(typeName);
            if (t == null) return;
            var prefix = new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(BlockGameClick));
            int n = 0;
            foreach (var name in new[] { "OnClickUp", "OnRightClickUp", "OnClick", "OnRightClick" })
            {
                var m = AccessTools.Method(t, name);
                if (m == null) { Logger.LogWarning($"{typeName}.{name} not found"); continue; }
                try { _harmony!.Patch(m, prefix: prefix); n++; }
                catch (Exception ex) { Logger.LogWarning($"{typeName}.{name}: {ex.Message}"); }
            }
            Logger.LogInfo($"{typeName} click handlers patched: {n}");
        }

        private void PatchUiRaycaster()
        {
            var t = AccessTools.TypeByName("UIRaycaster")
                    ?? AccessTools.TypeByName("Pigeon.UI.UIRaycaster")
                    ?? AccessTools.TypeByName("Pigeon.UIRaycaster");
            if (t == null) { Logger.LogWarning("UIRaycaster type not found."); return; }

            var prefix = new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(BlockUiRaycast));
            var probes = new List<Type>();
            foreach (var n in new[]
                     {
                         "GearUpgradeUI", "UpgradeEquipCell", "UpgradeIcon", "SkinEquipButton",
                         "Pigeon.UI.GearUpgradeUI", "Pigeon.UI.UpgradeEquipCell",
                         "Pigeon.UI.UpgradeIcon", "Pigeon.UI.SkinEquipButton",
                         "UnityEngine.UI.Graphic"
                     })
            {
                var x = AccessTools.TypeByName(n);
                if (x != null && !probes.Contains(x)) probes.Add(x);
            }
            probes.Add(typeof(Component));
            probes.Add(typeof(MonoBehaviour));
            probes.Add(typeof(object));

            int patched = 0;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Static | BindingFlags.Instance))
            {
                if (m.ReturnType != typeof(bool)) continue;
                if (!m.Name.StartsWith("Raycast", StringComparison.Ordinal)) continue;

                if (!m.IsGenericMethodDefinition)
                {
                    try { _harmony!.Patch(m, prefix: prefix); patched++; Logger.LogInfo("Patched UIRaycaster." + m.Name); }
                    catch (Exception ex) { Logger.LogWarning($"UIRaycaster.{m.Name}: {ex.Message}"); }
                    continue;
                }

                foreach (var p in probes)
                {
                    MethodInfo closed;
                    try { closed = m.MakeGenericMethod(p); }
                    catch { continue; }
                    try
                    {
                        _harmony!.Patch(closed, prefix: prefix);
                        patched++;
                        Logger.LogInfo($"Patched UIRaycaster.{m.Name}<{p.Name}>");
                        break;
                    }
                    catch (Exception ex) { Logger.LogWarning($"UIRaycaster.{m.Name}<{p.Name}>: {ex.Message}"); }
                }
            }
            Logger.LogInfo($"UIRaycaster patches: {patched}");
        }

        private static bool BlockGameClick()
        {
            return !ArchitectUI.BlockGameUiInput;
        }

        private static bool BlockUiRaycast(ref bool __result)
        {
            if (!ArchitectUI.BlockGameUiInput) return true;
            __result = false;
            return false;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            ArchitectUI.Dispose();
            try { _harmony?.UnpatchSelf(); } catch { }
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