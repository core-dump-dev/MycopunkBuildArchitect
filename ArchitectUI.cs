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

        private static Texture2D? _bgTex;
        private static Texture2D? _boxTex;

        // Cached IMGUI styles — allocated once, reused every frame instead of per-draw.
        private static GUIStyle? _winStyle;
        private static GUIStyle? _boxStyle;
        private static GUIStyle? _labelStyle;
        private static GUIStyle? _wrapStyle;

        public static void ToggleVisibility() => _visible = !_visible;

        /// <summary>Called when the active GearDetailsWindow changes — clears per-gear state.</summary>
        internal static void OnWindowChanged()
        {
            _requiredFlags.Clear();
            _scannedCandidates.Clear();
            _scanned = false;
            _manager.FoundVariants.Clear();
            _scroll = Vector2.zero;
            _scanScroll = Vector2.zero;
            _status = "Open gear window, then Scan or Generate.";
        }

        /// <summary>Destroys runtime textures/styles — called from BuildArchitectPlugin.OnDestroy.</summary>
        internal static void Dispose()
        {
            if (_bgTex != null) { Object.Destroy(_bgTex); _bgTex = null; }
            if (_boxTex != null) { Object.Destroy(_boxTex); _boxTex = null; }
            _winStyle = null;
            _boxStyle = null;
            _labelStyle = null;
            _wrapStyle = null;
        }

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

        private static void EnsureStyles()
        {
            if (_winStyle == null)
            {
                _winStyle = new GUIStyle(GUI.skin.window);
                _winStyle.normal.background = _bgTex;
                _winStyle.onNormal.background = _bgTex;
                _winStyle.normal.textColor = Color.white;
            }
            if (_boxStyle == null)
            {
                _boxStyle = new GUIStyle(GUI.skin.box);
                _boxStyle.normal.background = _boxTex;
            }
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label);
                _labelStyle.normal.textColor = Color.white;
            }
            if (_wrapStyle == null)
            {
                _wrapStyle = new GUIStyle(GUI.skin.label);
                _wrapStyle.wordWrap = true;
                _wrapStyle.normal.textColor = Color.white;
            }
        }

        public static void DrawIfVisible()
        {
            if (!_visible) return;
            var plugin = BuildArchitectPlugin.Instance;
            if (plugin == null || plugin.CurrentWindow == null) return;

            EnsureTextures();
            EnsureStyles();

            var prevWindow = GUI.skin.window;
            var prevBox = GUI.skin.box;
            var prevLabel = GUI.skin.label;

            GUI.skin.window = _winStyle!;
            GUI.skin.box = _boxStyle!;
            GUI.skin.label = _labelStyle!;

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
                _showScanList = !_showScanList;

            GUI.enabled = !_isWorking;
            if (GUILayout.Button("1. Scan available upgrades", GUILayout.Height(28)))
            {
                if (win != null)
                {
                    _scannedCandidates = ScanCandidates(win);
                    // Preserve the user's ★ selections across rescans — only seed new IDs.
                    foreach (var c in _scannedCandidates)
                    {
                        int sourceId = LayoutSolver.GetSourceId(c);
                        if (!_requiredFlags.ContainsKey(sourceId))
                            _requiredFlags[sourceId] = false;
                    }
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
                    GUILayout.Label(label, _wrapStyle!);
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
                        {
                            int sid = LayoutSolver.GetSourceId(c);
                            if (!_requiredFlags.ContainsKey(sid)) _requiredFlags[sid] = false;
                        }
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
            try
            {
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
            }
            finally
            {
                _isWorking = false;
            }
        }

        private static IEnumerator RunApply(GearDetailsWindow win, LayoutVariant v)
        {
            yield return null;
            try
            {
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

                _status = result
                    ? ("Applied " + v.Name + $" — {v.PlacedCount}/{v.TotalCount} placed.")
                    : "Apply failed.";
            }
            finally
            {
                _isWorking = false;
            }
        }
    }
}