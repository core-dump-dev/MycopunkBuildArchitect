using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MycopunkBuildArchitect
{
    public static class ArchitectUI
    {
        private static bool _visible;
        private static Rect _windowRect = new Rect(30, 120, 440, 520);
        private static Vector2 _scroll;

        private static MultiLayoutManager _manager = new MultiLayoutManager();
        private static bool _isWorking;
        private static string _status = "Open the gear upgrade window, then press Generate.";

        public static void ToggleVisibility() => _visible = !_visible;

        public static void DrawIfVisible()
        {
            if (!_visible) return;
            var plugin = BuildArchitectPlugin.Instance;
            if (plugin == null || !plugin.IsWindowOpen) return;

            _windowRect = GUI.Window(182742, _windowRect, DrawWindow, "BUILD ARCHITECT");
        }

        private static void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            GUILayout.Label("Gear: " + (BuildArchitectPlugin.Instance?.CurrentGear?.ToString() ?? "Unknown"));
            GUILayout.Space(6);

            GUI.enabled = !_isWorking;
            if (GUILayout.Button("Generate layouts", GUILayout.Height(32)))
            {
                var plugin = BuildArchitectPlugin.Instance;
                if (plugin != null && plugin.CurrentUpgradeWindow != null)
                {
                    _isWorking = true;
                    _status = "Searching...";
                    plugin.StartCoroutine(RunGenerate());
                }
            }
            GUI.enabled = true;

            GUILayout.Space(8);
            GUILayout.Label("Variants:");
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(340));

            if (_manager.FoundVariants.Count == 0)
                GUILayout.Label("(none yet — press Generate)");

            foreach (var variant in _manager.FoundVariants)
            {
                GUILayout.BeginVertical("box");
                GUILayout.Label($"{variant.Name} — strategy: {variant.Strategy} — {variant.CellCount} cells");
                foreach (var up in variant.Upgrades)
                    GUILayout.Label("  • " + up.DisplayName);

                if (GUILayout.Button("Apply", GUILayout.Height(26)))
                {
                    // TODO: реальное размещение в игре (пока — просто сообщение)
                    _status = "Apply is not implemented yet (solver is stub).";
                }
                GUILayout.EndVertical();
            }

            GUILayout.EndScrollView();

            GUILayout.Space(4);
            GUILayout.Label(_status);
            if (GUILayout.Button("Close"))
                _visible = false;

            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }

        private static IEnumerator RunGenerate()
        {
            var plugin = BuildArchitectPlugin.Instance;
            if (plugin == null) yield break;

            // TODO: заменить на реальное извлечение списка улучшений из окна
            var upgrades = new List<object> { "Placeholder A", "Placeholder B", "Placeholder C" };

            var routine = _manager.GenerateVariants(plugin.CurrentUpgradeWindow, plugin.CurrentGear, upgrades);
            while (routine.MoveNext())
                yield return routine.Current;

            _status = $"Done. Found {_manager.FoundVariants.Count} variant(s).";
            _isWorking = false;
        }
    }
}