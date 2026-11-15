using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MycopunkBuildArchitect
{
    public static class ArchitectUI
    {
        private static bool _visible;
        private static Rect _windowRect = new(30, 40, 580, 750); // Немного уменьшили высоту окна
        private static Vector2 _scroll;
        private static Vector2 _scanScroll;
        private static readonly MultiLayoutManager _manager = new();
        private static bool _isWorking;
        private static string _status = "Open gear window, then Scan or Generate.";

        private static bool _includeOuroboros = false;
        private static bool _ignoreRotationLocks = false;
        private static bool _showScanList = true; // Новая галочка для скрытия

        private static List<UpgradeInstance> _scannedCandidates = new();
        private static readonly Dictionary<int, bool> _requiredFlags = new();
        private static bool _scanned;

        public static void ToggleVisibility() => _visible = !_visible;

        public static void DrawIfVisible()
        {
            if (!_visible) return;
            var plugin = BuildArchitectPlugin.Instance;
            if (plugin == null || plugin.CurrentWindow == null) return;
            _windowRect = GUI.Window(182742, _windowRect, DrawWindow, "BUILD ARCHITECT");
        }

        private static void DrawWindow(int id)
        {
            GUILayout.BeginVertical();
            var win = BuildArchitectPlugin.Instance?.CurrentWindow;
            string gearName = "?";
            try { gearName = win?.UpgradablePrefab?.Info?.Name ?? "?"; } catch { }
            GUILayout.Label("Gear: " + gearName);
            GUILayout.Space(4);

            _includeOuroboros = GUILayout.Toggle(_includeOuroboros, " Include Ouroboros upgrades (steals from others)");
            _ignoreRotationLocks = GUILayout.Toggle(_ignoreRotationLocks, " Ignore rotation level locks (Cheat mode)");
            GUILayout.Space(4);

            // Кнопка сворачивания/разворачивания списка
            if (GUILayout.Button(_showScanList ? "▼ Hide available upgrades" : "▶ Show available upgrades", GUILayout.Height(22)))
            {
                _showScanList = !_showScanList;
            }

            GUI.enabled = !_isWorking;
            if (GUILayout.Button("1. Scan available upgrades", GUILayout.Height(28)))
            {
                if (win != null)
                {
                    _scannedCandidates = ScanCandidates(win);
                    _requiredFlags.Clear();
                    foreach (var c in _scannedCandidates)
                        _requiredFlags[LayoutSolver.GetSourceId(c)] = false;
                    _scanned = true;
                    _showScanList = true; // Автоматически открываем после скана
                    _status = $"Scanned {_scannedCandidates.Count} upgrade type(s).";
                }
            }
            GUI.enabled = true;

            if (_scanned && _scannedCandidates.Count > 0 && _showScanList)
            {
                GUILayout.Label($"Available ({_scannedCandidates.Count}) — mark ★ to force into layout:");
                _scanScroll = GUILayout.BeginScrollView(_scanScroll, GUILayout.Height(200));
                foreach (var c in _scannedCandidates)
                {
                    int sid = LayoutSolver.GetSourceId(c);
                    bool req = _requiredFlags.TryGetValue(sid, out var v) && v;
                    string name = LayoutSolver.SafeName(c);
                    string desc = LayoutSolver.SafeDescription(c);
                    string label = string.IsNullOrEmpty(desc) ? name : name + "  —  " + desc;

                    GUILayout.BeginHorizontal();
                    bool newReq = GUILayout.Toggle(req, req ? " ★ " : " ☐ ", GUILayout.Width(30));
                    GUILayout.Label(label);
                    GUILayout.EndHorizontal();

                    if (newReq != req) _requiredFlags[sid] = newReq;
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(4);
            GUI.enabled = !_isWorking;
            if (GUILayout.Button("2. Generate layouts", GUILayout.Height(32)))
            {
                if (win != null && BuildArchitectPlugin.Instance != null)
                {
                    if (!_scanned)
                    {
                        _scannedCandidates = ScanCandidates(win);
                        foreach (var c in _scannedCandidates)
                            if (!_requiredFlags.ContainsKey(LayoutSolver.GetSourceId(c)))
                                _requiredFlags[LayoutSolver.GetSourceId(c)] = false;
                        _scanned = true;
                    }

                    _isWorking = true;
                    _status = "Searching...";
                    BuildArchitectPlugin.Instance.StartCoroutine(RunGenerate(win));
                }
            }
            GUI.enabled = true;

            GUILayout.Space(6);
            GUILayout.Label($"Results ({_manager.FoundVariants.Count}):");
            
            // Используем ExpandHeight, чтобы список заполнил оставшееся место в окне (убирает пустоту)
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            if (_manager.FoundVariants.Count == 0)
                GUILayout.Label("(none yet — press Generate)");

            foreach (var v in _manager.FoundVariants)
            {
                GUILayout.BeginVertical("box");
                string reqWarn = v.RequiredMissing > 0 ? $"  ⚠ {v.RequiredMissing} REQUIRED missing" : "";
                GUILayout.Label($"{v.Name}  [{v.Strategy}]  {v.PlacedCount}/{v.TotalCount} upgrades, {v.CellCount} cells{reqWarn}");

                foreach (var p in v.Upgrades)
                {
                    string line = "  • " + p.DisplayName;
                    if (!string.IsNullOrEmpty(p.Description)) line += "\n      * " + p.Description;
                    GUILayout.Label(line);
                }

                if (v.Missing.Count > 0)
                    GUILayout.Label("  Missing: " + string.Join(", ", v.Missing));

                GUI.enabled = !_isWorking;
                if (GUILayout.Button("Apply this layout", GUILayout.Height(26)))
                {
                    if (win != null && BuildArchitectPlugin.Instance != null)
                    {
                        _isWorking = true;
                        _status = "Applying " + v.Name + "...";
                        BuildArchitectPlugin.Instance.StartCoroutine(RunApply(win, v));
                    }
                }
                GUI.enabled = true;
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            GUILayout.Label(_status);
            if (GUILayout.Button("Close", GUILayout.Width(75))) _visible = false;

            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }

        private static List<UpgradeInstance> ScanCandidates(GearDetailsWindow win)
        {
            try
            {
                var solver = new LayoutSolver(win, _includeOuroboros, _ignoreRotationLocks);
                var all = solver.CollectCandidates();
                return solver.PickForLayout(all);
            }
            catch (System.Exception ex)
            {
                BuildArchitectPlugin.Log.LogError("Scan failed: " + ex);
                return new List<UpgradeInstance>();
            }
        }

        private static HashSet<int> BuildRequiredSet()
        {
            var set = new HashSet<int>();
            foreach (var kv in _requiredFlags)
                if (kv.Value) set.Add(kv.Key);
            return set;
        }

        private static IEnumerator RunGenerate(GearDetailsWindow win)
        {
            yield return null;
            var required = BuildRequiredSet();
            bool done = false;
            var routine = _manager.GenerateVariants(win, _includeOuroboros, _ignoreRotationLocks, required, () => done = true);
            while (!done)
            {
                object? cur = null;
                bool has;
                try { has = routine.MoveNext(); if (has) cur = routine.Current; }
                catch (System.Exception ex) { BuildArchitectPlugin.Log.LogError("Generate: " + ex); break; }
                if (!has) break;
                yield return cur;
            }
            _status = $"Done. {_manager.FoundVariants.Count} layout(s) generated ({required.Count} required).";
            _isWorking = false;
        }

        private static IEnumerator RunApply(GearDetailsWindow win, LayoutVariant v)
        {
            yield return null;
            bool result = false;
            IEnumerator? routine = null;

            try
            {
                var solver = new LayoutSolver(win, _includeOuroboros, _ignoreRotationLocks);
                routine = solver.Apply(v, r => result = r);
            }
            catch (System.Exception ex) { BuildArchitectPlugin.Log.LogError("Apply init: " + ex); }

            if (routine != null)
            {
                while (true)
                {
                    object? cur = null;
                    bool has;
                    try { has = routine.MoveNext(); if (has) cur = routine.Current; }
                    catch (System.Exception ex) { BuildArchitectPlugin.Log.LogError("Apply step: " + ex); break; }
                    if (!has) break;
                    yield return cur;
                }
            }

            _status = result ? ("Applied " + v.Name + $" — {v.PlacedCount}/{v.TotalCount} placed.") : "Apply failed.";
            _isWorking = false;
        }
    }
}