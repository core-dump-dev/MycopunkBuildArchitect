using System;
using System.Collections;
using System.Collections.Generic;

namespace MycopunkBuildArchitect
{
    public class MultiLayoutManager
    {
        public static readonly string[] Strategies = { "Expanders first", "Large first", "Small first", "Rarity first", "Random" };
        public List<LayoutVariant> FoundVariants { get; } = new();

        public IEnumerator GenerateVariants(GearDetailsWindow window, bool includeOuroboros, bool ignoreRotationLocks, HashSet<int> requiredIds, Action onDone)
        {
            FoundVariants.Clear();
            LayoutSolver solver;
            try { solver = new LayoutSolver(window, includeOuroboros, ignoreRotationLocks, requiredIds); }
            catch (Exception ex) { BuildArchitectPlugin.Log.LogError("Solver init failed: " + ex); onDone(); yield break; }

            var candidates = solver.CollectCandidates();
            var picked = solver.PickForLayout(candidates);
            if (picked.Count == 0)
            {
                BuildArchitectPlugin.Log.LogWarning("No candidate upgrades found.");
                onDone(); yield break;
            }

            foreach (var strategy in Strategies)
            {
                LayoutVariant? result = null;
                IEnumerator routine;
                try { routine = solver.Solve(strategy, picked, v => result = v); }
                catch (Exception ex) { BuildArchitectPlugin.Log.LogError("Solve " + strategy + ": " + ex); continue; }

                while (true)
                {
                    object? current = null;
                    bool has;
                    try { has = routine.MoveNext(); if (has) current = routine.Current; }
                    catch (Exception ex) { BuildArchitectPlugin.Log.LogError("Solve step: " + ex); break; }
                    if (!has) break;
                    yield return current;
                }

                if (result != null)
                {
                    result.Name = $"Layout {FoundVariants.Count + 1}";
                    FoundVariants.Add(result);
                }
                yield return null;
            }

            BuildArchitectPlugin.Log.LogInfo($"Generated {FoundVariants.Count} layout(s).");
            onDone();
        }
    }
}