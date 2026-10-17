using System;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Daily production per company: worker-days × efficiency, limited by input stock and storage.
    /// Efficiency combines energy supply, building condition, breakdowns, adopted technology,
    /// worker education, soil moisture (farms) and nearby forest (forestry).
    /// </summary>
    public sealed class ProductionSystem : ISimulationSystem
    {
        public const double BreakdownChancePerDay = 0.002;
        public const int BreakdownDays = 3;
        public const double StorageDays = 30;
        public const int ForestRadius = 8;

        public string Name => "Production";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EconomyState e = w.Economy;
            foreach (Company co in e.Companies.Values)
            {
                // Power plants have no recipe: their output is recorded earlier in the day by the
                // energy system and must survive until pollution reads it.
                if (co.PowerOutput > 0) continue;
                co.InputShortage = false;
                co.LastOutput = 0;
                co.OutputCapacity = 0;
                if (co.Closed || co.Recipe == null) continue;
                if (!w.TryGetBuilding(co.BuildingId, out Building b)) continue;

                UpdateBreakdown(w, co);
                int workers = co.Workers.Count;
                if (workers == 0) continue;

                double eff = Efficiency(w, co, b);
                double capacity = workers * eff;
                co.OutputCapacity = capacity;

                double batches = capacity;
                foreach (var kv in co.Recipe.Inputs)
                {
                    double possible = co.Stock(kv.Key) / kv.Value;
                    if (possible < batches) { batches = possible; co.InputShortage = true; }
                }
                foreach (var kv in co.Recipe.Outputs)
                {
                    double room = (kv.Value * w.Effects.JobCapacity(co) * StorageDays - co.Stock(kv.Key)) / kv.Value;
                    if (room < batches) batches = Math.Max(0, room);
                }
                if (batches <= 0) continue;

                foreach (var kv in co.Recipe.Inputs) co.Inventory[kv.Key] = Math.Max(0, co.Stock(kv.Key) - kv.Value * batches);
                foreach (var kv in co.Recipe.Outputs)
                {
                    co.AddStock(kv.Key, kv.Value * batches);
                    Market.Add(e.Market.Produced, kv.Key, kv.Value * batches);
                }
                co.LastOutput = batches;
                if (co.Recipe.Id == "harvest_wood") Deforest(w, b, batches);
            }
        }

        public static double Efficiency(WorldState w, Company co, Building b)
        {
            if (co.BrokenDays > 0 || b.Condition < 0.25f) return 0;
            double eff = w.Environment.EnergyRatio * b.Condition;
            eff *= 1 + w.Effects.ForCompany(co, "productivity");
            eff *= EducationFactor(w, co);
            if (co.Def.Category == BuildingCategory.Agriculture)
                eff *= Math.Max(0.3, Math.Min(1.2, 0.5 + w.Environment.SoilMoisture));
            if (co.Recipe != null && co.Recipe.Id == "harvest_wood")
                eff *= ForestFactor(w, b);
            return eff;
        }

        private static double EducationFactor(WorldState w, Company co)
        {
            if (co.Workers.Count == 0) return 1;
            double sum = 0;
            foreach (int id in co.Workers)
                if (w.Economy.Citizens.TryGetValue(id, out Citizen c)) sum += c.Education;
            return 0.85 + 0.1 * sum / co.Workers.Count;
        }

        /// <summary>Share of a well-forested neighbourhood around the building (0..1).</summary>
        public static double ForestFactor(WorldState w, Building b)
        {
            double sum = 0;
            ForEachNear(w, b, (c, v) => sum += v);
            return Math.Min(1.0, sum / (255.0 * 40));
        }

        private static void Deforest(WorldState w, Building b, double workerDays)
        {
            // Each worker-day removes ~3 density points, taken from the densest nearby cells.
            int toRemove = (int)Math.Round(workerDays * 3);
            while (toRemove > 0)
            {
                GridPos best = default;
                int bestV = 0;
                ForEachNear(w, b, (c, v) => { if (v > bestV) { bestV = v; best = c; } });
                if (bestV == 0) return;
                int take = Math.Min(toRemove, Math.Min(bestV, 40));
                w.SetVegetation(best, (byte)(bestV - take));
                toRemove -= take;
            }
        }

        private static void ForEachNear(WorldState w, Building b, Action<GridPos, int> f)
        {
            GridRect r = b.Footprint.Expand(ForestRadius).Clamp(w.Width, w.Depth);
            for (int z = r.MinZ; z < r.MaxZ; z++)
            for (int x = r.MinX; x < r.MaxX; x++)
            {
                var c = new GridPos(x, z);
                int v = w.GetVegetation(c);
                if (v > 0) f(c, v);
            }
        }

        public static void UpdateBreakdown(WorldState w, Company co)
        {
            BuildingCategory cat = co.Def.Category;
            if (cat != BuildingCategory.Industrial && cat != BuildingCategory.Power) return;
            if (co.BrokenDays > 0)
            {
                co.BrokenDays--;
                if (co.BrokenDays == 0)
                {
                    long cost = co.Def.Cost / 100;
                    long paid = w.Economy.Ledger.TransferUpTo(co, w.Economy.External, cost, Flow.Repair);
                    co.CostToday += paid;
                }
                return;
            }
            if (co.Workers.Count > 0 && w.Random.Chance(BreakdownChancePerDay))
            {
                co.BrokenDays = BreakdownDays;
                w.Log("故障", $"{co.Def.Name} #{co.BuildingId} の設備が故障しました（{BreakdownDays}日間停止）");
            }
        }
    }
}
