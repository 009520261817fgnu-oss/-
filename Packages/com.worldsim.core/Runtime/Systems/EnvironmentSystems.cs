using System;
using System.Collections.Generic;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Environment;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Simple weather: seasonal temperature plus warming, Markov rain days, soil moisture that
    /// feeds crop yields and, after long heavy rain, floods (via DisasterSystem).
    /// </summary>
    public sealed class WeatherSystem : ISimulationSystem
    {
        public string Name => "Weather";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EnvironmentState env = w.Environment;
            DeterministicRandom rng = w.Random;
            double doy = w.Clock.DayOfYear;
            env.Temperature = (float)(14 + 11 * Math.Sin(2 * Math.PI * (doy - 100) / SimClock.DaysPerYear)
                                      + (rng.NextDouble() - 0.5) * 6 + env.Warming);
            double wetChance = env.WetStreak > 0 ? 0.55 : 0.25;
            if (rng.Chance(wetChance))
            {
                env.WetStreak++;
                env.RainToday = (float)(-Math.Log(1 - rng.NextDouble()) * 14);
            }
            else
            {
                env.WetStreak = 0;
                env.RainToday = 0;
            }
            double evaporation = 0.04 + Math.Max(0, env.Temperature - 20) * 0.004;
            env.SoilMoisture = (float)Math.Max(0, Math.Min(1.5, env.SoilMoisture * (1 - evaporation) + env.RainToday / 60.0));
            env.RainLast3Days = env.RainLast3Days * 2f / 3f + env.RainToday;
        }
    }

    /// <summary>
    /// Air pollution on a coarse grid: emissions from industry and power, decay, diffusion and
    /// absorption by forest. High pollution damages forest; clean areas slowly regrow.
    /// Cumulative emissions raise temperature slightly (very simplified climate change).
    /// </summary>
    public sealed class PollutionSystem : ISimulationSystem
    {
        public const double WarmingPerEmission = 2e-8;

        public string Name => "Pollution";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EnvironmentState env = w.Environment;
            float[] p = env.Pollution;
            int gw = env.GridW, gd = env.GridD;
            double emitted = 0;
            foreach (Company co in w.Economy.Companies.Values)
            {
                double rate = co.Def.GetParam("pollution");
                if (rate <= 0 || co.Closed || !w.TryGetBuilding(co.BuildingId, out Building b)) continue;
                double activity = co.OutputCapacity > 0 ? Math.Min(1, co.LastOutput / co.OutputCapacity) : 0;
                if (co.PowerOutput > 0) activity = co.PowerOutput > 0 ? Math.Min(1, co.LastOutput / co.PowerOutput) : 0;
                double amount = rate * activity * Math.Max(0, 1 + w.Effects.ForCompany(co, "pollution"));
                if (amount <= 0) continue;
                int gx = (b.Origin.X + b.Def.Width / 2) / EnvironmentState.PollutionCell;
                int gz = (b.Origin.Z + b.Def.Depth / 2) / EnvironmentState.PollutionCell;
                p[Math.Min(gd - 1, gz) * gw + Math.Min(gw - 1, gx)] += (float)amount;
                emitted += amount;
            }
            env.CumulativeEmissions += emitted;
            env.Warming = env.CumulativeEmissions * WarmingPerEmission * 1000;

            var next = new float[p.Length];
            for (int z = 0; z < gd; z++)
            for (int x = 0; x < gw; x++)
            {
                int i = z * gw + x;
                float sum = 0;
                int n = 0;
                if (x > 0) { sum += p[i - 1]; n++; }
                if (x < gw - 1) { sum += p[i + 1]; n++; }
                if (z > 0) { sum += p[i - gw]; n++; }
                if (z < gd - 1) { sum += p[i + gw]; n++; }
                float diffused = p[i] * 0.8f + (n > 0 ? sum / n * 0.2f : 0);
                next[i] = diffused * 0.85f;
            }
            Array.Copy(next, p, p.Length);

            Vegetation(w, env);
        }

        private static void Vegetation(WorldState w, EnvironmentState env)
        {
            DeterministicRandom rng = w.Random;
            // Absorption and damage on every forested cell of a sample of grid cells.
            for (int k = 0; k < 400; k++)
            {
                var c = new GridPos(rng.NextInt(0, w.Width), rng.NextInt(0, w.Depth));
                int v = w.GetVegetation(c);
                int gi = (c.Z / EnvironmentState.PollutionCell) * env.GridW + c.X / EnvironmentState.PollutionCell;
                float pol = env.Pollution[gi];
                if (v > 0)
                {
                    env.Pollution[gi] = Math.Max(0, pol - v / 255f * 0.5f);
                    if (pol > 40) w.SetVegetation(c, (byte)Math.Max(0, v - 10));
                    else if (v < 255 && pol < 10) w.SetVegetation(c, (byte)Math.Min(255, v + 3));
                }
                else if (pol < 5 && !w.IsOccupied(c) && !w.Terrain.IsCellUnderwater(c) && HasForestNeighbour(w, c)
                         && w.Terrain.CellMaxHeight(c) - w.Terrain.CellMinHeight(c) < 6)
                {
                    w.SetVegetation(c, 20);
                }
            }
        }

        private static bool HasForestNeighbour(WorldState w, GridPos c)
        {
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var n = new GridPos(c.X + dx, c.Z + dz);
                if ((dx != 0 || dz != 0) && w.Terrain.InBounds(n) && w.GetVegetation(n) > 60) return true;
            }
            return false;
        }
    }

    public enum DisasterKind
    {
        Flood,
        Earthquake,
    }

    /// <summary>
    /// Floods (after heavy rain, or triggered) and earthquakes (rare, or triggered) damage buildings
    /// and roads. Damage is reduced by resilience from preparedness spending and seismic tech
    /// (scenario D). Damaged roads are impassable and damaged buildings work at reduced capacity.
    /// </summary>
    public sealed class DisasterSystem : ISimulationSystem
    {
        public const double FloodRainThreshold = 110;
        public const double EarthquakeChancePerDay = 1.0 / 3000;

        public string Name => "Disaster";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EnvironmentState env = w.Environment;
            if (env.FloodDaysLeft > 0 && --env.FloodDaysLeft == 0)
            {
                env.FloodLevel = 0;
                w.Log("災害", "洪水の水が引きました");
            }
            if (env.FloodDaysLeft == 0 && env.RainLast3Days > FloodRainThreshold)
            {
                float depth = (float)Math.Min(4.0, 1.5 + (env.RainLast3Days - FloodRainThreshold) / 40.0);
                Flood(w, w.Terrain.SeaLevel + depth);
            }
            if (w.Random.Chance(EarthquakeChancePerDay))
            {
                var epicenter = new GridPos(w.Random.NextInt(0, w.Width), w.Random.NextInt(0, w.Depth));
                Earthquake(w, epicenter, 5.5 + w.Random.NextDouble() * 1.5);
            }
        }

        /// <summary>0 (no protection) to 0.8 (heavily protected).</summary>
        public static double Resilience(WorldState w)
        {
            double prep = 0.5 * (1 - Math.Exp(-w.Economy.City.PreparednessStock / 300_000_000.0));
            return Math.Min(0.8, prep + w.Effects.Global("disasterResilience"));
        }

        public static void Flood(WorldState w, float waterLevel)
        {
            EnvironmentState env = w.Environment;
            env.FloodLevel = waterLevel;
            env.FloodDaysLeft = 2;
            double protect = 1 - Resilience(w);
            int damaged = 0, roads = 0;
            foreach (Building b in w.Buildings)
            {
                w.Terrain.HeightRange(b.Footprint, out float min, out _);
                if (min >= waterLevel) continue;
                double dmg = Math.Min(0.9, (0.3 + (waterLevel - min) * 0.15) * protect);
                w.SetCondition(b, (float)(b.Condition - dmg));
                damaged++;
            }
            for (int z = 0; z < w.Depth; z++)
            for (int x = 0; x < w.Width; x++)
            {
                var c = new GridPos(x, z);
                if (!w.HasRoad(c) || w.Terrain.CellMinHeight(c) >= waterLevel) continue;
                if (w.Random.Chance(0.3 + 0.7 * protect)) { w.SetRoadDamaged(c, true); roads++; }
            }
            w.Log("災害", $"洪水発生（水位 {waterLevel:0.0}m）：建物 {damaged} 棟、道路 {roads} マスが被害");
        }

        public static void Earthquake(WorldState w, GridPos epicenter, double magnitude)
        {
            double protect = 1 - Resilience(w);
            double strength = Math.Max(0, (magnitude - 5) * 0.45);
            int damaged = 0, roads = 0;
            foreach (Building b in w.Buildings)
            {
                double d = Math.Sqrt(Sq(b.Origin.X - epicenter.X) + Sq(b.Origin.Z - epicenter.Z));
                double intensity = strength * Math.Exp(-d / 45.0);
                double dmg = intensity * (0.5 + 0.5 * w.Random.NextDouble()) * protect;
                if (dmg < 0.05) continue;
                w.SetCondition(b, (float)(b.Condition - Math.Min(0.95, dmg)));
                damaged++;
            }
            for (int z = 0; z < w.Depth; z++)
            for (int x = 0; x < w.Width; x++)
            {
                var c = new GridPos(x, z);
                if (!w.HasRoad(c)) continue;
                double intensity = strength * Math.Exp(-Math.Sqrt(Sq(x - epicenter.X) + Sq(z - epicenter.Z)) / 45.0);
                if (w.Random.Chance(intensity * 0.35 * protect)) { w.SetRoadDamaged(c, true); roads++; }
            }
            w.Log("災害", $"地震発生（M{magnitude:0.0}、震源 {epicenter}）：建物 {damaged} 棟、道路 {roads} マスが被害");
        }

        private static double Sq(double v) => v * v;
    }

    /// <summary>
    /// The city repairs damage within its daily repair budget, using crews (more with staffed
    /// fire/recovery stations): roads first, then power, industry, public, commerce, farms, homes.
    /// </summary>
    public sealed class RepairSystem : ISimulationSystem
    {
        public const int BaseCrews = 2;
        public const int RoadCellsPerCrew = 6;
        public const float ConditionPerCrew = 0.2f;
        public const long RoadRepairCost = 40_000;

        public string Name => "Repair";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EconomyState e = w.Economy;
            long budget = Math.Min(e.City.Policy.RepairBudget, Math.Max(0, e.City.Cash));
            double crews = BaseCrews;
            foreach (Company co in e.Companies.Values)
            {
                double c = co.Def.GetParam("repairCrews");
                if (c > 0 && co.TargetWorkers > 0) crews += c * co.Workers.Count / (double)co.TargetWorkers;
            }

            double roadCapacity = crews * RoadCellsPerCrew;
            for (int z = 0; z < w.Depth && roadCapacity >= 1; z++)
            for (int x = 0; x < w.Width && roadCapacity >= 1; x++)
            {
                var c = new GridPos(x, z);
                if (!w.IsRoadDamaged(c) || budget < RoadRepairCost) continue;
                e.Ledger.Transfer(e.City, e.External, RoadRepairCost, Flow.Repair);
                budget -= RoadRepairCost;
                w.SetRoadDamaged(c, false);
                roadCapacity--;
                crews -= 1.0 / RoadCellsPerCrew;
            }

            var damaged = new List<Building>();
            foreach (Building b in w.Buildings) if (b.Condition < 1f) damaged.Add(b);
            damaged.Sort((a, b) => Priority(a).CompareTo(Priority(b)) != 0 ? Priority(a).CompareTo(Priority(b)) : a.Id.CompareTo(b.Id));
            foreach (Building b in damaged)
            {
                if (crews <= 0.01 || budget <= 0) break;
                float amount = (float)Math.Min(1 - b.Condition, ConditionPerCrew * Math.Min(1, crews));
                long cost = (long)Math.Ceiling(b.Def.Cost * 0.3 * amount);
                if (cost > budget)
                {
                    amount *= (float)budget / cost;
                    cost = budget;
                }
                e.Ledger.Transfer(e.City, e.External, cost, Flow.Repair);
                budget -= cost;
                w.SetCondition(b, b.Condition + amount >= 0.999f ? 1f : b.Condition + amount);
                crews -= 1;
            }
        }

        private static int Priority(Building b)
        {
            switch (b.Def.Category)
            {
                case BuildingCategory.Power: return 0;
                case BuildingCategory.Industrial: return 1;
                case BuildingCategory.Public: return 2;
                case BuildingCategory.Commercial: return 3;
                case BuildingCategory.Agriculture: return 4;
                default: return 5;
            }
        }
    }
}
