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
        public const string PluginVersion = "0.6.2";

        internal static ManualLogSource Log = null!;
        internal static BuildArchitectPlugin? Instance { get; private set; }
        private Harmony? _harmony;
        internal Window? CurrentWindow;
        internal float OpenedAt;

        private const string SpamMarker = "Look rotation viewing vector is zero";

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
            try { PatchHoverGuard(); }
            catch (Exception ex) { Logger.LogError("Hover patch failed: " + ex); }
            try { PatchGearSwitch(); }
            catch (Exception ex) { Logger.LogError("Gear switch patch failed: " + ex); }
            try { PatchUiButtons(); }
            catch (Exception ex) { Logger.LogError("Button patch failed: " + ex); }
            try { PatchEventSystem(); }
            catch (Exception ex) { Logger.LogError("EventSystem patch failed: " + ex); }
            try { PatchLogSpam(); }
            catch (Exception ex) { Logger.LogError("Log filter failed: " + ex); }
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
                if (m == null) continue;
                try { _harmony!.Patch(m, prefix: prefix); n++; }
                catch (Exception ex) { Logger.LogWarning($"{typeName}.{name}: {ex.Message}"); }
            }
            Logger.LogInfo($"{typeName} click handlers patched: {n}");
        }

        private void PatchHoverGuard()
        {
            var t = AccessTools.TypeByName("UpgradeEquipCell")
                    ?? AccessTools.TypeByName("Pigeon.UI.UpgradeEquipCell");
            if (t == null) { Logger.LogWarning("UpgradeEquipCell not found."); return; }
            var m = AccessTools.Method(t, "OnHover", new[] { typeof(bool) });
            if (m == null) { Logger.LogWarning("UpgradeEquipCell.OnHover not found."); return; }
            _harmony!.Patch(m, prefix: new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(BlockGameClick)));
            Logger.LogInfo("Hover guard installed.");
        }

        private void PatchGearSwitch()
        {
            int n = 0;
            var ouro = AccessTools.TypeByName("OuroGearWindow");
            var select = ouro == null ? null : AccessTools.Method(ouro, "SelectGear");
            if (select != null)
            {
                _harmony!.Patch(select, prefix: new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(BlockGearSelect)));
                n++;
            }
            var gdw = AccessTools.TypeByName("GearDetailsWindow");
            if (gdw != null)
            {
                var prefix = new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(BlockGameClick));
                foreach (var name in new[] { "EquipLoadout", "SaveLoadout", "IncrementLoadoutIcon" })
                {
                    var m = AccessTools.Method(gdw, name);
                    if (m == null) continue;
                    try { _harmony!.Patch(m, prefix: prefix); n++; } catch { }
                }
            }
            Logger.LogInfo($"Gear switch guards: {n}");
        }

        private void PatchUiButtons()
        {
            var typeNames = new[]
            {
                "OuroGearButton", "DefaultButton", "ColorButtonInstant", "LoadoutHoverInfo",
                "LevelUnlockButton", "SkinEquipButton", "ScrollBar", "ScrollArea",
                "Pigeon.UI.OuroGearButton", "Pigeon.UI.DefaultButton", "Pigeon.UI.ColorButtonInstant",
                "Pigeon.UI.LoadoutHoverInfo", "Pigeon.UI.LevelUnlockButton", "Pigeon.UI.SkinEquipButton",
                "Pigeon.UI.ScrollBar", "Pigeon.UI.ScrollArea"
            };
            var methodNames = new HashSet<string>
            {
                "OnClick", "OnClickUp", "OnPointerClick", "OnPointerDown", "OnPointerUp",
                "Click", "Press", "OnPress", "OnSubmit", "Submit", "OnActivate", "Activate"
            };
            var prefix = new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(BlockGameClick));
            var done = new HashSet<MethodBase>();
            int n = 0;
            foreach (var tn in typeNames)
            {
                var cur = AccessTools.TypeByName(tn);
                while (cur != null
                       && cur != typeof(MonoBehaviour)
                       && cur != typeof(Behaviour)
                       && cur != typeof(UnityEngine.Object)
                       && cur != typeof(object))
                {
                    foreach (var m in cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                     | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (!methodNames.Contains(m.Name)) continue;
                        if (m.IsGenericMethodDefinition) continue;
                        if (m.GetParameters().Length != 0) continue;
                        if (!done.Add(m)) continue;
                        try { _harmony!.Patch(m, prefix: prefix); n++; } catch { }
                    }
                    cur = cur.BaseType;
                }
            }
            Logger.LogInfo($"UI button guards: {n}");
        }

        private void PatchEventSystem()
        {
            int n = 0;
            var esType = AccessTools.TypeByName("UnityEngine.EventSystems.EventSystem");
            if (esType == null) { Logger.LogWarning("EventSystem not found."); return; }
            foreach (var m in esType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "IsPointerOverGameObject") continue;
                if (m.IsGenericMethodDefinition) continue;
                try
                {
                    _harmony!.Patch(m, postfix: new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(ForcePointerOver)));
                    n++;
                }
                catch { }
            }
            Logger.LogInfo($"EventSystem guards: {n}");
        }

        private void PatchLogSpam()
        {
            int n = 0;
            var uls = AccessTools.TypeByName("BepInEx.Logging.UnityLogSource");
            if (uls != null)
            {
                var logTypeType = typeof(LogType);
                foreach (var m in uls.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                 | BindingFlags.Instance | BindingFlags.Static
                                                 | BindingFlags.DeclaredOnly))
                {
                    var ps = m.GetParameters();
                    if (ps.Length != 3) continue;
                    if (ps[0].ParameterType != typeof(string)) continue;
                    if (ps[1].ParameterType != typeof(string)) continue;
                    if (ps[2].ParameterType != logTypeType) continue;
                    try
                    {
                        _harmony!.Patch(m, prefix: new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(SkipUnitySpam)));
                        n++;
                        Logger.LogInfo("Spam filter hooked: " + m.Name);
                    }
                    catch (Exception ex) { Logger.LogWarning("Spam filter: " + ex.Message); }
                }
            }
            var mls = AccessTools.TypeByName("BepInEx.Logging.ManualLogSource");
            if (mls != null)
            {
                var log = AccessTools.Method(mls, "Log", new[] { typeof(LogLevel), typeof(object) });
                if (log != null)
                {
                    try
                    {
                        _harmony!.Patch(log, prefix: new HarmonyMethod(typeof(BuildArchitectPlugin), nameof(SkipAnySpam)));
                        n++;
                    }
                    catch (Exception ex) { Logger.LogWarning("Spam filter ManualLogSource: " + ex.Message); }
                }
            }
            Logger.LogInfo($"Log spam filters: {n}");
        }

        private static bool SkipUnitySpam(string __0)
        {
            return __0 == null || __0.IndexOf(SpamMarker, StringComparison.Ordinal) < 0;
        }

        private static bool SkipAnySpam(object __1)
        {
            var s = __1 as string;
            return s == null || s.IndexOf(SpamMarker, StringComparison.Ordinal) < 0;
        }

        private static bool BlockGameClick()
        {
            return !ArchitectUI.BlockGameUiInput;
        }

        private static bool BlockGearSelect(object __instance)
        {
            if (!ArchitectUI.BlockGameUiInput) return true;
            var inst = Instance;
            if (inst == null) return true;
            if (!ReferenceEquals(inst.CurrentWindow, __instance)) return true;
            if (Time.unscaledTime - inst.OpenedAt < 0.3f) return true;
            return false;
        }

        private static void ForcePointerOver(ref bool __result)
        {
            if (ArchitectUI.BlockGameUiInput) __result = true;
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
            Instance.OpenedAt = Time.unscaledTime;
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