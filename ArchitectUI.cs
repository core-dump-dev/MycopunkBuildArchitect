using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Pigeon.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using Rect = UnityEngine.Rect;

namespace MycopunkBuildArchitect
{
    public static class ArchitectUI
    {
        private enum UpgradeMark { None, Required, Excluded }

        private sealed class GearState
        {
            public List<UpgradeInstance> ScannedCandidates = new();
            public Dictionary<int, UpgradeMark> Marks = new();
            public Dictionary<UpgradeInstance, string> NameCache = new();
            public Dictionary<UpgradeInstance, string> DescCache = new();
            public bool Scanned;
            public Vector2 Scroll;
            public Vector2 ScanScroll;
            public string Status = "Open gear window, then Scan or Generate.";
            public MultiLayoutManager Manager = new();
            public bool IsWorking;
        }

        private static bool _visible;
        private static Rect _windowRect = new(30, 40, 580, 780);
        private static readonly ConditionalWeakTable<IUpgradable, GearState> _gearStates = new();
        private static readonly GearState _fallbackState = new();
        private static bool _ignoreRotationLocks = false;
        private static bool _showScanList = true;
        private static bool _blockClicks = true;
        private static Vector2 _guiMouse;
        private static InputAction? _pointAction;
        private static bool _pointResolved;
        private static Texture2D? _bgTex;
        private static Texture2D? _boxTex;
        private static GUIStyle? _winStyle;
        private static GUIStyle? _boxStyle;
        private static GUIStyle? _labelStyle;
        private static GUIStyle? _wrapStyle;
        private static GUIStyle? _excludedStyle;

        public static void ToggleVisibility() => _visible = !_visible;

        internal static bool BlockGameUiInput
        {
            get
            {
                if (!_visible || !_blockClicks) return false;
                var plugin = BuildArchitectPlugin.Instance;
                if (plugin == null || plugin.CurrentWindow == null) return false;
                if (_windowRect.Contains(_guiMouse)) return true;
                var p = GamePointer();
                if (p.x < 0f && p.y < 0f) return false;
                return _windowRect.Contains(new Vector2(p.x, Screen.height - p.y));
            }
        }

        private static object? ReadMemberChain(Type? rootType, params string[] names)
        {
            object? cur = null;
            Type? t = rootType;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.FlattenHierarchy;
            foreach (var n in names)
            {
                if (t == null) return null;
                var p = t.GetProperty(n, flags);
                if (p != null)
                {
                    cur = p.GetValue(cur);
                }
                else
                {
                    var f = t.GetField(n, flags);
                    if (f != null)
                    {
                        cur = f.GetValue(cur);
                    }
                    else
                    {
                        var nested = t.GetNestedType(n, BindingFlags.Public | BindingFlags.NonPublic);
                        if (nested == null) return null;
                        cur = null;
                        t = nested;
                        continue;
                    }
                }
                if (cur == null) return null;
                t = cur.GetType();
            }
            return cur;
        }

        private static void ResolvePointAction()
        {
            if (_pointResolved) return;
            _pointResolved = true;
            foreach (var tn in new[] { "PlayerInput", "Pigeon.PlayerInput", "Pigeon.UI.PlayerInput", "Pigeon.Movement.PlayerInput" })
            {
                var t = AccessTools.TypeByName(tn);
                if (t == null) continue;
                var act = ReadMemberChain(t, "Controls", "Menu", "Point") as InputAction;
                if (act != null) { _pointAction = act; break; }
            }
            BuildArchitectPlugin.Log.LogInfo("Game UI pointer: " + (_pointAction != null ? _pointAction.name : "not found, using IMGUI mouse"));
        }

        private static Vector2 GamePointer()
        {
            ResolvePointAction();
            if (_pointAction != null)
            {
                try { return _pointAction.ReadValue<Vector2>(); } catch { }
            }
            var m = Mouse.current;
            if (m != null)
            {
                try { return m.position.ReadValue(); } catch { }
            }
            return new Vector2(-1f, -1f);
        }

        private static GearState CurrentState
        {
            get
            {
                var win = BuildArchitectPlugin.Instance?.CurrentWindow;
                IUpgradable? gear = BuildArchitectPlugin.GetGear(win);
                if (gear == null) return _fallbackState;
                return _gearStates.GetValue(gear, _ => new GearState());
            }
        }

        internal static void Dispose()
        {
            if (_bgTex != null) { UnityEngine.Object.Destroy(_bgTex); _bgTex = null; }
            if (_boxTex != null) { UnityEngine.Object.Destroy(_boxTex); _boxTex = null; }
            _winStyle = null; _boxStyle = null; _labelStyle = null; _wrapStyle = null; _excludedStyle = null;
            _pointAction = null; _pointResolved = false;
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
            var plugin = BuildArchitectPlugin.Instance;
            if (!_visible || plugin == null || plugin.CurrentWindow == null) return;

            var ev0 = Event.current;
            if (ev0 != null)
            {
                var t0 = ev0.type;
                if (t0 == EventType.Repaint || t0 == EventType.Layout
                    || t0 == EventType.MouseMove || t0 == EventType.MouseDrag
                    || t0 == EventType.MouseDown || t0 == EventType.MouseUp
                    || t0 == EventType.ScrollWheel)
                {
                    _guiMouse = ev0.mousePosition;
                }
            }

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

            var ev = Event.current;
            if (ev != null)
            {
                var t = ev.type;
                if (_windowRect.Contains(ev.mousePosition)
                    && (t == EventType.MouseDown || t == EventType.MouseUp || t == EventType.MouseDrag
                        || t == EventType.MouseMove || t == EventType.ScrollWheel))
                {
                    ev.Use();
                }
            }
        }

        private static void DrawWindow(int id)
        {
            var st = CurrentState;
            var win = BuildArchitectPlugin.Instance?.CurrentWindow;
            bool isOuro = BuildArchitectPlugin.IsOuroWindow(win);
            GUILayout.BeginVertical();
            string gearName = "?";
            string windowName = "?";
            try
            {
                gearName = BuildArchitectPlugin.GetGear(win)?.Info?.Name ?? "?";
                windowName = win?.GetType().Name ?? "?";
            }
            catch { }
            GUILayout.Label($"<b>Gear: {gearName}</b>  <i>({windowName})</i>");
            GUILayout.Label(isOuro
                ? "<color=cyan><b>Ouroboros window</b> — mission upgrades only.</color>"
                : "<color=#8fd3ff><b>Base window</b> — character upgrades only.</color>");
            GUILayout.Space(4);
            _blockClicks = GUILayout.Toggle(_blockClicks, " Ignore game clicks under this window");
            _ignoreRotationLocks = GUILayout.Toggle(_ignoreRotationLocks, " Ignore rotation level locks (Cheat mode)");
            GUILayout.Space(4);
            if (BuildArchitectPlugin.IsOuroAutoApply(win))
            {
                GUILayout.Label("<color=red>⚠ Ouroboros Auto-Apply is ON — manual placement is disabled by the game.</color>");
                GUILayout.Space(4);
            }
            if (GUILayout.Button(_showScanList ? "▼ Hide available upgrades" : "▶ Show available upgrades", GUILayout.Height(22)))
                _showScanList = !_showScanList;
            GUI.enabled = !st.IsWorking;
            if (GUILayout.Button("1. Scan available upgrades", GUILayout.Height(28)))
            {
                if (win != null)
                {
                    st.ScannedCandidates = ScanCandidates(win);
                    RefreshCaches(st);
                    st.Scanned = true;
                    _showScanList = true;
                    st.Status = $"Scanned {st.ScannedCandidates.Count} upgrade(s).";
                }
            }
            GUI.enabled = true;
            if (st.Scanned && st.ScannedCandidates.Count > 0 && _showScanList)
            {
                string group = isOuro ? "OUROBOROS UPGRADES" : "CHARACTER UPGRADES";
                GUILayout.Label($"<b>{group} ({st.ScannedCandidates.Count})</b> — LMB = ★ required, RMB = ✕ excluded:");
                st.ScanScroll = GUILayout.BeginScrollView(st.ScanScroll, GUILayout.Height(240));
                foreach (var c in st.ScannedCandidates) DrawUpgradeRow(st, c);
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
                        RefreshCaches(st);
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
            GUILayout.Label($"<size=10><color=#999999>click guard: {(BlockGameUiInput ? "ACTIVE" : "idle")}</color></size>");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Dump input info to log", GUILayout.Width(190))) DumpInputInfo();
            if (GUILayout.Button("Close", GUILayout.Width(75))) _visible = false;
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }

        private static void DrawUpgradeRow(GearState st, UpgradeInstance c)
        {
            int sid = LayoutSolver.GetSourceId(c);
            if (!st.Marks.TryGetValue(sid, out var mark)) mark = UpgradeMark.None;
            var currentMark = mark;
            string name = st.NameCache.TryGetValue(c, out var n) ? n : LayoutSolver.SafeName(c);
            string desc = st.DescCache.TryGetValue(c, out var d) ? d : LayoutSolver.SafeDescription(c);
            string label = desc.Length == 0 ? name : name + "\n    " + desc;
            string toggleLabel = currentMark == UpgradeMark.Required ? " ★ "
                               : currentMark == UpgradeMark.Excluded ? " ✕ "
                               : " ☐ ";
            GUILayout.BeginHorizontal();
            Rect btnRect = GUILayoutUtility.GetRect(
                new GUIContent(toggleLabel), GUI.skin.button, GUILayout.Width(30));
            var ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 1 && btnRect.Contains(ev.mousePosition))
            {
                currentMark = currentMark == UpgradeMark.Excluded ? UpgradeMark.None : UpgradeMark.Excluded;
                st.Marks[sid] = currentMark;
                ev.Use();
            }
            toggleLabel = currentMark == UpgradeMark.Required ? " ★ "
                        : currentMark == UpgradeMark.Excluded ? " ✕ "
                        : " ☐ ";
            bool lmb = GUI.Button(btnRect, toggleLabel);
            if (lmb && ev.button == 0)
            {
                currentMark = currentMark == UpgradeMark.Required ? UpgradeMark.None : UpgradeMark.Required;
                st.Marks[sid] = currentMark;
            }
            GUILayout.Label(label, currentMark == UpgradeMark.Excluded ? _excludedStyle! : _wrapStyle!);
            GUILayout.EndHorizontal();
            GUILayout.Space(2);
        }

        internal static void DumpInputInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Build Architect input dump ===");
            sb.AppendLine($"visible={_visible} blockClicks={_blockClicks} blocking={BlockGameUiInput}");
            sb.AppendLine($"Screen={Screen.width}x{Screen.height} window={_windowRect}");
            sb.AppendLine($"guiMouse={_guiMouse} gamePoint={GamePointer()} pointAction={(_pointAction != null ? _pointAction.name : "null")}");
            var m = Mouse.current;
            sb.AppendLine(m == null ? "Mouse.current = null" : $"Mouse raw={m.position.ReadValue()}");
            try
            {
                var t = AccessTools.TypeByName("UIRaycaster")
                        ?? AccessTools.TypeByName("Pigeon.UI.UIRaycaster")
                        ?? AccessTools.TypeByName("Pigeon.UIRaycaster");
                if (t == null) sb.AppendLine("UIRaycaster: NOT FOUND");
                else
                {
                    sb.AppendLine("UIRaycaster: " + t.FullName);
                    foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                    {
                        if (!mi.Name.StartsWith("Raycast", StringComparison.Ordinal)) continue;
                        var ps = mi.GetParameters();
                        var sig = new StringBuilder();
                        foreach (var p in ps)
                        {
                            if (sig.Length > 0) sig.Append(", ");
                            sig.Append(p.ParameterType.Name);
                        }
                        sb.AppendLine($"  {mi.Name}({sig}) -> {mi.ReturnType.Name} generic={mi.IsGenericMethodDefinition}");
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("UIRaycaster dump error: " + ex.Message); }
            BuildArchitectPlugin.Log.LogInfo(sb.ToString());
        }

        private static void RefreshCaches(GearState st)
        {
            st.NameCache.Clear();
            st.DescCache.Clear();
            foreach (var c in st.ScannedCandidates)
            {
                int sid = LayoutSolver.GetSourceId(c);
                if (!st.Marks.ContainsKey(sid)) st.Marks[sid] = UpgradeMark.None;
                st.NameCache[c] = LayoutSolver.SafeName(c);
                st.DescCache[c] = LayoutSolver.SafeDescription(c);
            }
        }

        private static List<UpgradeInstance> ScanCandidates(Window win)
        {
            try
            {
                var gear = BuildArchitectPlugin.GetGear(win);
                var slots = BuildArchitectPlugin.GetEquipSlots(win);
                if (gear == null || slots == null)
                {
                    BuildArchitectPlugin.Log.LogWarning("ScanCandidates: gear or slots unavailable.");
                    return new List<UpgradeInstance>();
                }
                bool ouroMode = BuildArchitectPlugin.IsOuroWindow(win);
                var solver = new LayoutSolver(gear, slots, () => { }, _ignoreRotationLocks, ouroMode);
                var all = solver.CollectCandidates();
                return solver.PickForLayout(all);
            }
            catch (Exception ex)
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

        private static IEnumerator RunGenerate(Window win, GearState st)
        {
            yield return null;
            try
            {
                var gear = BuildArchitectPlugin.GetGear(win);
                var slots = BuildArchitectPlugin.GetEquipSlots(win);
                if (gear == null || slots == null)
                {
                    st.Status = "Cannot generate: gear or equip slots unavailable.";
                    yield break;
                }
                bool ouroMode = BuildArchitectPlugin.IsOuroWindow(win);
                var required = BuildRequiredSet(st.Marks);
                var excluded = BuildExcludedSet(st.Marks);
                bool done = false;
                var routine = st.Manager.GenerateVariants(
                    gear, slots,
                    () => BuildArchitectPlugin.NotifyUpgradesChanged(win),
                    _ignoreRotationLocks, ouroMode,
                    required, excluded, () => done = true);
                while (!done)
                {
                    object? cur = null;
                    bool has;
                    try { has = routine.MoveNext(); if (has) cur = routine.Current; }
                    catch (Exception ex) { BuildArchitectPlugin.Log.LogError("Generate: " + ex); break; }
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

        private static IEnumerator RunApply(Window win, LayoutVariant v, GearState st)
        {
            yield return null;
            try
            {
                var gear = BuildArchitectPlugin.GetGear(win);
                var slots = BuildArchitectPlugin.GetEquipSlots(win);
                if (gear == null || slots == null)
                {
                    st.Status = "Cannot apply: gear or equip slots unavailable.";
                    yield break;
                }
                bool ouroMode = BuildArchitectPlugin.IsOuroWindow(win);
                bool result = false;
                IEnumerator? routine = null;
                try
                {
                    var solver = new LayoutSolver(gear, slots,
                        () => BuildArchitectPlugin.NotifyUpgradesChanged(win),
                        _ignoreRotationLocks, ouroMode);
                    routine = solver.Apply(v, r => result = r);
                }
                catch (Exception ex) { BuildArchitectPlugin.Log.LogError("Apply init: " + ex); }
                if (routine != null)
                {
                    while (true)
                    {
                        object? cur = null;
                        bool has;
                        try { has = routine.MoveNext(); if (has) cur = routine.Current; }
                        catch (Exception ex) { BuildArchitectPlugin.Log.LogError("Apply step: " + ex); break; }
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