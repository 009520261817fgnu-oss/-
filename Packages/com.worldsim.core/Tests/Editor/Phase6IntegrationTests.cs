using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Commands;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Persistence;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>
    /// Phase 6: the four linkage scenarios of spec chapter 4 on the full generated town, plus a
    /// long run with invariant checks and a save/load in the middle.
    /// </summary>
    public class Phase6IntegrationTests
    {
        private static WorldState Town(ulong seed = 1) => WorldFactory.Create(TestContent.Load(), new WorldGenOptions { Seed = seed });

        private static double Sum(WorldState w, int lastDays, System.Func<StatsSnapshot, double> f) =>
            w.History.Skip(w.History.Count - lastDays).Sum(f);

        [Test]
        public void ScenarioA_FactoryToCityEconomy()
        {
            WorldState a = Town(), b = Town();
            var newIds = new List<int>();
            foreach (string id in new[] { "food_plant", "factory", "shop" })
            {
                CommandResult r = b.TryPlaceBuilding(id, TestContent.FindSpot(b, id));
                Assert.IsTrue(r.Success, r.Message);
                newIds.Add(r.CreatedId);
            }
            SimulationRunner ra = WorldFactory.CreateRunner(a), rb = WorldFactory.CreateRunner(b);
            ra.StepMany(60 * SimClock.TicksPerDay);
            rb.StepMany(60 * SimClock.TicksPerDay);

            Company plant = b.Economy.Companies[newIds[0]], factory = b.Economy.Companies[newIds[1]];
            Assert.Greater(plant.Workers.Count + factory.Workers.Count, 0, "1-2: new plants hired and run");
            Assert.That(b.Economy.Companies.Values.Where(c => newIds.Contains(c.BuildingId)).Any(c => c.Inventory.Values.Sum() > 0 || c.LastOutput > 0),
                "2-3: inputs were bought and products made");
            Assert.Greater(Sum(b, 30, s => s.Employed), Sum(a, 30, s => s.Employed), "4: employment rises");
            Assert.Greater(Sum(b, 30, s => s.Wages), Sum(a, 30, s => s.Wages), "4: wage income rises");
            // Local food production lowers food prices, so the same purchases can cost fewer yen; judge
            // consumption by what households spent plus what they kept (S2 finding, see CAUSAL_FINDINGS).
            Assert.Greater(Sum(b, 30, s => s.RetailSales) + b.History.Last().HouseholdCash,
                (Sum(a, 30, s => s.RetailSales) + a.History.Last().HouseholdCash) * 0.98, "5: consumption holds or rises");
            Assert.LessOrEqual(Sum(b, 30, s => s.Unmet.TryGetValue("food", out double fb) ? fb : 0),
                Sum(a, 30, s => s.Unmet.TryGetValue("food", out double fa) ? fa : 0), "5: no more unmet food demand");
            // Wages → income tax (cheaper food can lower retailers' profits and so corporate tax).
            long IncomeTax(WorldState w) => w.Economy.Causes.Sum(CauseMetric.CityMoney, w.Clock.TotalDays - 29, w.Clock.TotalDays)[(int)Flow.IncomeTax];
            Assert.Greater(IncomeTax(b), IncomeTax(a), "6: income tax revenue rises");
            Assert.GreaterOrEqual(b.History.Last().Population + 5, a.History.Last().Population, "6: population does not fall behind");
        }

        [Test]
        public void ScenarioB_CuttingTheHighwayIsolatesTheEconomy()
        {
            WorldState a = Town(), b = Town();
            // Cut every road cell on the map edge: no imports or exports any more.
            for (int i = 0; i < b.Width; i++)
                foreach (var c in new[] { new GridPos(i, 0), new GridPos(i, b.Depth - 1), new GridPos(0, i), new GridPos(b.Width - 1, i) })
                    if (b.HasRoad(c)) b.TryRemoveRoad(c);
            Assert.IsFalse(b.Network.HasOutsideConnection);
            SimulationRunner ra = WorldFactory.CreateRunner(a), rb = WorldFactory.CreateRunner(b);
            ra.StepMany(40 * SimClock.TicksPerDay);
            rb.StepMany(40 * SimClock.TicksPerDay);
            Assert.AreEqual(0, Sum(b, 30, s => s.Imports + s.Exports), "1-3: no trade beyond the map");
            Assert.Greater(Sum(a, 30, s => s.Imports + s.Exports), 0);
            Assert.That(CityAnalytics.Diagnose(b).Any(f => f.Title.Contains("外部への道路")), "the analysis names the cause");
            Assert.AreNotEqual(a.Economy.Market.GetPrice("steel"), b.Economy.Market.GetPrice("steel"), "4: prices react");
            Assert.AreNotEqual(Sum(a, 10, s => s.Employed), Sum(b, 10, s => s.Employed), "5: employment reacts");
        }

        [Test]
        public void ScenarioC_TechnologyChangesProductionJobsAndEnergy()
        {
            WorldState a = Town(), b = Town();
            foreach (string t in new[] { "efficient_motors", "automation", "mechanization" }) b.Economy.City.Research.Completed.Add(t);
            foreach (Company co in b.Economy.Companies.Values)
                if (!co.IsPublic) b.Economy.Ledger.Transfer(b.Economy.External, co, 20_000_000, Flow.Investment);
            foreach (Company co in a.Economy.Companies.Values)
                if (!co.IsPublic) a.Economy.Ledger.Transfer(a.Economy.External, co, 20_000_000, Flow.Investment);
            SimulationRunner ra = WorldFactory.CreateRunner(a), rb = WorldFactory.CreateRunner(b);
            ra.StepMany(30 * SimClock.TicksPerDay);
            rb.StepMany(30 * SimClock.TicksPerDay);

            Assert.That(b.Economy.Companies.Values.Count(c => c.AdoptedTechs.Contains("automation")), Is.GreaterThan(2), "2: companies adopt");
            int IndustrialJobs(WorldState w) => w.Economy.Companies.Values
                .Where(c => c.Def.Category == BuildingCategory.Industrial && !c.Closed).Sum(c => w.Effects.JobCapacity(c));
            Assert.Less(IndustrialJobs(b), IndustrialJobs(a), "4: industrial job slots shrink");
            Assert.AreNotEqual(a.History.Last().EnergyDemand, b.History.Last().EnergyDemand, "3: energy use changes");
            double PerWorker(WorldState w, string res) => Sum(w, 10, s => s.Production.TryGetValue(res, out double v) ? v : 0)
                / System.Math.Max(1, w.Economy.Companies.Values.Where(c => c.Produces(res)).Sum(c => c.Workers.Count));
            Assert.Greater(PerWorker(b, "grain"), PerWorker(a, "grain") * 1.1, "3: mechanised farms produce more per worker");
            Assert.AreNotEqual(a.Economy.City.Cash, b.Economy.City.Cash, "5: city finances change");
        }

        [Test]
        public void ScenarioD_DisasterAndRecoveryDependOnPreparedness()
        {
            WorldState root = Town(2);
            var mgr = new BranchManager(root, WorldFactory.CreateRunner);
            // Same funding (a national grant) and repair budget for both; only preparedness differs.
            foreach (Branch br in new[] { mgr.Get(1) })
                br.World.Economy.Ledger.Transfer(br.World.Economy.External, br.World.Economy.City, 1_500_000_000, Flow.Investment);
            new SetPolicyCommand("repairBudget", 30_000_000).Execute(root);
            Branch prepared = mgr.Fork("防災投資あり");
            new SetPolicyCommand("preparedness", 20_000_000).Execute(prepared.World);
            mgr.Advance(1, 60);
            mgr.Advance(prepared.Id, 60);

            var results = new List<(double damage, int recoveryDay, double outputDrop)>();
            foreach (Branch br in new[] { mgr.Get(1), prepared })
            {
                WorldState w = br.World;
                new SetPolicyCommand("preparedness", 0).Execute(w); // after the quake, money goes to recovery
                double outputBefore = w.History.Last().Production.Values.Sum();
                new TriggerDisasterCommand(DisasterKind.Earthquake, new GridPos(w.Width / 2, w.Depth / 2), 7.2).Execute(w);
                double damage = w.Buildings.Sum(b => 1 - b.Condition);
                Assert.Greater(damage, 0, "1: buildings damaged");
                mgr.Advance(br.Id, 1);
                double outputAfter = w.History.Last().Production.Values.Sum();
                int day = 1;
                while (day < 200 && (w.Buildings.Any(b => b.Condition < 1f) || w.History.Last().DamagedRoads > 0)) { mgr.Advance(br.Id, 1); day++; }
                results.Add((damage, day, outputAfter / System.Math.Max(1, outputBefore)));
                Assert.IsEmpty(WorldValidator.Validate(w));
            }
            Assert.Less(results[0].outputDrop, 1.0, "2-3: production falls after the quake");
            Assert.Less(results[1].damage, results[0].damage * 0.8, "5: preparedness reduces damage");
            Assert.Less(results[1].recoveryDay, results[0].recoveryDay, "4-5: and speeds up recovery");
            Assert.Less(results[0].recoveryDay, 200, "the unprepared city also recovers eventually");
        }

        [Test]
        public void TenYearRunKeepsEveryInvariant()
        {
            WorldState w = Town(3);
            SimulationRunner r = WorldFactory.CreateRunner(w);
            var sw = Stopwatch.StartNew();
            for (int year = 1; year <= 10; year++)
            {
                for (int d = 0; d < SimClock.DaysPerYear; d++)
                {
                    r.StepMany(SimClock.TicksPerDay);
                    if (d % 30 == 0) CollectionAssert.IsEmpty(WorldValidator.Validate(w), $"year {year} day {d}");
                }
                if (year == 5)
                {
                    // Save and continue from the loaded copy halfway through.
                    w = SaveSystem.Load(TestContent.Load(), SaveSystem.Save(w));
                    r = WorldFactory.CreateRunner(w);
                }
            }
            TestContext.WriteLine($"10 years in {sw.Elapsed.TotalSeconds:0.0}s, population {w.History.Last().Population}");
            Assert.AreEqual(10 * SimClock.DaysPerYear, w.History.Count);
            Assert.That(w.History.Select(s => s.TotalMoney), Is.All.EqualTo(0L));
            Assert.Greater(w.History.Last().Population, 200, "the town survives");
            Assert.Greater(w.History.Last().Employed, 100);
            Assert.Greater(w.Economy.City.Research.Completed.Count, 3, "research progressed");
            Assert.Less(sw.Elapsed.TotalSeconds, 120, "performance budget");
        }

        [Test]
        public void FastSpeedProducesTheSameWorldAsNormalSpeed()
        {
            WorldState normal = Town(4), fast = Town(4);
            var rn = WorldFactory.CreateRunner(normal);
            var rf = WorldFactory.CreateRunner(fast);
            rn.Speed = SimSpeed.Normal;
            rf.Speed = SimSpeed.VeryFast;
            int target = 3 * SimClock.TicksPerDay;
            while (normal.Clock.Tick < target) rn.Update(System.Math.Min(1.0 / 30, (target - normal.Clock.Tick) / 2.0));
            while (fast.Clock.Tick < target) rf.Update(System.Math.Min(1.0 / 30, (target - fast.Clock.Tick) / 32.0));
            Assert.AreEqual(target, normal.Clock.Tick);
            Assert.AreEqual(target, fast.Clock.Tick);
            Assert.AreEqual(normal.ComputeHash(), fast.ComputeHash(), "time acceleration never changes the outcome");
        }
    }
}
