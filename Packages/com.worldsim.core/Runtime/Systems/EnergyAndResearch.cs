using System;
using System.Collections.Generic;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Electricity balance. Demand comes from businesses (scaled by staffing and energy tech),
    /// public buildings and households; supply from staffed, intact power plants (solar varies with
    /// rain). The served share becomes <see cref="Environment.EnvironmentState.EnergyRatio"/>, which
    /// limits production. Consumers pay power companies; thermal plants buy fuel from outside.
    /// </summary>
    public sealed class EnergySystem : ISimulationSystem
    {
        public const double ResidentKwhPerDay = 8;

        public string Name => "Energy";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EconomyState e = w.Economy;
            var env = w.Environment;
            var consumers = new List<(Account payer, Company co, double kwh)>();
            double demand = 0, supply = 0;
            var plants = new List<(Company co, double output)>();

            foreach (Company co in e.Companies.Values)
            {
                if (!w.TryGetBuilding(co.BuildingId, out Building b)) continue;
                if (co.PowerOutput > 0)
                {
                    ProductionSystem.UpdateBreakdown(w, co);
                    double staffing = co.TargetWorkers == 0 ? 0 : Math.Min(1.0, co.Workers.Count / (double)co.TargetWorkers);
                    double output = co.Closed || co.BrokenDays > 0 || b.Condition < 0.25f ? 0 : co.PowerOutput * staffing * b.Condition;
                    if (co.Def.Id == "solar_farm") output *= env.RainToday > 5 ? 0.5 : 1.0;
                    if (output > 0) { plants.Add((co, output)); supply += output; }
                    continue;
                }
                if (co.Closed) { co.EnergyUse = 0; continue; }
                double cap = Math.Max(1, w.Effects.JobCapacity(co));
                double activity = co.IsPublic ? 1 : Math.Min(1, co.Workers.Count / cap);
                double kwh = co.Def.GetParam("energy") * activity * Math.Max(0.1, 1 + w.Effects.ForCompany(co, "energy"));
                co.EnergyUse = kwh;
                demand += kwh;
                if (kwh > 0) consumers.Add((co.IsPublic ? (Account)e.City : co, co, kwh));
            }
            foreach (Citizen c in e.Citizens.Values)
            {
                if (c.HomeId == 0) continue;
                demand += ResidentKwhPerDay;
                consumers.Add((c, null, ResidentKwhPerDay));
            }

            env.EnergyDemand = demand;
            env.EnergySupply = supply;
            env.EnergyRatio = demand <= 0 ? 1 : Math.Min(1, supply / demand);
            if (supply <= 0) return;

            double price = e.City.Policy.ElectricityPrice;
            double delivered = Math.Min(supply, demand);
            // Bills are collected into each plant in proportion to its share of output.
            foreach (var (payer, co, kwh) in consumers)
            {
                long bill = (long)Math.Round(kwh * env.EnergyRatio * price);
                if (bill <= 0) continue;
                long remaining = bill;
                for (int i = 0; i < plants.Count; i++)
                {
                    long share = i == plants.Count - 1 ? remaining : (long)(bill * plants[i].output / supply);
                    long paid = e.Ledger.TransferUpTo(payer, plants[i].co, share, Flow.Energy);
                    plants[i].co.RevenueToday += paid;
                    if (co != null && !co.IsPublic) co.CostToday += paid;
                    remaining -= share;
                }
            }
            foreach (var (co, output) in plants)
            {
                double used = output / supply * delivered;
                long fuel = (long)Math.Round(used * co.Def.GetParam("fuelCost"));
                long paid = e.Ledger.TransferUpTo(co, e.External, fuel, Flow.Fuel);
                co.CostToday += paid;
                co.LastOutput = used;
                co.OutputCapacity = output;
            }
        }
    }

    /// <summary>
    /// Research labs turn staffed, educated workers into research points. Completed technologies
    /// either apply city-wide at once or are adopted company by company when they can afford it
    /// (scenario C: research → adoption → productivity, jobs, energy, pollution change).
    /// </summary>
    public sealed class ResearchSystem : ISimulationSystem
    {
        public const double AdoptionCashMultiple = 3;

        public string Name => "Research";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EconomyState e = w.Economy;
            ResearchState rs = e.City.Research;
            double points = 0;
            foreach (Company co in e.Companies.Values)
            {
                double perWorker = co.Def.GetParam("research");
                if (perWorker <= 0 || co.Closed || !w.TryGetBuilding(co.BuildingId, out Building b)) continue;
                double edu = 0;
                foreach (int id in co.Workers)
                    if (e.Citizens.TryGetValue(id, out Citizen c)) edu += c.Education;
                double eduFactor = co.Workers.Count == 0 ? 0 : 0.6 + 0.3 * edu / co.Workers.Count;
                points += perWorker * co.Workers.Count * eduFactor * b.Condition * w.Environment.EnergyRatio;
            }
            rs.PointsYesterday = points;

            if (rs.Current == null || rs.Completed.Contains(rs.Current) || !Available(w, rs.Current)) rs.Current = PickDefault(w);
            if (rs.Current != null && points > 0)
            {
                rs.Progress += points;
                TechDef t = w.Content.GetTech(rs.Current);
                if (rs.Progress >= t.Cost)
                {
                    rs.Completed.Add(t.Id);
                    rs.Progress = 0;
                    rs.Current = null;
                    w.Log("技術", $"新技術「{t.Name}」の研究が完了しました：{t.Description}");
                    if (t.Effects.ContainsKey("roadSpeed")) w.Network.Invalidate();
                }
            }

            Adopt(w);
        }

        public static bool Available(WorldState w, string techId)
        {
            if (!w.Content.TryGetTech(techId, out TechDef t)) return false;
            ResearchState rs = w.Economy.City.Research;
            if (rs.Completed.Contains(techId)) return false;
            foreach (string p in t.Prereqs) if (!rs.Completed.Contains(p)) return false;
            return true;
        }

        private static string PickDefault(WorldState w)
        {
            TechDef best = null;
            foreach (TechDef t in w.Content.Techs)
                if (Available(w, t.Id) && (best == null || t.Cost < best.Cost)) best = t;
            return best?.Id;
        }

        private static void Adopt(WorldState w)
        {
            EconomyState e = w.Economy;
            foreach (string id in e.City.Research.Completed)
            {
                TechDef t = w.Content.GetTech(id);
                if (t.AdoptionCost <= 0) continue;
                foreach (Company co in e.Companies.Values)
                {
                    if (co.IsPublic || co.Closed || co.AdoptedTechs.Contains(id) || !t.AffectsCategory(co.Def.Category)) continue;
                    if (co.Cash < t.AdoptionCost * AdoptionCashMultiple) continue;
                    e.Ledger.Transfer(co, e.External, t.AdoptionCost, Flow.TechAdoption);
                    co.AdoptedTechs.Add(id);
                    w.Log("技術導入", $"{co.Def.Name} #{co.BuildingId} が「{t.Name}」を導入しました");
                }
            }
        }
    }
}
