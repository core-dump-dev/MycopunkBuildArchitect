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
        private static readonly FieldInfo EquipSlotsField =
            typeof(GearDetailsWindow).GetField("equipSlots", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly GearDetailsWindow _window;
        private readonly ModuleEquipSlots _equipSlots;
        private readonly HexMap _hexMap;
        private readonly IUpgradable _gear;
        private readonly IUpgradable? _global;

        public LayoutSolver(GearDetailsWindow window)
        {
            _window = window;
            _equipSlots = (ModuleEquipSlots)EquipSlotsField.GetValue(window);
            _hexMap = _equipSlots.HexMap;
            _gear = window.UpgradablePrefab;
            try { _global = Global.Instance; } catch { _global = null; }
        }

        public List<UpgradeInstance> CollectCandidates()
        {
            var result = new List<UpgradeInstance>();
            CollectFrom(_gear, result);
            if (_gear is Character && _global != null && PlayerData.HasUpgrades(_global))
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
            var placed = new List<PlacedUpgrade>();
            var missing = new List<string>();

            foreach (var upg in ordered)
            {
                if (HasStackingConflict(upg)) { missing.Add(SafeName(upg) + " (conflict)"); continue; }

                HexMap pattern;
                try { pattern = upg.GetPattern(); } catch { missing.Add(SafeName(upg) + " (no pattern)"); continue; }
                if (pattern == null) { missing.Add(SafeName(upg) + " (null pattern)"); continue; }

                // Уважаем игровую блокировку поворотов по редкости
                bool canRotate;
                try { canRotate = PlayerData.Instance.CanRotateUpgrade(upg.Upgrade.Rarity); }
                catch { canRotate = false; }
                int maxRot = canRotate ? 6 : 1;

                bool did = false;
                for (int rot = 0; rot < maxRot && !did; rot++)
                {
                    HexMap rotPattern;
                    try { rotPattern = pattern.GetModifiedMap(rot); } catch { continue; }
                    if (rotPattern == null) continue;

                    for (int y = 0; y < _hexMap.Height && !did; y++)
                    {
                        for (int x = 0; x < _hexMap.Width && !did; x++)
                        {
                            int centerX = rotPattern.Width / 2;
                            int offsetX = x - centerX;
                            int sumY = 0, count = 0;
                            for (int i = 0; i < rotPattern.Height; i++)
                                if (rotPattern[centerX, i].enabled) { sumY += i; count++; }
                            int offsetY = count == 0 ? (y - rotPattern.Height / 2) : (y - sumY / count);
                            if (centerX % 2 == 1) offsetY--;

                            if (!CanFit(rotPattern, offsetX, offsetY)) continue;

                            bool ok = false;
                            try { ok = _equipSlots.EquipModule(_gear, upg, offsetX, offsetY, (byte)rot, sort: false, equippedManually: true); }
                            catch (Exception ex) { BuildArchitectPlugin.Log.LogWarning("Equip failed: " + ex.Message); }

                            if (ok)
                            {
                                placed.Add(new PlacedUpgrade {
                                    Instance = upg,
                                    DisplayName = SafeName(upg),
                                    Rotation = rot,
                                    OffsetX = offsetX,
                                    OffsetY = offsetY,
                                });
                                did = true;
                            }
                        }
                    }
                }

                if (!did)
                {
                    bool wouldFitRotated = false;
                    if (!canRotate)
                    {
                        for (int r = 1; r < 6 && !wouldFitRotated; r++)
                            if (WouldFitWithRotation(pattern, r)) wouldFitRotated = true;
                    }
                    missing.Add(SafeName(upg) + (wouldFitRotated ? " (needs rotation unlock)" : " (no fit)"));
                }
                yield return null;
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

        public IEnumerator Apply(LayoutVariant variant, Action<bool> done)
        {
            UnequipAll();
            yield return null;

            int ok = 0, fail = 0;
            foreach (var p in variant.Upgrades)
            {
                try
                {
                    bool r = _equipSlots.EquipModule(_gear, p.Instance, p.OffsetX, p.OffsetY, (byte)p.Rotation, sort: false, equippedManually: true);
                    if (r) ok++; else fail++;
                }
                catch (Exception ex) { fail++; BuildArchitectPlugin.Log.LogWarning("Apply failed: " + ex.Message); }
                yield return null;
            }

            try { _window.OnUpgradesChanged(); } catch { }
            BuildArchitectPlugin.Log.LogInfo($"Apply: {ok} placed, {fail} failed");
            done(ok > 0);
        }

        private bool CanFit(HexMap pattern, int offsetX, int offsetY)
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
                if (x < 0 || y < 0 || x >= _hexMap.Width || y >= _hexMap.Height) return false;
                if (_hexMap[x, y].enabled) return false;
            }
            return true;
        }

        private bool WouldFitWithRotation(HexMap pattern, int rotation)
        {
            HexMap rotPattern;
            try { rotPattern = pattern.GetModifiedMap(rotation); } catch { return false; }
            if (rotPattern == null) return false;

            for (int y = 0; y < _hexMap.Height; y++)
            {
                for (int x = 0; x < _hexMap.Width; x++)
                {
                    int centerX = rotPattern.Width / 2;
                    int offsetX = x - centerX;
                    int sumY = 0, count = 0;
                    for (int i = 0; i < rotPattern.Height; i++)
                        if (rotPattern[centerX, i].enabled) { sumY += i; count++; }
                    int offsetY = count == 0 ? (y - rotPattern.Height / 2) : (y - sumY / count);
                    if (centerX % 2 == 1) offsetY--;

                    if (CanFit(rotPattern, offsetX, offsetY)) return true;
                }
            }
            return false;
        }

        private bool HasStackingConflict(UpgradeInstance upgrade)
        {
            var e = _hexMap.GetActiveCells();
            while (e.MoveNext())
            {
                var other = e.Current.upgrade;
                if (other == null || other == upgrade) continue;
                try { if (!Upgrade.CanEquipTogether(upgrade, other)) return true; } catch { }
            }
            return false;
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

        private static List<UpgradeInstance> SortCandidates(List<UpgradeInstance> list, string strategy)
        {
            switch (strategy)
            {
                case "Large first": return list.OrderByDescending(SafePatternCount).ToList();
                case "Small first": return list.OrderBy(SafePatternCount).ToList();
                case "Rarity first": return list.OrderByDescending(u => { try { return (int)u.Upgrade.Rarity; } catch { return 0; } }).ThenByDescending(SafePatternCount).ToList();
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