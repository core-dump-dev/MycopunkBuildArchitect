using System.Collections;
using UnityEngine;

namespace MycopunkBuildArchitect
{
    public static class ArchitectUI
    {
        private static bool _visible;
        private static Rect _windowRect = new(30, 120, 500, 640);
        private static Vector2 _scroll;
        private static readonly MultiLayoutManager _manager = new();
        private static bool _isWorking;
        private static string _status = "Open gear window, then press Generate.";

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
            GUILayout.Space(6);

            GUI.enabled = !_isWorking;
            if (GUILayout.Button("Generate layouts", GUILayout.Height(32)))
            {
                if (win != null && BuildArchitectPlugin.Instance != null)
                {
                    _isWorking = true;
                    _status = "Searching...";
                    BuildArchitectPlugin.Instance.StartCoroutine(RunGenerate(win));
                }
            }
            GUI.enabled = true;

            GUILayout.Space(8);
            GUILayout.Label($"Variants ({_manager.FoundVariants.Count}):");
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(480));

            if (_manager.FoundVariants.Count == 0)
                GUILayout.Label("(none yet - press Generate)");

            foreach (var v in _manager.FoundVariants)
            {
                GUILayout.BeginVertical("box");
                GUILayout.Label($"{v.Name}  [{v.Strategy}]  {v.PlacedCount}/{v.TotalCount} upgrades, {v.CellCount} cells");
                foreach (var p in v.Upgrades)
                    GUILayout.Label("  • " + p.DisplayName);
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

        private static IEnumerator RunGenerate(GearDetailsWindow win)
        {
            yield return null;
            bool done = false;
            var routine = _manager.GenerateVariants(win, () => done = true);
            while (!done)
            {
                object? cur = null;
                bool has;
                try
                {
                    has = routine.MoveNext();
                    if (has) cur = routine.Current;
                }
                catch (System.Exception ex)
                {
                    BuildArchitectPlugin.Log.LogError("Generate: " + ex);
                    break;
                }
                if (!has) break;
                yield return cur;
            }
            _status = $"Done. {_manager.FoundVariants.Count} layout(s) generated.";
            _isWorking = false;
        }

        private static IEnumerator RunApply(GearDetailsWindow win, LayoutVariant v)
        {
            yield return null;

            bool result = false;
            IEnumerator? routine = null;

            // try только на создание solver/routine (без yield)
            try
            {
                var solver = new LayoutSolver(win);
                routine = solver.Apply(v, r => result = r);
            }
            catch (System.Exception ex)
            {
                BuildArchitectPlugin.Log.LogError("Apply init: " + ex);
            }

            // итерация — вне try, чтобы можно было yield
            if (routine != null)
            {
                while (true)
                {
                    object? cur = null;
                    bool has;
                    try
                    {
                        has = routine.MoveNext();
                        if (has) cur = routine.Current;
                    }
                    catch (System.Exception ex)
                    {
                        BuildArchitectPlugin.Log.LogError("Apply step: " + ex);
                        break;
                    }
                    if (!has) break;
                    yield return cur;
                }
            }

            _status = result ? ("Applied " + v.Name) : "Apply failed.";
            _isWorking = false;
        }
    }
}