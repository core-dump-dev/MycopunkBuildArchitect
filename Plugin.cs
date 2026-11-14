using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace MycopunkBuildArchitect
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("coruscnium.gridsolvercoru", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("sparroh.uilibrary", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class BuildArchitectPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "mycopunk.buildarchitect";
        public const string PluginName = "Mycopunk Build Architect";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log = null!;
        private Harmony? _harmony;
        private object? _gearWindow;
        private Rect _windowRect = new Rect(24, 110, 370, 440);
        private bool _visible;
        private string _profileName = "My build";
        private string _status = "Hover an unlocked upgrade and press N to select it in GridSolverCoru.";
        private Vector2 _scroll;

        private ProfileDatabase _database = new ProfileDatabase();

        private string StorePath => Path.Combine(Paths.ConfigPath, "MycopunkBuildArchitect", "profiles.json");

        private void Awake()
        {
            Log = Logger;
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            LoadDatabase();

            _harmony = new Harmony(PluginGuid);
            PatchWindowLifecycle();

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
            Logger.LogInfo("Build Architect uses GridSolverCoru for selection and automatic placement.");
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        private void PatchWindowLifecycle()
        {
            var windowType = AccessTools.TypeByName("GearDetailsWindow");
            if (windowType == null)
            {
                Logger.LogWarning("GearDetailsWindow type was not found. The manager will wait for the game window.");
                return;
            }

            var open = AccessTools.Method(windowType, "OnOpen");
            var close = AccessTools.Method(windowType, "OnCloseCallback");

            var opened = AccessTools.Method(typeof(BuildArchitectPlugin), nameof(WindowOpened));
            var closed = AccessTools.Method(typeof(BuildArchitectPlugin), nameof(WindowClosed));

            if (open != null) _harmony!.Patch(open, postfix: new HarmonyMethod(opened));
            else Logger.LogWarning("GearDetailsWindow.OnOpen was not found.");

            if (close != null) _harmony!.Patch(close, postfix: new HarmonyMethod(closed));
            else Logger.LogWarning("GearDetailsWindow.OnCloseCallback was not found.");
        }

        private static void WindowOpened(object __instance)
        {
            var plugin = FindPlugin();
            if (plugin == null) return;
            plugin._gearWindow = __instance;
            plugin._visible = true;
            plugin._status = "Window ready. Select upgrades with N, then save a profile.";
        }

        private static void WindowClosed(object __instance)
        {
            var plugin = FindPlugin();
            if (plugin == null) return;
            if (ReferenceEquals(plugin._gearWindow, __instance))
            {
                plugin._gearWindow = null;
                plugin._visible = false;
            }
        }

        private static BuildArchitectPlugin? FindPlugin()
        {
            return FindObjectOfType<BuildArchitectPlugin>();
        }

        private void Update()
        {
            // The game can switch gear without closing the details window.
            if (_gearWindow is Component component && component != null && !component.gameObject.activeInHierarchy)
            {
                _gearWindow = null;
                _visible = false;
            }

            if (Input.GetKeyDown(KeyCode.F7))
                _visible = !_visible;
        }

        private void OnGUI()
        {
            if (!_visible || _gearWindow == null) return;
            if (!IsWindowAlive(_gearWindow)) return;

            _windowRect = GUI.Window(182741, _windowRect, DrawWindow, "BUILD ARCHITECT");
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            GUILayout.Label("Gear: " + CurrentGearKey());
            GUILayout.Space(5);
            GUILayout.Label("Profile name");
            _profileName = GUILayout.TextField(_profileName ?? "My build", 40);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save selected upgrades", GUILayout.Height(30)))
                SaveCurrentSelection();
            if (GUILayout.Button("Refresh", GUILayout.Width(85), GUILayout.Height(30)))
                _status = "Profiles refreshed.";
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("Saved builds for this gear");
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(235));

            var gearKey = CurrentGearKey();
            var profiles = _database.Profiles
                .Where(p => string.Equals(p.GearKey, gearKey, StringComparison.Ordinal))
                .OrderBy(p => p.Name)
                .ToList();

            if (profiles.Count == 0)
                GUILayout.Label("No saved builds yet. Select upgrades with N and save them.");

            foreach (var profile in profiles)
            {
                GUILayout.BeginVertical("box");
                GUILayout.Label(profile.Name + "  (" + profile.Items.Sum(i => i.Count) + " upgrades)");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Apply / Solve", GUILayout.Height(28)))
                    ApplyProfile(profile);
                if (GUILayout.Button("Delete", GUILayout.Width(70), GUILayout.Height(28)))
                    DeleteProfile(profile);
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }

            GUILayout.EndScrollView();
            GUILayout.Space(5);
            GUILayout.Label(_status, GUILayout.MinHeight(48));
            GUILayout.BeginHorizontal();
            GUILayout.Label("F7: toggle manager");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(75)))
                _visible = false;
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }

        private void SaveCurrentSelection()
        {
            try
            {
                var selected = GetSelectedUpgradeUis();
                if (selected.Count == 0)
                {
                    _status = "Nothing selected. Hover unlocked upgrades and press N first.";
                    return;
                }

                var items = new List<ProfileItem>();
                foreach (var ui in selected)
                {
                    var instance = ReadMember(ui, "Upgrade");
                    if (instance == null) continue;
                    var sourceId = ReadMember(instance, "SourceUpgradeID")?.ToString();
                    if (string.IsNullOrWhiteSpace(sourceId)) continue;

                    var name = ReadMember(ReadMember(instance, "Upgrade"), "Name")?.ToString() ?? sourceId;
                    var existing = items.FirstOrDefault(x => x.SourceId == sourceId);
                    if (existing == null)
                        items.Add(new ProfileItem { SourceId = sourceId, DisplayName = name, Count = 1 });
                    else
                        existing.Count++;
                }

                if (items.Count == 0)
                {
                    _status = "Could not read upgrade identifiers from the current selection.";
                    return;
                }

                var gearKey = CurrentGearKey();
                var profile = _database.Profiles.FirstOrDefault(p =>
                    p.GearKey == gearKey && p.Name.Equals(_profileName.Trim(), StringComparison.OrdinalIgnoreCase));

                if (profile == null)
                {
                    profile = new BuildProfile { GearKey = gearKey, Name = string.IsNullOrWhiteSpace(_profileName) ? "My build" : _profileName.Trim() };
                    _database.Profiles.Add(profile);
                }

                profile.Items = items;
                SaveDatabase();
                _status = "Saved '" + profile.Name + "' with " + items.Sum(i => i.Count) + " upgrades.";
            }
            catch (Exception ex)
            {
                Logger.LogError("Save profile failed: " + ex);
                _status = "Save failed: " + ex.Message;
            }
        }

        private void ApplyProfile(BuildProfile profile)
        {
            try
            {
                if (_gearWindow is not Component windowComponent)
                {
                    _status = "The active gear window is not available.";
                    return;
                }

                var solverUi = GetGridSolverUi();
                if (solverUi == null)
                {
                    _status = "GridSolverCoru UI was not found. Check that GridSolverCoru is installed.";
                    return;
                }

                var selectedField = FindField(solverUi.GetType(), "_selectedUpgrades");
                var solveEnabledField = FindField(solverUi.GetType(), "_solveButtonEnabled");
                var selectedObject = selectedField?.GetValue(solverUi) as IDictionary;

                if (selectedField == null || solveEnabledField == null || selectedObject == null)
                {
                    _status = "GridSolverCoru API did not match the expected version.";
                    return;
                }

                selectedObject.Clear();

                var uiType = AccessTools.TypeByName("GearUpgradeUI");
                if (uiType == null)
                {
                    _status = "GearUpgradeUI type was not found.";
                    return;
                }

                var components = windowComponent.GetComponentsInChildren(uiType, true);
                var missing = new List<string>();
                foreach (var item in profile.Items)
                {
                    var remaining = item.Count;
                    foreach (var ui in components)
                    {
                        if (remaining <= 0 || ui == null) continue;
                        var instance = ReadMember(ui, "Upgrade");
                        var sourceId = ReadMember(instance, "SourceUpgradeID")?.ToString();
                        if (!string.Equals(sourceId, item.SourceId, StringComparison.Ordinal)) continue;

                        var instanceId = ReadMember(instance, "InstanceID");
                        if (instanceId == null) continue;
                        selectedObject[instanceId] = ui;
                        remaining--;
                    }
                    if (remaining > 0) missing.Add(item.DisplayName + " x" + remaining);
                }

                if (selectedObject.Count == 0)
                {
                    _status = "None of the saved upgrades are currently available for this gear.";
                    return;
                }

                solveEnabledField.SetValue(solverUi, true);
                var solve = AccessTools.Method(solverUi.GetType(), "OnSolveClicked");
                if (solve == null)
                {
                    _status = "GridSolverCoru solve action could not be found.";
                    return;
                }

                solve.Invoke(solverUi, null);
                _status = missing.Count == 0
                    ? "Submitted " + selectedObject.Count + " upgrades to the solver."
                    : "Submitted available upgrades. Missing: " + string.Join(", ", missing);
            }
            catch (Exception ex)
            {
                Logger.LogError("Apply profile failed: " + ex);
                _status = "Apply failed: " + ex.Message;
            }
        }

        private void DeleteProfile(BuildProfile profile)
        {
            _database.Profiles.Remove(profile);
            SaveDatabase();
            _status = "Deleted '" + profile.Name + "'.";
        }

        private List<object> GetSelectedUpgradeUis()
        {
            var solverUi = GetGridSolverUi();
            if (solverUi == null) return new List<object>();
            var field = FindField(solverUi.GetType(), "_selectedUpgrades");
            var dict = field?.GetValue(solverUi) as IDictionary;
            if (dict == null) return new List<object>();

            var result = new List<object>();
            foreach (DictionaryEntry entry in dict)
                if (entry.Value != null) result.Add(entry.Value);
            return result;
        }

        private object? GetGridSolverUi()
        {
            var pluginType = AccessTools.TypeByName("GridSolverPlugin");
            if (pluginType == null) return null;

            var instance = ReadStaticMember(pluginType, "Instance");
            if (instance == null) return null;

            var solver = ReadMember(instance, "upgradeSolver");
            return solver == null ? null : ReadMember(solver, "SolverUI");
        }

        private string CurrentGearKey()
        {
            if (_gearWindow == null) return "Unknown gear";
            var gear = ReadMember(_gearWindow, "UpgradablePrefab");
            if (gear == null) return "Unknown gear";
            var name = ReadMember(gear, "name")?.ToString();
            if (!string.IsNullOrWhiteSpace(name)) return name!;
            return gear.ToString() ?? "Unknown gear";
        }

        private static bool IsWindowAlive(object window)
        {
            if (window is Component component)
                return component != null && component.gameObject.activeInHierarchy;
            return window != null;
        }

        private static object? ReadStaticMember(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            return type.GetField(name, flags)?.GetValue(null) ?? type.GetProperty(name, flags)?.GetValue(null, null);
        }

        private static object? ReadMember(object? target, string name)
        {
            if (target == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = target.GetType();
            while (type != null)
            {
                var field = type.GetField(name, flags);
                if (field != null) return field.GetValue(target);
                var property = type.GetProperty(name, flags);
                if (property != null) return property.GetValue(target, null);
                type = type.BaseType;
            }
            return null;
        }

        private static FieldInfo? FindField(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            while (type != null)
            {
                var field = type.GetField(name, flags);
                if (field != null) return field;
                type = type.BaseType!;
            }
            return null;
        }

        private void LoadDatabase()
        {
            try
            {
                if (File.Exists(StorePath))
                {
                    var json = File.ReadAllText(StorePath);
                    _database = JsonUtility.FromJson<ProfileDatabase>(json) ?? new ProfileDatabase();
                    _database.Profiles ??= new List<BuildProfile>();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not load profile database: " + ex.Message);
                _database = new ProfileDatabase();
            }
        }

        private void SaveDatabase()
        {
            try
            {
                File.WriteAllText(StorePath, JsonUtility.ToJson(_database, true));
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not save profile database: " + ex);
                _status = "Could not write profile file: " + ex.Message;
            }
        }
    }
}
