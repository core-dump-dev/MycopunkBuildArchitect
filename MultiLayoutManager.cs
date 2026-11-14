using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MycopunkBuildArchitect
{
    public class MultiLayoutManager
    {
        public static readonly string[] Strategies =
        {
            "Large first",
            "Small first",
            "Random",
        };

        public List<LayoutVariant> FoundVariants { get; } = new List<LayoutVariant>();

        public IEnumerator GenerateVariants(object window, object gear, List<object> upgrades)
        {
            FoundVariants.Clear();
            if (window == null || upgrades == null || upgrades.Count == 0)
                yield break;

            foreach (var strategy in Strategies)
            {
                LayoutVariant? result = null;
                var solver = new LayoutSolver(window, gear, upgrades);
                var routine = solver.TrySolve(strategy, r => result = r);
                while (routine.MoveNext())
                    yield return routine.Current;

                if (result != null)
                {
                    result.Name = $"Layout {FoundVariants.Count + 1}";
                    FoundVariants.Add(result);
                    yield return null;
                }
            }

            BuildArchitectPlugin.Log.LogInfo($"Generated {FoundVariants.Count} layout variant(s).");
        }
    }
}