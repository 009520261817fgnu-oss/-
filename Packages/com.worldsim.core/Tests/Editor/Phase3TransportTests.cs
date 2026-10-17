using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.Transport;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>Phase 3: road network, traffic, freight cost, prices, migration and analysis (scenario B).</summary>
    public class Phase3TransportTests
    {
        /// <summary>
        /// 48×48 flat world: main road along z=10 (x 2..45) plus a long northern detour
        /// (2,10)→(2,45)→(45,45)→(45,10). Cutting (25,10) forces traffic onto the detour.
        /// </summary>
        private static WorldState LoopVillage(bool cutMainRoad, int westHomes = 3, bool outside = false)
        {
            WorldState w = TestContent.FlatWorld(48);
            w.TryBuildRoad(new GridPos(outside ? 0 : 2, 10), new GridPos(45, 10));
            w.TryBuildRoad(new GridPos(2, 11), new GridPos(2, 45));
            w.TryBuildRoad(new GridPos(3, 45), new GridPos(45, 45));
            w.TryBuildRoad(new GridPos(45, 44), new GridPos(45, 11));
            for (int i = 0; i < westHomes; i++) w.TryPlaceBuilding("apartment", new GridPos(4 + i * 3, 11));
            foreach (Building b in w.Buildings.ToList())
                for (int k = 0; k < b.Def.Housing; k++)
                {
                    var c = new Citizen { Id = w.AllocateId(), BirthDay = -30 * 360, Education = 1, SchoolDays = -1 };
                    w.Economy.Citizens.Add(c.Id, c);
                    w.Economy.MoveIn(c, b.Id);
                    w.Economy.Ledger.Transfer(w.Economy.External, c, 300_000, Flow.Migration);
                }
            TestContent.PlaceSouth(w, "power_plant", 14);
            if (cutMainRoad) w.TryRemoveRoad(new GridPos(25, 10));
            return w;
        }

        [Test]
        public void CuttingARoadForcesALongerRoute()
        {
            WorldState a = LoopVillage(false), b = LoopVillage(true);
            int ma = TestContent.PlaceSouth(a, "mine", 4), sa = TestContent.PlaceSouth(a, "steel_mill", 38);
            int mb = TestContent.PlaceSouth(b, "mine", 4), sb = TestContent.PlaceSouth(b, "steel_mill", 38);
            float direct = a.Network.TravelHours(ma, sa), detour = b.Network.TravelHours(mb, sb);
            Assert.Less(direct, float.PositiveInfinity);
            Assert.Greater(detour, direct * 2, "the detour via z=45 is much longer");
            List<int> route = b.Network.RouteCells(mb, sb);
            Assert.That(route.Any(cell => cell / b.Width == 45), "route uses the northern road");
        }

        [Test]
        public void CongestionSlowsTravel()
        {
            WorldState w = LoopVillage(false);
            int m = TestContent.PlaceSouth(w, "mine", 4), s = TestContent.PlaceSouth(w, "steel_mill", 38);
            float free = w.Network.TravelHours(m, s);
            for (int x = 2; x <= 45; x++) w.CongestionBasis[10 * w.Width + x] = RoadNetwork.CellCapacityPerDay * 2;
            w.Network.Invalidate();
            float jammed = w.Network.TravelHours(m, s);
            Assert.Greater(jammed, free * 1.3f, "with the main road jammed, travel slows (or reroutes)");
        }

        [Test]
        public void CommutersLoadTheRoadsTheyUse()
        {
            WorldState w = LoopVillage(false);
            int office = TestContent.PlaceSouth(w, "office", 18);
            SimulationRunner r = WorldFactory.CreateRunner(w);
            r.StepMany(3 * SimClock.TicksPerDay);
            Assert.Greater(w.Economy.Companies[office].Workers.Count, 0);
            float onRoute = w.TrafficVolume[10 * w.Width + 15];
            float unused = w.TrafficVolume[10 * w.Width + 40];
            Assert.Greater(onRoute, 0, "cells between homes and office carry commuters");
            Assert.AreEqual(0f, unused, "cells past the office carry nothing");
        }

        [Test]
        public void LongerRouteRaisesFreightCostAndLowersProfit()
        {
            // Scenario B steps 1-4: road change -> travel time -> freight cost -> company profit.
            WorldState a = LoopVillage(false), b = LoopVillage(true);
            foreach (WorldState w in new[] { a, b })
            {
                TestContent.PlaceSouth(w, "mine", 4);
                TestContent.PlaceSouth(w, "steel_mill", 38);
            }
            SimulationRunner ra = WorldFactory.CreateRunner(a), rb = WorldFactory.CreateRunner(b);
            ra.StepMany(3 * SimClock.TicksPerDay);
            rb.StepMany(3 * SimClock.TicksPerDay);
            long freightA = a.History.Sum(s => s.Freight), freightB = b.History.Sum(s => s.Freight);
            Assert.Greater(freightA, 0, "ore moves by road");
            Assert.Greater(freightB, freightA * 1.5, "the detour costs more freight");
            Company millA = a.Economy.Companies.Values.First(c => c.Def.Id == "steel_mill");
            Company millB = b.Economy.Companies.Values.First(c => c.Def.Id == "steel_mill");
            Assert.Less(millB.Cash, millA.Cash, "the same ore cost the mill more, leaving it less cash");
        }

        [Test]
        public void LongCommutesMakeResidentsMoveCloserToWork()
        {
            // Scenario B steps 5-6: homes in the west, jobs in the east, main road cut.
            WorldState w = LoopVillage(true, westHomes: 3);
            int office = TestContent.PlaceSouth(w, "office", 40);
            for (int x = 30; x <= 42; x += 3) w.TryPlaceBuilding("apartment", new GridPos(x, 11)); // empty homes near work
            SimulationRunner r = WorldFactory.CreateRunner(w);
            r.StepMany(2 * SimClock.TicksPerDay);
            double before = w.Economy.Citizens.Values.Where(c => c.JobId == office).Average(c => c.CommuteHours);
            Assert.Greater(before, PopulationSystem.MoveCommuteHours, "the detour makes commutes long");
            r.StepMany(90 * SimClock.TicksPerDay);
            Assert.Greater(w.History.Sum(s => s.Moves), 0, "some residents moved");
            double after = w.Economy.Citizens.Values.Where(c => c.JobId != 0).Average(c => c.CommuteHours);
            Assert.Less(after, before, "average commute fell after moving");
        }

        [Test]
        public void ShortagesRaisePricesAndSurplusesLowerThem()
        {
            WorldState w = TestContent.Village(4, outsideRoad: true);
            TestContent.PlaceSouth(w, "factory", 4);   // needs steel: only importable
            TestContent.PlaceSouth(w, "farm", 10);     // grain: nobody local buys it
            TestContent.PlaceSouth(w, "power_plant", 16);
            WorldFactory.CreateRunner(w).StepMany(20 * SimClock.TicksPerDay);
            Market m = w.Economy.Market;
            Assert.Greater(m.GetPrice("steel"), w.Content.GetResource("steel").BasePrice * 1.1, "imported input gets dearer locally");
            Assert.Less(m.GetPrice("grain"), w.Content.GetResource("grain").BasePrice * 0.9, "exported surplus gets cheaper locally");
        }

        [Test]
        public void TaxesAndBenefitsFollowPolicy()
        {
            WorldState w = TestContent.Village(4);
            TestContent.PlaceSouth(w, "office", 4);
            TestContent.PlaceSouth(w, "power_plant", 10);
            SimulationRunner r = WorldFactory.CreateRunner(w);
            r.StepMany(2 * SimClock.TicksPerDay);
            StatsSnapshot s = w.History.Last();
            long incomeTax = w.Economy.Ledger.Yesterday[(int)Flow.IncomeTax];
            Assert.AreEqual(s.Wages * w.Economy.City.Policy.IncomeTax, incomeTax, s.Wages * 0.01);
            int unemployed = w.Economy.Citizens.Values.Count(c => c.JobId == 0);
            Assert.Greater(unemployed, 0, "more residents than office jobs");
            Assert.AreEqual(unemployed * w.Economy.City.Policy.UnemploymentBenefit, w.Economy.Ledger.Yesterday[(int)Flow.Benefit]);
        }

        [Test]
        public void VacanciesAttractImmigrantsAndJoblessnessDrivesEmigration()
        {
            WorldState jobs = TestContent.Village(0);
            TestContent.PlaceSouth(jobs, "office", 4);
            TestContent.PlaceSouth(jobs, "power_plant", 10);
            for (int x = 20; x <= 38; x += 3) jobs.TryPlaceBuilding("apartment", new GridPos(x, 11)); // empty homes
            WorldFactory.CreateRunner(jobs).StepMany(15 * SimClock.TicksPerDay);
            Assert.Greater(jobs.History.Sum(s => s.Immigrants), 0);

            WorldState idle = TestContent.Village(4); // homes but no employers
            WorldFactory.CreateRunner(idle).StepMany(90 * SimClock.TicksPerDay);
            Assert.Greater(idle.History.Sum(s => s.Emigrants), 0);
            Assert.AreEqual(0, idle.History.Sum(s => s.Immigrants));
        }

        [Test]
        public void DiagnosisNamesCausesAndTrendsAreLinear()
        {
            WorldState idle = TestContent.Village(4);
            WorldFactory.CreateRunner(idle).StepMany(2 * SimClock.TicksPerDay);
            List<Finding> f = CityAnalytics.Diagnose(idle);
            Assert.That(f.Any(x => x.Title.Contains("外部への道路")));
            Assert.That(f.Any(x => x.Title.StartsWith("失業率")));
            Assert.AreEqual(Severity.Critical, f[0].Severity, "most severe first");

            var history = new List<StatsSnapshot>();
            for (int i = 0; i < 40; i++) history.Add(new StatsSnapshot { Population = 100 + 3 * i });
            Assert.AreEqual(3.0, CityAnalytics.Trend(history, "population", 30), 1e-9);
            Assert.AreEqual(100 + 3 * 39 + 30, CityAnalytics.Forecast(history, "population", 10), 1e-6);
        }

        [Test]
        public void LandValueReflectsAccessAndPollution()
        {
            WorldState w = TestContent.Village(3);
            TestContent.PlaceSouth(w, "shop", 4);
            CommandResult far = w.TryPlaceBuilding("house_small", new GridPos(40, 40)); // no road
            Dictionary<int, double> lv = CityAnalytics.LandValues(w);
            int nearHome = w.Buildings.First(b => b.Def.Id == "apartment").Id;
            Assert.Greater(lv[nearHome], lv[far.CreatedId]);
            Assert.AreEqual(0, lv[far.CreatedId]);
        }
    }
}
