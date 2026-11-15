using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MycopunkBuildArchitect
{
    public static class ArchitectUI
    {
        private static bool _visible;
        private static Rect _windowRect = new(30, 40, 580, 750);
        private static Vector2 _scroll;
        private static Vector2 _scanScroll;
        private static readonly MultiLayoutManager _manager = new();
        private static bool _isWorking;
        private static string _status = "Open gear window, then Scan or Generate.";

        private static bool _includeOuroboros = false;
        private static bool _ignoreRotationLocks = false;
        private static bool _showScanList = true;

        private static List<UpgradeInstance> _scannedCandidates = new();
        private static readonly Dictionary<int, bool> _requiredFlags = new();
        private static bool _scanned;

        private static Texture2D _bgTex = null!;
        private static Texture2D _boxTex = null!;

        public static void ToggleVisibility() => _visible = !_visible;

        private static void EnsureTextures()
        {
            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, new Color(0.1f, 0.1f, 0.1f, 1f));
                _bgTex.Apply();
            }
            if (_boxTex == null)
            {
                _boxTex = new Texture2D(1, 1);
                _boxTex.SetPixel(0, 0, new Color(0.15f, 0.15f, 0.15f, 1f));
                _boxTex.Apply();
            }
        }

        public static void DrawIfVisible()
        {
            if (!_visible) return;
            var plugin = BuildArchitectPlugin.Instance;
            if (plugin == null || plugin.CurrentWindow == null) return;

            EnsureTextures();

            var prevWindow = GUI.skin.window;
            var prevBox = GUI.skin.box;
            var prevLabel = GUI.skin.label;

            var winStyle = new GUIStyle(prevWindow);
            winStyle.normal.background = _bgTex;
            winStyle.onNormal.background = _bgTex;
            winStyle.normal.textColor = Color.white;
            GUI.skin.window = winStyle;

            var boxStyle = new GUIStyle(prevBox);
            boxStyle.normal.background = _boxTex;
            GUI.skin.box = boxStyle;

            var labelStyle = new GUIStyle(prevLabel);
            labelStyle.normal.textColor = Color.white;
            GUI.skin.label = labelStyle;

            _windowRect = GUI.Window(182742, _windowRect, DrawWindow, "BUILD ARCHITECT");

            GUI.skin.window = prevWindow;
            GUI.skin.box = prevBox;
            GUI.skin.label = prevLabel;
        }

        private static void DrawWindow(int id)
        {
            GUILayout.BeginVertical();
            var win = BuildArchitectPlugin.Instance?.CurrentWindow;
            string gearName = "?";
            try { gearName = win?.UpgradablePrefab?.Info?.Name ?? "?"; } catch { }
            
            GUILayout.Label("<b>Gear: " + gearName + "</b>");
            GUILayout.Space(4);

            _includeOuroboros = GUILayout.Toggle(_includeOuroboros, " Include Ouroboros upgrades (steals from others)");
            _ignoreRotationLocks = GUILayout.Toggle(_ignoreRotationLocks, " Ignore rotation level locks (Cheat mode)");
            GUILayout.Space(4);

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
                    _showScanList = true;
                    _status = $"Scanned {_scannedCandidates.Count} upgrade type(s).";
                }
            }
            GUI.enabled = true;

            if (_scanned && _scannedCandidates.Count > 0 && _showScanList)
            {
                GUILayout.Label($"<b>Available ({_scannedCandidates.Count})</b> — mark ★ to force into layout:");
                _scanScroll = GUILayout.BeginScrollView(_scanScroll, GUILayout.Height(200));
                foreach (var c in _scannedCandidates)
                {
                    int sid = LayoutSolver.GetSourceId(c);
                    bool req = _requiredFlags.TryGetValue(sid, out var v) && v;
                    string name = LayoutSolver.SafeName(c);
                    string desc = LayoutSolver.SafeDescription(c);
                    string label = string.IsNullOrEmpty(desc) ? name : name + "\n    " + desc;

                    GUILayout.BeginHorizontal();
                    bool newReq = GUILayout.Toggle(req, req ? " ★ " : " ☐ ", GUILayout.Width(30));
                    
                    var wrapStyle = new GUIStyle(GUI.skin.label);
                    wrapStyle.wordWrap = true;
                    GUILayout.Label(label, wrapStyle);
                    GUILayout.EndHorizontal();
                    GUILayout.Space(2);

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
            GUILayout.Label($"<b>Results ({_manager.FoundVariants.Count}):</b>");
            
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            if (_manager.FoundVariants.Count == 0)
                GUILayout.Label("(none yet — press Generate)");

            foreach (var v in _manager.FoundVariants)
            {
                GUILayout.BeginVertical("box");
                string reqWarn = v.RequiredMissing > 0 ? $"  <color=red>⚠ {v.RequiredMissing} REQUIRED missing</color>" : "";
                
                // Заменили "cells" на "cells free"
                GUILayout.Label($"<b>{v.Name}  [{v.Strategy}]</b>  {v.PlacedCount}/{v.TotalCount} upgrades, {v.FreeCells} cells free{reqWarn}");

                foreach (var p in v.Upgrades)
                {
                    string line = "  • " + p.DisplayName;
                    if (!string.IsNullOrEmpty(p.Description)) line += "\n      * " + p.Description;
                    GUILayout.Label(line);
                }

                if (v.Missing.Count > 0)
                    GUILayout.Label("  <color=orange>Missing: " + string.Join(", ", v.Missing) + "</color>");

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
                GUILayout.Space(4);
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