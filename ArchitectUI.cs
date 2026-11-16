using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace MycopunkBuildArchitect
{
    public static class ArchitectUI
    {
        private enum UpgradeMark { None, Required, Excluded }

        /// <summary>Per-gear UI/session state. One per weapon, persists across window switches.</summary>
        private sealed class GearState
        {
            public List<UpgradeInstance> ScannedCandidates = new();
            public Dictionary<int, UpgradeMark> Marks = new();
            public bool Scanned;
            public Vector2 Scroll;
            public Vector2 ScanScroll;
            public string Status = "Open gear window, then Scan or Generate.";
            public MultiLayoutManager Manager = new();
            public bool IsWorking;
        }

        private static bool _visible;
        private static Rect _windowRect = new(30, 40, 580, 750);

        // Weak table: state dies with the gear instance. Same weapon = same state.
        private static readonly ConditionalWeakTable<IUpgradable, GearState> _gearStates = new();
        private static readonly GearState _fallbackState = new();

        // These options are user preferences, not per-gear.
        private static bool _includeOuroboros = false;
        private static bool _ignoreRotationLocks = false;
        private static bool _showScanList = true;

        private static Texture2D? _bgTex;
        private static Texture2D? _boxTex;
        private static GUIStyle? _winStyle;
        private static GUIStyle? _boxStyle;
        private static GUIStyle? _labelStyle;
        private static GUIStyle? _wrapStyle;
        private static GUIStyle? _excludedStyle;

        public static void ToggleVisibility() => _visible = !_visible;

        private static GearState CurrentState
        {
            get
            {
                var win = BuildArchitectPlugin.Instance?.CurrentWindow;
                IUpgradable? gear = null;
                try { gear = win?.UpgradablePrefab; } catch { }
                if (gear == null) return _fallbackState;
                return _gearStates.GetValue(gear, _ => new GearState());
            }
        }

        internal static void Dispose()
        {
            if (_bgTex != null) { Object.Destroy(_bgTex); _bgTex = null; }
            if (_boxTex != null) { Object.Destroy(_boxTex); _boxTex = null; }
            _winStyle = null;
            _boxStyle = null;
            _labelStyle = null;
            _wrapStyle = null;
            _excludedStyle = null;
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
            if (_excludedStyle == null)
            {
                _excludedStyle = new GUIStyle(GUI.skin.label);
                _excludedStyle.wordWrap = true;
                _excludedStyle.normal.textColor = new Color(0.55f, 0.55f, 0.55f, 1f);
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
            var st = CurrentState;
            var win = BuildArchitectPlugin.Instance?.CurrentWindow;

            GUILayout.BeginVertical();
            string gearName = "?";
            try { gearName = win?.UpgradablePrefab?.Info?.Name ?? "?"; } catch { }

            GUILayout.Label("<b>Gear: " + gearName + "</b>");
            GUILayout.Space(4);

            _includeOuroboros = GUILayout.Toggle(_includeOuroboros, " Include Ouroboros upgrades (steals from others)");
            _ignoreRotationLocks = GUILayout.Toggle(_ignoreRotationLocks, " Ignore rotation level locks (Cheat mode)");
            GUILayout.Space(4);

            if (GUILayout.Button(_showScanList ? "▼ Hide available upgrades" : "▶ Show available upgrades", GUILayout.Height(22)))
                _showScanList = !_showScanList;

            GUI.enabled = !st.IsWorking;
            if (GUILayout.Button("1. Scan available upgrades", GUILayout.Height(28)))
            {
                if (win != null)
                {
                    st.ScannedCandidates = ScanCandidates(win);
                    // Preserve existing marks — only seed new IDs.
                    foreach (var c in st.ScannedCandidates)
                    {
                        int sourceId = LayoutSolver.GetSourceId(c);
                        if (!st.Marks.ContainsKey(sourceId))
                            st.Marks[sourceId] = UpgradeMark.None;
                    }
                    st.Scanned = true;
                    _showScanList = true;
                    st.Status = $"Scanned {st.ScannedCandidates.Count} upgrade type(s).";
                }
            }
            GUI.enabled = true;

            if (st.Scanned && st.ScannedCandidates.Count > 0 && _showScanList)
            {
                GUILayout.Label($"<b>Available ({st.ScannedCandidates.Count})</b> — LMB on checkbox = ★ required, RMB on checkbox = ✕ excluded:");
                st.ScanScroll = GUILayout.BeginScrollView(st.ScanScroll, GUILayout.Height(200));
                foreach (var c in st.ScannedCandidates)
                {
                    int sid = LayoutSolver.GetSourceId(c);
                    if (!st.Marks.TryGetValue(sid, out var mark)) mark = UpgradeMark.None;
                    var currentMark = mark;

                    string name = LayoutSolver.SafeName(c);
                    string desc = LayoutSolver.SafeDescription(c);
                    string label = string.IsNullOrEmpty(desc) ? name : name + "\n    " + desc;

                    string toggleLabel = currentMark == UpgradeMark.Required ? " ★ "
                                       : currentMark == UpgradeMark.Excluded ? " ✕ "
                                       : " ☐ ";

                    GUILayout.BeginHorizontal();

                    // Reserve the button rect *before* drawing so we can hit-test it
                    // deterministically (GetLastRect after EndHorizontal is unreliable).
                    Rect btnRect = GUILayoutUtility.GetRect(
                        new GUIContent(toggleLabel), GUI.skin.button, GUILayout.Width(30));

                    var ev = Event.current;

                    // RMB only counts when it lands on the checkbox button itself.
                    if (ev.type == EventType.MouseDown && ev.button == 1 && btnRect.Contains(ev.mousePosition))
                    {
                        currentMark = currentMark == UpgradeMark.Excluded
                            ? UpgradeMark.None
                            : UpgradeMark.Excluded;
                        st.Marks[sid] = currentMark;
                        ev.Use();
                    }

                    // Recompute displayed toggle in case RMB just changed the mark.
                    toggleLabel = currentMark == UpgradeMark.Required ? " ★ "
                                : currentMark == UpgradeMark.Excluded ? " ✕ "
                                : " ☐ ";

                    bool lmb = GUI.Button(btnRect, toggleLabel);
                    // Guard against a stray RMB-originated "true" from Button:
                    // only treat the release as LMB when it really is button 0.
                    if (lmb && ev.button == 0)
                    {
                        currentMark = currentMark == UpgradeMark.Required
                            ? UpgradeMark.None
                            : UpgradeMark.Required;
                        st.Marks[sid] = currentMark;
                    }

                    GUILayout.Label(label, currentMark == UpgradeMark.Excluded ? _excludedStyle! : _wrapStyle!);
                    GUILayout.EndHorizontal();

                    GUILayout.Space(2);
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(4);
            GUI.enabled = !st.IsWorking;
            if (GUILayout.Button("2. Generate layouts", GUILayout.Height(32)))
            {
                if (win != null && BuildArchitectPlugin.Instance != null)
                {
                    if (!st.Scanned)
                    {
                        st.ScannedCandidates = ScanCandidates(win);
                        foreach (var c in st.ScannedCandidates)
                        {
                            int sid = LayoutSolver.GetSourceId(c);
                            if (!st.Marks.ContainsKey(sid)) st.Marks[sid] = UpgradeMark.None;
                        }
                        st.Scanned = true;
                    }

                    st.IsWorking = true;
                    st.Status = "Searching...";
                    BuildArchitectPlugin.Instance.StartCoroutine(RunGenerate(win, st));
                }
            }
            GUI.enabled = true;

            GUILayout.Space(6);
            GUILayout.Label($"<b>Results ({st.Manager.FoundVariants.Count}):</b>");

            st.Scroll = GUILayout.BeginScrollView(st.Scroll, GUILayout.ExpandHeight(true));

            if (st.Manager.FoundVariants.Count == 0)
                GUILayout.Label("(none yet — press Generate)");

            foreach (var v in st.Manager.FoundVariants)
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

                GUI.enabled = !st.IsWorking;
                if (GUILayout.Button("Apply this layout", GUILayout.Height(26)))
                {
                    if (win != null && BuildArchitectPlugin.Instance != null)
                    {
                        st.IsWorking = true;
                        st.Status = "Applying " + v.Name + "...";
                        BuildArchitectPlugin.Instance.StartCoroutine(RunApply(win, v, st));
                    }
                }
                GUI.enabled = true;
                GUILayout.EndVertical();
                GUILayout.Space(4);
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            GUILayout.Label(st.Status);
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

        private static HashSet<int> BuildRequiredSet(Dictionary<int, UpgradeMark> marks)
        {
            var set = new HashSet<int>();
            foreach (var kv in marks)
                if (kv.Value == UpgradeMark.Required) set.Add(kv.Key);
            return set;
        }

        private static HashSet<int> BuildExcludedSet(Dictionary<int, UpgradeMark> marks)
        {
            var set = new HashSet<int>();
            foreach (var kv in marks)
                if (kv.Value == UpgradeMark.Excluded) set.Add(kv.Key);
            return set;
        }

        private static IEnumerator RunGenerate(GearDetailsWindow win, GearState st)
        {
            yield return null;
            try
            {
                var required = BuildRequiredSet(st.Marks);
                var excluded = BuildExcludedSet(st.Marks);
                bool done = false;
                var routine = st.Manager.GenerateVariants(
                    win, _includeOuroboros, _ignoreRotationLocks,
                    required, excluded, () => done = true);
                while (!done)
                {
                    object? cur = null;
                    bool has;
                    try { has = routine.MoveNext(); if (has) cur = routine.Current; }
                    catch (System.Exception ex) { BuildArchitectPlugin.Log.LogError("Generate: " + ex); break; }
                    if (!has) break;
                    yield return cur;
                }
                st.Status = $"Done. {st.Manager.FoundVariants.Count} layout(s) generated " +
                            $"({required.Count} required, {excluded.Count} excluded).";
            }
            finally
            {
                st.IsWorking = false;
            }
        }

        private static IEnumerator RunApply(GearDetailsWindow win, LayoutVariant v, GearState st)
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

                st.Status = result
                    ? ("Applied " + v.Name + $" — {v.PlacedCount}/{v.TotalCount} placed.")
                    : "Apply failed.";
            }
            finally
            {
                st.IsWorking = false;
            }
        }
    }
}