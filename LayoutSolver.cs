using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace MycopunkBuildArchitect
{
    public class PlacedUpgrade
    {
        public UpgradeInstance Instance = null!;
        public string DisplayName = "";
        public string Description = "";
        public int Rotation;
        public int OffsetX;
        public int OffsetY;
    }

    public class LayoutVariant
    {
        public string Name = "Layout";
        public string Strategy = "";
        public int FreeCells;
        public int PlacedCount;
        public int TotalCount;
        public int RequiredMissing;
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
            public SimulatedGrid(int w, int h) { Width = w; Height = h; Occupied = new UpgradeInstance[w, h]; }
        }

        private readonly IUpgradable _gear;
        private readonly ModuleEquipSlots _equipSlots;
        private readonly HexMap _hexMap;
        private readonly IUpgradable? _global;
        private readonly Action _onUpgradesChanged;
        private readonly bool _includeOuroboros;
        private readonly bool _ignoreRotationLocks;
        private readonly bool _ouroMode;
        private readonly HashSet<int> _requiredIds;
        private readonly HashSet<int> _excludedIds;

        private SimulatedGrid _simGrid = null!;
        private List<PlacedUpgrade> _bestPlacement = new();
        private List<PlacedUpgrade> _currentPlacement = new();
        private int _maxPlaced;
        private int _maxReq;
        private int _currentReq;
        private int _nodeCount;
        private HashSet<UpgradeInstance> _placedInstances = new();

        // ---------- caches (per solver instance, reused across strategies) ----------
        private readonly Dictionary<UpgradeInstance, int> _sourceIdCache = new();
        private readonly Dictionary<UpgradeInstance, bool> _requiredCache = new();
        private int[] _requiredSuffix = Array.Empty<int>();

        public LayoutSolver(IUpgradable gear, ModuleEquipSlots equipSlots, Action onUpgradesChanged,
                            bool includeOuroboros, bool ignoreRotationLocks, bool ouroMode,
                            HashSet<int>? requiredIds = null, HashSet<int>? excludedIds = null)
        {
            _gear = gear;
            _equipSlots = equipSlots;
            _hexMap = equipSlots.HexMap;
            _onUpgradesChanged = onUpgradesChanged ?? (() => { });
            _includeOuroboros = includeOuroboros;
            _ignoreRotationLocks = ignoreRotationLocks;
            _ouroMode = ouroMode;
            _requiredIds = requiredIds ?? new HashSet<int>();
            _excludedIds = excludedIds ?? new HashSet<int>();
            try { _global = Global.Instance; } catch { _global = null; }
        }

        private int GetSourceIdCached(UpgradeInstance u)
        {
            if (_sourceIdCache.TryGetValue(u, out int id)) return id;
            id = GetSourceId(u);
            _sourceIdCache[u] = id;
            return id;
        }

        private bool IsRequired(UpgradeInstance u)
        {
            if (_requiredCache.TryGetValue(u, out bool req)) return req;
            req = _requiredIds.Contains(GetSourceIdCached(u));
            _requiredCache[u] = req;
            return req;
        }

        public List<UpgradeInstance> CollectCandidates()
        {
            var result = new List<UpgradeInstance>();
            CollectFrom(_gear, result);

            bool wantGlobal = _gear is Character && _global != null
                              && (_ouroMode || _includeOuroboros)
                              && PlayerData.HasUpgrades(_global);
            if (wantGlobal)
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

                    if (_ouroMode)
                    {
                        if (!inst.RemoveAfterMission) continue;
                    }
                    else
                    {
                        if (inst.RemoveAfterMission) continue;
                    }

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
                try { key = GetSourceIdCached(u).ToString(); }
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
            // NOTE: no UnequipAll here. Simulation starts from an empty grid anyway,
            // and modifying the real game state 5 times (once per strategy) is what was causing hitches.
            yield return null;

            List<UpgradeInstance> filtered;
            if (_excludedIds.Count > 0)
            {
                filtered = new List<UpgradeInstance>(candidates.Count);
                foreach (var u in candidates)
                    if (!_excludedIds.Contains(GetSourceIdCached(u)))
                        filtered.Add(u);
            }
            else
            {
                filtered = candidates;
            }

            var ordered = SortCandidates(filtered, strategy, _requiredIds, this);

            _simGrid = new SimulatedGrid(_hexMap.Width, _hexMap.Height);
            _placedInstances.Clear();
            _currentPlacement.Clear();
            _bestPlacement.Clear();
            _maxPlaced = 0;
            _maxReq = 0;
            _currentReq = 0;
            _nodeCount = 0;

            // Precompute required-suffix once: O(N) → O(1) bound check per node.
            _requiredSuffix = new int[ordered.Count + 1];
            for (int i = ordered.Count - 1; i >= 0; i--)
                _requiredSuffix[i] = _requiredSuffix[i + 1] + (IsRequired(ordered[i]) ? 1 : 0);

            Search(0, ordered);

            var placed = new List<PlacedUpgrade>();
            var missing = new List<string>();

            foreach (var p in _bestPlacement)
            {
                p.DisplayName = SafeName(p.Instance);
                p.Description = SafeDescription(p.Instance);
                placed.Add(p);
            }

            var placedSet = new HashSet<UpgradeInstance>(_bestPlacement.Select(x => x.Instance));
            int requiredMissing = 0;

            foreach (var u in ordered)
            {
                if (placedSet.Contains(u)) continue;
                bool isReq = IsRequired(u);
                if (isReq) requiredMissing++;

                bool canRotate = _ignoreRotationLocks;
                if (!canRotate) { try { canRotate = PlayerData.Instance.CanRotateUpgrade(u.Upgrade.Rarity); } catch { } }

                HexMap pattern = null;
                try { pattern = u.GetPattern(); } catch { }
                bool wouldFitRotated = false;
                if (pattern != null && !canRotate)
                {
                    for (int r = 1; r < 6 && !wouldFitRotated; r++)
                        if (WouldFitWithRotation(pattern, r)) wouldFitRotated = true;
                }

                string reason = wouldFitRotated ? " (needs rotation unlock)" : " (no fit)";
                missing.Add((isReq ? "★ REQUIRED: " : "") + SafeName(u) + reason);
            }

            int totalGridCells = 0;
            var cellIter = _hexMap.GetActiveCells();
            while (cellIter.MoveNext()) totalGridCells++;

            int usedCells = 0;
            foreach (var p in placed)
            {
                try { usedCells += p.Instance.GetPattern().GetModifiedMap(p.Rotation).GetCellCount(); } catch { }
            }
            int freeCells = Math.Max(0, totalGridCells - usedCells);

            var variant = new LayoutVariant
            {
                Strategy = strategy,
                FreeCells = freeCells,
                PlacedCount = placed.Count,
                TotalCount = ordered.Count,
                RequiredMissing = requiredMissing,
                Upgrades = placed,
                Missing = missing,
            };

            done(variant);
        }

        private void Search(int index, List<UpgradeInstance> items)
        {
            if (_nodeCount++ > 500000) return;

            if (index >= items.Count)
            {
                if (_currentReq > _maxReq || (_currentReq == _maxReq && _currentPlacement.Count > _maxPlaced))
                {
                    _maxReq = _currentReq;
                    _maxPlaced = _currentPlacement.Count;
                    _bestPlacement.Clear();
                    _bestPlacement.AddRange(_currentPlacement);
                }
                return;
            }

            // O(1) bound check using precomputed suffix sums.
            int maxPossibleReq = _currentReq + _requiredSuffix[index];
            int maxPossibleTotal = _currentPlacement.Count + (items.Count - index);
            if (maxPossibleReq < _maxReq) return;
            if (maxPossibleReq == _maxReq && maxPossibleTotal <= _maxPlaced) return;

            var upg = items[index];
            HexMap pattern = null;
            try { pattern = upg.GetPattern(); } catch { }

            if (pattern != null)
            {
                bool canRotate = _ignoreRotationLocks;
                if (!canRotate) { try { canRotate = PlayerData.Instance.CanRotateUpgrade(upg.Upgrade.Rarity); } catch { } }
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

                            validPlacements.Add(new PlacedUpgrade
                            {
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
                    if (_maxReq == _requiredIds.Count && _maxPlaced == items.Count) return;
                }
            }

            Search(index + 1, items);
        }

        private static void GetWorldCoords(
            int cellX, int cellY, int offsetX, int offsetY,
            int patternWidth, int centerX,
            out int x, out int y)
        {
            x = cellX + offsetX;
            y = cellY + offsetY;
            if (cellX % 2 == 1 && x % 2 == 0) y++;
            if (centerX % 2 == 1) y++;
            if (centerX % 2 == 1 && patternWidth / 2 % 2 == 0) y--;
        }

        private bool CanFitSimulated(HexMap pattern, int offsetX, int offsetY, UpgradeInstance upg)
        {
            int centerX = offsetX + pattern.Width / 2;
            var e = pattern.GetActiveCells();
            while (e.MoveNext())
            {
                GetWorldCoords(e.X, e.Y, offsetX, offsetY, pattern.Width, centerX, out int x, out int y);
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
                GetWorldCoords(e.X, e.Y, p.OffsetX, p.OffsetY, pattern.Width, centerX, out int x, out int y);
                _simGrid.Occupied[x, y] = p.Instance;
            }
            _placedInstances.Add(p.Instance);
            if (IsRequired(p.Instance)) _currentReq++;
        }

        private void RemoveSimulated(PlacedUpgrade p)
        {
            var pattern = p.Instance.GetPattern().GetModifiedMap(p.Rotation);
            int centerX = p.OffsetX + pattern.Width / 2;
            var e = pattern.GetActiveCells();
            while (e.MoveNext())
            {
                GetWorldCoords(e.X, e.Y, p.OffsetX, p.OffsetY, pattern.Width, centerX, out int x, out int y);
                _simGrid.Occupied[x, y] = null;
            }
            _placedInstances.Remove(p.Instance);
            if (IsRequired(p.Instance)) _currentReq--;
        }

        private bool WouldFitWithRotation(HexMap pattern, int rotation)
        {
            HexMap rotPattern;
            try { rotPattern = pattern.GetModifiedMap(rotation); } catch { return false; }
            if (rotPattern == null) return false;

            for (int y = 0; y < _simGrid.Height; y++)
                for (int x = 0; x < _simGrid.Width; x++)
                {
                    int centerX = rotPattern.Width / 2;
                    int offsetX = x - centerX;
                    int sumY = 0, count = 0;
                    for (int i = 0; i < rotPattern.Height; i++)
                        if (rotPattern[centerX, i].enabled) { sumY += i; count++; }
                    int offsetY = count == 0 ? (y - rotPattern.Height / 2) : (y - sumY / count);
                    if (centerX % 2 == 1) offsetY--;
                    if (CanFitDummy(rotPattern, offsetX, offsetY)) return true;
                }
            return false;
        }

        private bool CanFitDummy(HexMap pattern, int offsetX, int offsetY)
        {
            int centerX = offsetX + pattern.Width / 2;
            var e = pattern.GetActiveCells();
            while (e.MoveNext())
            {
                GetWorldCoords(e.X, e.Y, offsetX, offsetY, pattern.Width, centerX, out int x, out int y);
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
                    if (r) { ok++; actuallyApplied.Add(p); }
                    else { fail++; BuildArchitectPlugin.Log.LogWarning($"Failed to place {p.DisplayName} at {p.OffsetX},{p.OffsetY} rot {p.Rotation}"); }
                }
                catch (Exception ex) { fail++; BuildArchitectPlugin.Log.LogWarning("Apply failed: " + ex.Message); }
                yield return null;
            }

            try { _onUpgradesChanged(); } catch (Exception ex) { BuildArchitectPlugin.Log.LogWarning("OnUpgradesChanged: " + ex.Message); }

            variant.Upgrades = actuallyApplied;
            variant.PlacedCount = ok;

            BuildArchitectPlugin.Log.LogInfo($"Apply: {ok} placed, {fail} failed");
            done(ok > 0);
        }

        private void UnequipAll()
        {
            var set = new HashSet<UpgradeInstance>();
            var e = _hexMap.GetActiveCells();
            while (e.MoveNext())
                if (e.Current.upgrade != null) set.Add(e.Current.upgrade);
            foreach (var u in set)
            {
                try { _equipSlots.Unequip(_gear, u); }
                catch (Exception ex) { BuildArchitectPlugin.Log.LogWarning("UnequipAll: " + ex.Message); }
            }
        }

        internal static int GetSourceId(UpgradeInstance u)
        {
            try { int id = u.SourceUpgradeID.ID; if (id != 0) return id; } catch { }
            try { return u.Upgrade.Name.GetHashCode(); } catch { }
            return u.InstanceID;
        }

        private static List<UpgradeInstance> SortCandidates(
            List<UpgradeInstance> list, string strategy, HashSet<int> requiredIds, LayoutSolver self)
        {
            List<UpgradeInstance> sorted;
            switch (strategy)
            {
                case "Large first": sorted = list.OrderByDescending(SafePatternCount).ToList(); break;
                case "Small first": sorted = list.OrderBy(SafePatternCount).ToList(); break;
                case "Rarity first":
                    sorted = list.OrderByDescending(u => { try { return (int)u.Upgrade.Rarity; } catch { return 0; } })
                                 .ThenByDescending(SafePatternCount).ToList();
                    break;
                case "Expanders first":
                    sorted = list.OrderByDescending(u => { try { return u.Upgrade.Name.ToLower().Contains("expand") ? 1 : 0; } catch { return 0; } })
                                 .ThenByDescending(SafePatternCount).ToList();
                    break;
                case "Random":
                    {
                        var rng = new System.Random(unchecked(Environment.TickCount * 31 + list.Count));
                        sorted = list.OrderBy(_ => rng.Next()).ToList();
                        break;
                    }
                default: sorted = list.ToList(); break;
            }
            return sorted.OrderByDescending(u => self.IsRequired(u) ? 1 : 0).ToList();
        }

        private static int SafePatternCount(UpgradeInstance u)
        {
            try { return u.GetPattern()?.GetCellCount() ?? 1; } catch { return 1; }
        }

        internal static string SafeName(UpgradeInstance u)
        {
            try { var n = u.Upgrade.GetInstanceName(u); if (!string.IsNullOrEmpty(n)) return StripTags(n); } catch { }
            try { return u.Upgrade.Name; } catch { }
            return "Unknown";
        }

        internal static string SafeDescription(UpgradeInstance u)
        {
            try
            {
                var upg = u.Upgrade;
                if (upg == null) return "";

                var t = upg.GetType();
                string flavor = "";
                string stats = "";

                var descProp = t.GetProperty("Description", BindingFlags.Public | BindingFlags.Instance);
                if (descProp != null && descProp.PropertyType == typeof(string))
                    flavor = descProp.GetValue(upg) as string ?? "";

                var statMethod = t.GetMethod("GetStatList", BindingFlags.Public | BindingFlags.Instance);
                if (statMethod != null && statMethod.ReturnType == typeof(string))
                {
                    try
                    {
                        var parameters = statMethod.GetParameters();
                        if (parameters.Length == 2 && parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType == typeof(UpgradeInstance))
                            stats = statMethod.Invoke(upg, new object[] { u.Seed, u }) as string ?? "";
                        else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(UpgradeInstance))
                            stats = statMethod.Invoke(upg, new object[] { u }) as string ?? "";
                    }
                    catch { }
                }

                flavor = StripTags(flavor).Trim();
                stats = StripTags(stats).Trim();

                if (!string.IsNullOrEmpty(flavor) && !string.IsNullOrEmpty(stats))
                    return flavor + "\n" + stats;
                if (!string.IsNullOrEmpty(flavor)) return flavor;
                if (!string.IsNullOrEmpty(stats)) return stats;
                return "";
            }
            catch { }
            return "";
        }

        internal static string StripTags(string? s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s ?? "";
            var sb = new StringBuilder(s.Length);
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