using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MycopunkBuildArchitect
{
    public class PlacedUpgrade
    {
        public UpgradeInstance Instance = null!;
        public string DisplayName = "";
        public int Rotation;
        public int OffsetX;
        public int OffsetY;
    }

    public class LayoutVariant
    {
        public string Name = "Layout";
        public string Strategy = "";
        public int CellCount;
        public int PlacedCount;
        public int TotalCount;
        public List<PlacedUpgrade> Upgrades = new();
        public List<string> Missing = new();
    }

    public class LayoutSolver
    {
        private class SimulatedGrid
        {
            public int Width;
            public int Height;
            public UpgradeInstance?[,] Occupied;

            public SimulatedGrid(int w, int h)
            {
                Width = w;
                Height = h;
                Occupied = new UpgradeInstance[w, h];
            }
        }

        private static readonly FieldInfo EquipSlotsField =
            typeof(GearDetailsWindow).GetField("equipSlots", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly GearDetailsWindow _window;
        private readonly ModuleEquipSlots _equipSlots;
        private readonly HexMap _hexMap;
        private readonly IUpgradable _gear;
        private readonly IUpgradable? _global;
        
        private readonly bool _includeOuroboros;
        private readonly bool _ignoreRotationLocks;

        private SimulatedGrid _simGrid = null!;
        private List<PlacedUpgrade> _bestPlacement = new();
        private List<PlacedUpgrade> _currentPlacement = new();
        private int _maxPlaced;
        private int _nodeCount;
        private HashSet<UpgradeInstance> _placedInstances = new();

        public LayoutSolver(GearDetailsWindow window, bool includeOuroboros, bool ignoreRotationLocks)
        {
            _window = window;
            _equipSlots = (ModuleEquipSlots)EquipSlotsField.GetValue(window);
            _hexMap = _equipSlots.HexMap;
            _gear = window.UpgradablePrefab;
            _includeOuroboros = includeOuroboros;
            _ignoreRotationLocks = ignoreRotationLocks;
            try { _global = Global.Instance; } catch { _global = null; }
        }

        public List<UpgradeInstance> CollectCandidates()
        {
            var result = new List<UpgradeInstance>();
            CollectFrom(_gear, result);
            if (_includeOuroboros && _gear is Character && _global != null && PlayerData.HasUpgrades(_global))
                CollectFrom(_global, result);
            return result;
        }

        private void CollectFrom(IUpgradable source, List<UpgradeInstance> list)
        {
            try
            {
                var e = new PlayerData.UpgradeEnumerator(source);
                while (e.MoveNext())
                {
                    var inst = e.Upgrade;
                    if (inst == null) continue;
                    if (inst.RemoveAfterMission) continue;
                    var upg = inst.Upgrade;
                    if (upg == null) continue;
                    if ((upg.Flags & Upgrade.UpgradeFlags.OnlyInOuroboros) != 0) continue;
                    if (upg.Rarity == Rarity.Contraband) continue;
                    if (!upg.Obtainable() && !upg.AllowExistingUnobtainableUpgrade()) continue;
                    HexMap p;
                    try { p = inst.GetPattern(); } catch { continue; }
                    if (p == null || (p.Width == 0 && p.Height == 0)) continue;
                    list.Add(inst);
                }
            }
            catch (Exception ex) { BuildArchitectPlugin.Log.LogWarning("Collect failed: " + ex.Message); }
        }

        public List<UpgradeInstance> PickForLayout(List<UpgradeInstance> all)
        {
            var groups = new Dictionary<string, List<UpgradeInstance>>();
            foreach (var u in all)
            {
                string key;
                try { key = u.SourceUpgradeID.ID.ToString(); }
                catch { key = u.Upgrade.Name; }
                if (!groups.TryGetValue(key, out var lst)) { lst = new(); groups[key] = lst; }
                lst.Add(u);
            }
            var result = new List<UpgradeInstance>();
            foreach (var kv in groups)
            {
                var lst = kv.Value;
                bool stacks = lst.Any(x => { try { return x.CanStack; } catch { return false; } });
                if (stacks) result.AddRange(lst);
                else
                {
                    try
                    {
                        var best = lst
                            .OrderByDescending(x => { try { return x.IsTurbocharged ? 1 : 0; } catch { return 0; } })
                            .ThenByDescending(x => { try { return (int)x.Upgrade.Rarity; } catch { return 0; } })
                            .ThenByDescending(x => x.Seed)
                            .First();
                        result.Add(best);
                    }
                    catch { result.Add(lst[0]); }
                }
            }
            return result;
        }

        public IEnumerator Solve(string strategy, List<UpgradeInstance> candidates, Action<LayoutVariant> done)
        {
            UnequipAll();
            yield return null;

            var ordered = SortCandidates(candidates, strategy);
            
            // Используем реальные размеры сетки + небольшой запас для потенциальных расширителей
            int simW = Math.Max(_hexMap.Width + 2, 10);
            int simH = Math.Max(_hexMap.Height + 2, 10);
            
            _simGrid = new SimulatedGrid(simW, simH);
            _placedInstances.Clear();
            _currentPlacement.Clear();
            _bestPlacement.Clear();
            _maxPlaced = 0;
            _nodeCount = 0;

            Search(0, ordered);

            var placed = new List<PlacedUpgrade>();
            var missing = new List<string>();

            foreach (var p in _bestPlacement) {
                p.DisplayName = SafeName(p.Instance);
                placed.Add(p);
            }

            var placedSet = new HashSet<UpgradeInstance>(_bestPlacement.Select(x => x.Instance));

            foreach (var u in ordered) {
                if (!placedSet.Contains(u)) {
                    bool canRotate = _ignoreRotationLocks;
                    if (!canRotate) {
                        try { canRotate = PlayerData.Instance.CanRotateUpgrade(u.Upgrade.Rarity); } catch { canRotate = false; }
                    }
                    
                    HexMap pattern = null;
                    try { pattern = u.GetPattern(); } catch { }
                    bool wouldFitRotated = false;
                    if (pattern != null && !canRotate) {
                        for (int r = 1; r < 6 && !wouldFitRotated; r++)
                            if (WouldFitWithRotation(pattern, r)) wouldFitRotated = true;
                    }
                    missing.Add(SafeName(u) + (wouldFitRotated ? " (needs rotation unlock)" : " (no fit)"));
                }
            }

            int cellCount = 0;
            try { cellCount = _hexMap.GetCellCount(); } catch { }

            var variant = new LayoutVariant {
                Strategy = strategy,
                CellCount = cellCount,
                PlacedCount = placed.Count,
                TotalCount = ordered.Count,
                Upgrades = placed,
                Missing = missing,
            };

            UnequipAll();
            yield return null;
            done(variant);
        }

        private void Search(int index, List<UpgradeInstance> items)
        {
            if (_nodeCount++ > 100000) return; // Защита от зависания

            if (index >= items.Count)
            {
                if (_currentPlacement.Count > _maxPlaced)
                {
                    _maxPlaced = _currentPlacement.Count;
                    _bestPlacement.Clear();
                    _bestPlacement.AddRange(_currentPlacement);
                }
                return;
            }

            if (_currentPlacement.Count + (items.Count - index) <= _maxPlaced)
                return;

            var upg = items[index];

            HexMap pattern = null;
            try { pattern = upg.GetPattern(); } catch { }
            
            if (pattern != null)
            {
                bool canRotate = _ignoreRotationLocks;
                if (!canRotate) {
                    try { canRotate = PlayerData.Instance.CanRotateUpgrade(upg.Upgrade.Rarity); } catch { }
                }
                int maxRot = canRotate ? 6 : 1;

                var validPlacements = new List<PlacedUpgrade>();

                for (int rot = 0; rot < maxRot; rot++)
                {
                    HexMap rotPattern;
                    try { rotPattern = pattern.GetModifiedMap(rot); } catch { continue; }
                    if (rotPattern == null) continue;

                    for (int y = 0; y < _simGrid.Height; y++)
                    {
                        for (int x = 0; x < _simGrid.Width; x++)
                        {
                            int centerX = rotPattern.Width / 2;
                            int offsetX = x - centerX;
                            int sumY = 0, count = 0;
                            for (int i = 0; i < rotPattern.Height; i++)
                                if (rotPattern[centerX, i].enabled) { sumY += i; count++; }
                            int offsetY = count == 0 ? (y - rotPattern.Height / 2) : (y - sumY / count);
                            if (centerX % 2 == 1) offsetY--;

                            if (!CanFitSimulated(rotPattern, offsetX, offsetY, upg)) continue;

                            validPlacements.Add(new PlacedUpgrade {
                                Instance = upg,
                                Rotation = rot,
                                OffsetX = offsetX,
                                OffsetY = offsetY
                            });
                        }
                    }
                }

                foreach (var p in validPlacements)
                {
                    PlaceSimulated(p);
                    _currentPlacement.Add(p);
                    
                    Search(index + 1, items);
                    
                    _currentPlacement.RemoveAt(_currentPlacement.Count - 1);
                    RemoveSimulated(p);

                    if (_maxPlaced == items.Count) return;
                }
            }

            Search(index + 1, items);
        }

        private bool CanFitSimulated(HexMap pattern, int offsetX, int offsetY, UpgradeInstance upg)
        {
            int centerX = offsetX + pattern.Width / 2;
            var e = pattern.GetActiveCells();
            while (e.MoveNext())
            {
                int x = e.X + offsetX;
                int y = e.Y + offsetY;
                if (e.X % 2 == 1 && x % 2 == 0) y++;
                if (centerX % 2 == 1) y++;
                if (centerX % 2 == 1 && pattern.Width / 2 % 2 == 0) y--;
                
                if (x < 0 || y < 0 || x >= _simGrid.Width || y >= _simGrid.Height) return false;
                if (_simGrid.Occupied[x, y] != null) return false;
            }

            foreach (var other in _placedInstances)
            {
                try { if (!Upgrade.CanEquipTogether(upg, other)) return false; } catch { }
            }

            return true;
        }

        private void PlaceSimulated(PlacedUpgrade p)
        {
            var pattern = p.Instance.GetPattern().GetModifiedMap(p.Rotation);
            int centerX = p.OffsetX + pattern.Width / 2;
            var e = pattern.GetActiveCells();
            while (e.MoveNext())
            {
                int x = e.X + p.OffsetX;
                int y = e.Y + p.OffsetY;
                if (e.X % 2 == 1 && x % 2 == 0) y++;
                if (centerX % 2 == 1) y++;
                if (centerX % 2 == 1 && pattern.Width / 2 % 2 == 0) y--;
                _simGrid.Occupied[x, y] = p.Instance;
            }
            _placedInstances.Add(p.Instance);
        }

        private void RemoveSimulated(PlacedUpgrade p)
        {
            var pattern = p.Instance.GetPattern().GetModifiedMap(p.Rotation);
            int centerX = p.OffsetX + pattern.Width / 2;
            var e = pattern.GetActiveCells();
            while (e.MoveNext())
            {
                int x = e.X + p.OffsetX;
                int y = e.Y + p.OffsetY;
                if (e.X % 2 == 1 && x % 2 == 0) y++;
                if (centerX % 2 == 1) y++;
                if (centerX % 2 == 1 && pattern.Width / 2 % 2 == 0) y--;
                _simGrid.Occupied[x, y] = null;
            }
            _placedInstances.Remove(p.Instance);
        }

        private bool WouldFitWithRotation(HexMap pattern, int rotation)
        {
            HexMap rotPattern;
            try { rotPattern = pattern.GetModifiedMap(rotation); } catch { return false; }
            if (rotPattern == null) return false;

            for (int y = 0; y < _simGrid.Height; y++)
            {
                for (int x = 0; x < _simGrid.Width; x++)
                {
                    int centerX = rotPattern.Width / 2;
                    int offsetX = x - centerX;
                    int sumY = 0, count = 0;
                    for (int i = 0; i < rotPattern.Height; i++)
                        if (rotPattern[centerX, i].enabled) { sumY += i; count++; }
                    int offsetY = count == 0 ? (y - rotPattern.Height / 2) : (y - sumY / count);
                    if (centerX % 2 == 1) offsetY--;

                    if (CanFitSimulatedDummy(rotPattern, offsetX, offsetY)) return true;
                }
            }
            return false;
        }

        private bool CanFitSimulatedDummy(HexMap pattern, int offsetX, int offsetY)
        {
            int centerX = offsetX + pattern.Width / 2;
            var e = pattern.GetActiveCells();
            while (e.MoveNext())
            {
                int x = e.X + offsetX;
                int y = e.Y + offsetY;
                if (e.X % 2 == 1 && x % 2 == 0) y++;
                if (centerX % 2 == 1) y++;
                if (centerX % 2 == 1 && pattern.Width / 2 % 2 == 0) y--;
                
                if (x < 0 || y < 0 || x >= _simGrid.Width || y >= _simGrid.Height) return false;
                if (_simGrid.Occupied[x, y] != null) return false;
            }
            return true;
        }

        public IEnumerator Apply(LayoutVariant variant, Action<bool> done)
        {
            UnequipAll();
            yield return null;

            int ok = 0, fail = 0;
            var actuallyApplied = new List<PlacedUpgrade>();

            foreach (var p in variant.Upgrades)
            {
                try
                {
                    bool r = _equipSlots.EquipModule(_gear, p.Instance, p.OffsetX, p.OffsetY, (byte)p.Rotation, sort: false, equippedManually: true);
                    if (r) 
                    { 
                        ok++; 
                        actuallyApplied.Add(p); 
                    } 
                    else 
                    { 
                        fail++; 
                        BuildArchitectPlugin.Log.LogWarning($"Failed to place {p.DisplayName} at {p.OffsetX},{p.OffsetY} rot {p.Rotation}");
                    }
                }
                catch (Exception ex) { fail++; BuildArchitectPlugin.Log.LogWarning("Apply failed: " + ex.Message); }
                yield return null;
            }

            // Принудительно обновляем сетку, чтобы UI точно отобразил актуальное состояние
            try 
            { 
                _equipSlots.Setup(_gear, false, _window);
                _window.OnUpgradesChanged(); 
            } 
            catch { }

            // Обновляем данные варианта, чтобы UI показал реальное количество
            variant.Upgrades = actuallyApplied;
            variant.PlacedCount = ok;
            
            BuildArchitectPlugin.Log.LogInfo($"Apply: {ok} placed, {fail} failed");
            done(ok > 0);
        }

        private void UnequipAll()
        {
            // Более надежный метод снятия всего
            try
            {
                var e = new PlayerData.EquippedUpgradeEnumerator(_gear);
                while (e.MoveNext())
                {
                    try { e.Upgrade.Unequip(_gear); } catch { }
                }
            } catch { }
            try { _equipSlots.Clear(); } catch { }
        }

        private static List<UpgradeInstance> SortCandidates(List<UpgradeInstance> list, string strategy)
        {
            switch (strategy)
            {
                case "Large first": return list.OrderByDescending(SafePatternCount).ToList();
                case "Small first": return list.OrderBy(SafePatternCount).ToList();
                case "Rarity first": return list.OrderByDescending(u => { try { return (int)u.Upgrade.Rarity; } catch { return 0; } }).ThenByDescending(SafePatternCount).ToList();
                case "Expanders first": 
                    return list.OrderByDescending(u => { 
                        try { return u.Upgrade.Name.ToLower().Contains("expand") ? 1 : 0; } catch { return 0; } 
                    }).ThenByDescending(SafePatternCount).ToList();
                case "Random": var rng = new System.Random(12345); return list.OrderBy(_ => rng.Next()).ToList();
                default: return list;
            }
        }

        private static int SafePatternCount(UpgradeInstance u)
        {
            try { return u.GetPattern()?.GetCellCount() ?? 1; } catch { return 1; }
        }

        private static string SafeName(UpgradeInstance u)
        {
            try
            {
                var n = u.Upgrade.GetInstanceName(u);
                if (!string.IsNullOrEmpty(n)) return StripTags(n);
            }
            catch { }
            try { return u.Upgrade.Name; } catch { }
            return "Unknown";
        }

        private static string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            bool inTag = false;
            foreach (char c in s)
            {
                if (c == '<') inTag = true;
                else if (c == '>') inTag = false;
                else if (!inTag) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}