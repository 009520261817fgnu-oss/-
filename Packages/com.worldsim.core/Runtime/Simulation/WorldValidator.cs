using System;
using System.Collections.Generic;
using WorldSim.Core.Economy;
using WorldSim.Core.World;

namespace WorldSim.Core.Simulation
{
    /// <summary>
    /// Checks the cross-cutting invariants of the play state (spec 7.1): money conservation, no
    /// negative savings or stock, no NaN, consistent employment and residence links. Returns a list
    /// of problems (empty = consistent). Used by long-run tests and available as a debug check.
    /// </summary>
    public static class WorldValidator
    {
        public static List<string> Validate(WorldState w)
        {
            var problems = new List<string>();
            EconomyState e = w.Economy;
            if (e.TotalMoney() != 0) problems.Add($"お金の総量が保存されていません: {e.TotalMoney()}");

            var occupancy = new Dictionary<int, int>();
            var employedAt = new Dictionary<int, int>();
            foreach (Citizen c in e.Citizens.Values)
            {
                if (c.Cash < 0) problems.Add($"住民 #{c.Id} の貯蓄が負: {c.Cash}");
                if (!Finite(c.Happiness) || !Finite(c.CommuteHours)) problems.Add($"住民 #{c.Id} に不正な数値");
                if (c.HomeId != 0)
                {
                    if (!w.TryGetBuilding(c.HomeId, out Building h) || h.Def.Housing == 0) problems.Add($"住民 #{c.Id} の住居 #{c.HomeId} が不正");
                    occupancy[c.HomeId] = (occupancy.TryGetValue(c.HomeId, out int n) ? n : 0) + 1;
                }
                if (c.JobId != 0)
                {
                    if (!e.Companies.TryGetValue(c.JobId, out Company co) || !co.Workers.Contains(c.Id))
                        problems.Add($"住民 #{c.Id} の職場 #{c.JobId} と雇用者名簿が一致しません");
                    employedAt[c.JobId] = (employedAt.TryGetValue(c.JobId, out int n) ? n : 0) + 1;
                }
            }
            foreach (var kv in occupancy)
            {
                if (e.Residents(kv.Key) != kv.Value) problems.Add($"建物 #{kv.Key} の居住者数が不一致");
                if (w.TryGetBuilding(kv.Key, out Building b) && kv.Value > b.Def.Housing) problems.Add($"建物 #{kv.Key} が定員超過");
            }
            foreach (Company co in e.Companies.Values)
            {
                if (!w.TryGetBuilding(co.BuildingId, out _)) problems.Add($"企業 #{co.BuildingId} の建物がありません");
                if (!co.IsPublic && co.Cash < 0) problems.Add($"企業 #{co.BuildingId} の資金が負: {co.Cash}");
                int listed = co.Workers.Count;
                if (listed != new HashSet<int>(co.Workers).Count) problems.Add($"企業 #{co.BuildingId} の従業員が重複");
                if (listed != (employedAt.TryGetValue(co.BuildingId, out int n) ? n : 0)) problems.Add($"企業 #{co.BuildingId} の従業員数が不一致");
                foreach (var kv in co.Inventory)
                    if (kv.Value < -1e-6 || !Finite(kv.Value)) problems.Add($"企業 #{co.BuildingId} の在庫 {kv.Key} が不正: {kv.Value}");
                if (!Finite(co.ProfitAvg)) problems.Add($"企業 #{co.BuildingId} の利益が不正");
            }
            foreach (var kv in e.Market.Price)
                if (!Finite(kv.Value) || kv.Value <= 0) problems.Add($"価格 {kv.Key} が不正: {kv.Value}");
            foreach (float p in w.Environment.Pollution)
                if (!Finite(p) || p < 0) { problems.Add("汚染値が不正"); break; }
            foreach (float v in w.TrafficVolume)
                if (!Finite(v) || v < 0) { problems.Add("交通量が不正"); break; }
            if (!Finite(w.Environment.EnergyRatio)) problems.Add("電力比率が不正");
            People.PlayerState pl = w.Player;
            if (pl.IsActive)
            {
                if (!e.Citizens.ContainsKey(pl.CitizenId)) problems.Add("操作中の人物が存在しません");
                if (pl.InsideBuildingId != 0 && !w.TryGetBuilding(pl.InsideBuildingId, out _)) problems.Add("人物が存在しない建物の中にいます");
                if (pl.Pantry < 0 || !Finite(pl.Hunger) || !Finite(pl.Energy)) problems.Add("人物の状態が不正");
            }
            return problems;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
