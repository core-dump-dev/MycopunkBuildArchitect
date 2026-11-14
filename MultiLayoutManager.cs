using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MycopunkBuildArchitect
{
    /// <summary>
    /// Оркестратор: запускает LayoutSolver с разными стратегиями и собирает варианты.
    /// </summary>
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
                    yield return null; // пауза между стратегиями
                }
            }

            BuildArchitectPlugin.Log.LogInfo($"Generated {FoundVariants.Count} layout variant(s).");
        }
    }
}