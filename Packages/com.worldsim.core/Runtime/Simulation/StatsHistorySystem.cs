using System;
using System.Collections.Generic;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.World;

namespace WorldSim.Core.Simulation
{
    /// <summary>One day's key indicators. Series of these feed graphs and branch comparison.</summary>
    public sealed class StatsSnapshot
    {
        public long Tick;
        public int Buildings;
        public int RoadCells;
        public readonly Dictionary<BuildingCategory, int> ByCategory = new Dictionary<BuildingCategory, int>();

        public int Population, LabourForce, Employed, Jobs, Vacancies, Retired, Children;
        public int CompaniesOpen, CompaniesClosed;
        public long Treasury, CompanyCash, HouseholdCash, TotalMoney;
        public double AvgWage, AvgHappiness, AvgCommuteHours, AvgHealth;
        public long Wages, RetailSales, B2BSales, Imports, Exports, Freight, Taxes, CompanyProfit;
        public int Births, Deaths, Immigrants, Emigrants, Moves;
        public readonly SortedDictionary<string, double> Production = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, double> Prices = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, double> Imported = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, double> Unmet = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public double TrafficTotal, CongestedCells;
        public double EnergyDemand, EnergySupply, AvgPollution, Temperature, Rain, SoilMoisture;
        public double ResearchPoints;
        public int TechsCompleted, DamagedBuildings, DamagedRoads;

        public double UnemploymentRate => LabourForce == 0 ? 0 : 1.0 - (double)Employed / LabourForce;

        /// <summary>Named numeric series for generic charts and comparison.</summary>
        public static readonly string[] SeriesKeys =
        {
            "population", "unemployment", "treasury", "avgWage", "happiness", "retailSales", "wages", "taxes",
            "imports", "exports", "companyProfit", "companiesOpen", "traffic", "commute", "pollution",
            "energyDemand", "energySupply", "research", "damaged", "temperature", "health",
            "prod.grain", "prod.iron_ore", "prod.wood", "prod.steel", "prod.food", "prod.goods", "prod.services",
        };

        public double Get(string key)
        {
            switch (key)
            {
                case "population": return Population;
                case "unemployment": return UnemploymentRate * 100;
                case "treasury": return Treasury;
                case "avgWage": return AvgWage;
                case "happiness": return AvgHappiness * 100;
                case "retailSales": return RetailSales;
                case "wages": return Wages;
                case "taxes": return Taxes;
                case "imports": return Imports;
                case "exports": return Exports;
                case "companyProfit": return CompanyProfit;
                case "companiesOpen": return CompaniesOpen;
                case "traffic": return TrafficTotal;
                case "commute": return AvgCommuteHours * 60;
                case "pollution": return AvgPollution;
                case "energyDemand": return EnergyDemand;
                case "energySupply": return EnergySupply;
                case "research": return ResearchPoints;
                case "damaged": return DamagedBuildings + DamagedRoads;
                case "temperature": return Temperature;
                case "health": return AvgHealth * 100;
            }
            if (key.StartsWith("prod.", StringComparison.Ordinal))
                return Production.TryGetValue(key.Substring(5), out double v) ? v : 0;
            throw new ArgumentException($"Unknown series '{key}'");
        }
    }

    /// <summary>
    /// Runs last each day: records a snapshot into <see cref="WorldState.History"/>, then rolls
    /// daily accumulators (ledger, market volumes, retail demand) over to the next day.
    /// </summary>
    public sealed class StatsHistorySystem : ISimulationSystem
    {
        public string Name => "Stats";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState world)
        {
            world.History.Add(Capture(world));
            EndDay(world);
        }

        public static void EndDay(WorldState w)
        {
            EconomyState e = w.Economy;
            foreach (Company co in e.Companies.Values)
            {
                foreach (var kv in co.SalesToday)
                {
                    double prev = co.SalesAvg.TryGetValue(kv.Key, out double a) ? a : kv.Value;
                    co.SalesAvg[kv.Key] = prev * 0.8 + kv.Value * 0.2;
                }
                co.SalesToday.Clear();
            }
            e.Causes.CloseDay(e, w.Clock.TotalDays);
            e.Ledger.EndDay();
            e.Market.ClearDaily();
            w.Trips.Clear();
        }

        /// <summary>Snapshot of the current state; "today" flows are read before the day is rolled over.</summary>
        public static StatsSnapshot Capture(WorldState world)
        {
            EconomyState e = world.Economy;
            long today = world.Clock.TotalDays;
            var s = new StatsSnapshot { Tick = world.Clock.Tick, Buildings = world.BuildingCount, RoadCells = world.RoadCellCount };
            foreach (Building b in world.Buildings)
            {
                s.ByCategory.TryGetValue(b.Def.Category, out int n);
                s.ByCategory[b.Def.Category] = n + 1;
                if (b.Condition < 1f) s.DamagedBuildings++;
            }
            for (int z = 0; z < world.Depth; z++)
            for (int x = 0; x < world.Width; x++)
                if (world.IsRoadDamaged(new GridPos(x, z))) s.DamagedRoads++;

            double happiness = 0, commute = 0, health = 0;
            int commuters = 0;
            foreach (Citizen c in e.Citizens.Values)
            {
                s.Population++;
                s.HouseholdCash += c.Cash;
                happiness += c.Happiness;
                health += c.Health;
                int age = c.AgeYears(today);
                if (age < 18) s.Children++;
                if (c.Retired) s.Retired++;
                else if (age >= 18 && age < 65) s.LabourForce++;
                if (c.JobId != 0) { s.Employed++; commute += c.CommuteHours; commuters++; }
            }
            s.AvgHappiness = s.Population == 0 ? 0 : happiness / s.Population;
            s.AvgHealth = s.Population == 0 ? 0 : health / s.Population;
            s.AvgCommuteHours = commuters == 0 ? 0 : commute / commuters;

            double wageSum = 0;
            int wageCount = 0;
            foreach (Company co in e.Companies.Values)
            {
                if (co.Closed) { s.CompaniesClosed++; continue; }
                if (!co.IsPublic) { s.CompaniesOpen++; s.CompanyCash += co.Cash; s.CompanyProfit += co.LastProfit; }
                s.Jobs += co.TargetWorkers;
                s.Vacancies += Math.Max(0, co.TargetWorkers - co.Workers.Count);
                wageSum += co.Wage * co.Workers.Count;
                wageCount += co.Workers.Count;
            }
            s.AvgWage = wageCount == 0 ? 0 : wageSum / wageCount;
            s.Treasury = e.City.Cash;
            s.TotalMoney = e.TotalMoney();

            long[] f = e.Ledger.Today;
            s.Wages = f[(int)Flow.Wages];
            s.RetailSales = f[(int)Flow.Retail];
            s.B2BSales = f[(int)Flow.B2BSales];
            s.Imports = f[(int)Flow.Imports];
            s.Exports = f[(int)Flow.Exports];
            s.Freight = f[(int)Flow.Freight];
            s.Taxes = f[(int)Flow.IncomeTax] + f[(int)Flow.CorporateTax];

            s.Births = e.Daily.Births;
            s.Deaths = e.Daily.Deaths;
            s.Immigrants = e.Daily.Immigrants;
            s.Emigrants = e.Daily.Emigrants;
            s.Moves = e.Daily.Moves;
            foreach (var kv in e.Market.Produced) s.Production[kv.Key] = kv.Value;
            foreach (var kv in e.Market.Price) s.Prices[kv.Key] = kv.Value;
            foreach (var kv in e.Market.Imported) s.Imported[kv.Key] = kv.Value;
            foreach (var kv in e.Market.Unmet) s.Unmet[kv.Key] = kv.Value;

            foreach (float v in world.TrafficVolume)
            {
                s.TrafficTotal += v;
                if (v > Transport.RoadNetwork.CellCapacityPerDay) s.CongestedCells++;
            }
            var env = world.Environment;
            s.EnergyDemand = env.EnergyDemand;
            s.EnergySupply = env.EnergySupply;
            s.AvgPollution = env.AveragePollution();
            s.Temperature = env.Temperature;
            s.Rain = env.RainToday;
            s.SoilMoisture = env.SoilMoisture;
            s.ResearchPoints = e.City.Research.PointsYesterday;
            s.TechsCompleted = e.City.Research.Completed.Count;
            return s;
        }
    }
}
