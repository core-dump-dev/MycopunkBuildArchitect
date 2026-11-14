using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace MycopunkBuildArchitect
{
    public class PlacedUpgrade
    {
        public string DisplayName = "";
        public int X;
        public int Y;
        public int Rotation;
    }

    public class LayoutVariant
    {
        public string Name = "Variant";
        public string Strategy = "";
        public int CellCount;
        public List<PlacedUpgrade> Upgrades = new List<PlacedUpgrade>();
        public override string ToString() => $"{Name} [{CellCount} cells]";
    }

    /// <summary>
    /// Скелет решателя.
    /// </summary>
    public class LayoutSolver
    {
        private readonly object _window;
        private readonly object _gear;
        private readonly List<object> _upgrades;

        public LayoutSolver(object window, object gear, List<object> upgrades)
        {
            _window = window;
            _gear = gear;
            _upgrades = upgrades;
        }

        public IEnumerator TrySolve(string strategy, Action<LayoutVariant?> onComplete)
        {
            // =========================================================================
            // TODO: заменить на настоящий backtracking.
            // Сейчас — заглушка, чтобы можно было проверить UI и архитектуру.
            // Шаги для реальной реализации:
            //   1. Достать ModuleEquipSlots из _window.
            //   2. Достать HexMap из _equipSlots.
            //   3. Упорядочить _upgrades согласно strategy.
            //   4. Перебрать позиции и повороты; при успехе вызвать
            //      _equipSlots.EquipModule(upgradeInstance, x, y, rotation).
            //   5. Собрать LayoutVariant из результатов.
            // =========================================================================

            yield return null;

            if (_upgrades == null || _upgrades.Count == 0)
            {
                onComplete(null);
                yield break;
            }

            var fake = new LayoutVariant
            {
                Name = "Variant (" + strategy + ")",
                Strategy = strategy,
                CellCount = _upgrades.Count,
                Upgrades = _upgrades
                    .Select((u, i) => new PlacedUpgrade
                    {
                        DisplayName = u?.ToString() ?? "Unknown",
                        X = i % 4,
                        Y = i / 4,
                        Rotation = 0,
                    })
                    .ToList()
            };

            onComplete(fake);
        }
    }
}