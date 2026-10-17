using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Commands;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>Phase 4: technology, policy, energy, pollution, weather, disasters and recovery (scenarios C, D).</summary>
    public class Phase4SystemsTests
    {
        private static void Research(WorldState w, string tech) => w.Economy.City.Research.Completed.Add(tech);

        [Test]
        public void LabsResearchTheChosenTechnology()
        {
            WorldState w = TestContent.Village(3, outsideRoad: true);
            foreach (var c in w.Economy.Citizens.Values) c.Education = 2; // labs hire graduates
            TestContent.PlaceSouth(w, "research_lab", 4);
            int plant = TestContent.PlaceSouth(w, "power_plant", 10);
            // A full-size plant cannot live off one small village's bills; fund it for the test's duration.
            w.Economy.Ledger.Transfer(w.Economy.External, w.Economy.Companies[plant], 100_000_000, Flow.Investment);
            TestContent.PlaceSouth(w, "shop", 16);
            Assert.IsFalse(new SetResearchCommand("automation").Execute(w).Success, "prerequisite not done");
            Assert.IsTrue(new SetResearchCommand("traffic_signals").Execute(w).Success);
            SimulationRunner r = WorldFactory.CreateRunner(w);
            r.StepMany(SimClock.TicksPerDay);
            double perDay = w.Economy.City.Research.PointsYesterday;
            Assert.Greater(perDay, 0);
            int days = (int)System.Math.Ceiling(w.Content.GetTech("traffic_signals").Cost / perDay) + 5;
            r.StepMany(days * SimClock.TicksPerDay);
            Assert.That(w.Economy.City.Research.Completed, Does.Contain("traffic_signals"), $"perDay={perDay} days={days} progress={w.Economy.City.Research.Progress} current={w.Economy.City.Research.Current} now={w.Economy.City.Research.PointsYesterday} completed={string.Join(",", w.Economy.City.Research.Completed)}");
            Assert.That(w.Events.Any(e => e.Kind == "技術"));
        }

        [Test]
        public void CityWideTechSpeedsUpRoads()
        {
            WorldState w = TestContent.Village(2);
            int a = TestContent.PlaceSouth(w, "shop", 4), b = TestContent.PlaceSouth(w, "shop", 40);
            float before = w.Network.TravelHours(a, b);
            Research(w, "traffic_signals");
            w.Network.Invalidate();
            Assert.Less(w.Network.TravelHours(a, b), before);
        }

        [Test]
        public void CompaniesAdoptTechChangingOutputJobsAndEnergy()
        {
            // Scenario C: research -> adoption -> capacity, staffing, energy use change.
            WorldState w = TestContent.Village(6);
            int f = TestContent.PlaceSouth(w, "factory", 4);
            TestContent.PlaceSouth(w, "power_plant", 10);
            Company co = w.Economy.Companies[f];
            co.AddStock("steel", 5000);
            co.AddStock("wood", 10000);
            SimulationRunner r = WorldFactory.CreateRunner(w);
            r.StepMany(2 * SimClock.TicksPerDay);
            int jobsBefore = w.Effects.JobCapacity(co);
            double perWorkerBefore = co.LastOutput / co.Workers.Count;
            double energyPerWorkerBefore = co.EnergyUse / co.Workers.Count;

            Research(w, "efficient_motors");
            Research(w, "automation");
            w.Economy.Ledger.Transfer(w.Economy.External, co, 50_000_000, Flow.Investment);
            r.StepMany(2 * SimClock.TicksPerDay);

            Assert.That(co.AdoptedTechs, Does.Contain("automation"));
            Assert.Less(w.Effects.JobCapacity(co), jobsBefore, "automation needs fewer workers");
            Assert.LessOrEqual(co.Workers.Count, w.Effects.JobCapacity(co));
            Assert.Greater(co.LastOutput / co.Workers.Count, perWorkerBefore * 1.3, "output per worker rises");
            Assert.AreNotEqual(energyPerWorkerBefore, co.EnergyUse / co.Workers.Count, "energy use changes");
            Assert.AreEqual(0, w.Economy.TotalMoney());
        }

        [Test]
        public void TechUnlocksBuildings()
        {
            WorldState w = TestContent.FlatWorld();
            Assert.IsFalse(w.TryPlaceBuilding("solar_farm", new GridPos(2, 2)).Success);
            Research(w, "efficient_motors");
            Research(w, "solar_power");
            Assert.IsTrue(w.TryPlaceBuilding("solar_farm", new GridPos(2, 2)).Success);
        }

        [Test]
        public void PolicyChangesAreValidatedAndTakeEffect()
        {
            WorldState low = TestContent.Village(4), high = TestContent.Village(4);
            Assert.IsFalse(new SetPolicyCommand("incomeTax", 0.9).Execute(low).Success);
            Assert.IsFalse(new SetPolicyCommand("noSuchPolicy", 1).Execute(low).Success);
            Assert.IsTrue(new SetPolicyCommand("incomeTax", 0.05).Execute(low).Success);
            Assert.IsTrue(new SetPolicyCommand("incomeTax", 0.30).Execute(high).Success);
            Assert.IsTrue(new SetPolicyCommand("industrySubsidy", 3000).Execute(high).Success);
            foreach (WorldState w in new[] { low, high })
            {
                TestContent.PlaceSouth(w, "factory", 4);
                TestContent.PlaceSouth(w, "power_plant", 10);
                WorldFactory.CreateRunner(w).StepMany(3 * SimClock.TicksPerDay);
            }
            Assert.Greater(high.History.Last().Taxes, low.History.Last().Taxes * 3);
            Assert.Greater(high.Economy.Ledger.Yesterday[(int)Flow.Subsidy], 0);
            Assert.AreEqual(0, low.Economy.Ledger.Yesterday[(int)Flow.Subsidy]);
        }

        [Test]
        public void PowerShortageCutsProduction()
        {
            WorldState w = TestContent.Village(6);
            int f = TestContent.PlaceSouth(w, "factory", 4);
            int plant = TestContent.PlaceSouth(w, "power_plant", 10);
            Company co = w.Economy.Companies[f];
            co.AddStock("steel", 5000);
            co.AddStock("wood", 10000);
            SimulationRunner r = WorldFactory.CreateRunner(w);
            r.StepMany(SimClock.TicksPerDay);
            Assert.AreEqual(1.0, w.Environment.EnergyRatio, 1e-9);
            double powered = co.LastOutput;
            Assert.Greater(powered, 0);

            w.TryRemoveBuilding(plant);
            r.StepMany(SimClock.TicksPerDay);
            Assert.AreEqual(0, w.Environment.EnergyRatio);
            Assert.AreEqual(0, co.LastOutput, "no electricity, no production");
            Assert.That(CityAnalytics.Diagnose(w).Any(x => x.Title.StartsWith("電力不足")));
        }

        [Test]
        public void IndustryPollutesNearbyAndScrubbersHelp()
        {
            float Run(bool scrubbers)
            {
                WorldState w = TestContent.Village(6);
                int mill = TestContent.PlaceSouth(w, "steel_mill", 30);
                TestContent.PlaceSouth(w, "power_plant", 20);
                Company co = w.Economy.Companies[mill];
                co.AddStock("iron_ore", 100000);
                if (scrubbers) { Research(w, "scrubbers"); co.AdoptedTechs.Add("scrubbers"); }
                WorldFactory.CreateRunner(w).StepMany(10 * SimClock.TicksPerDay);
                float near = w.Environment.PollutionAt(new GridPos(31, 7));
                Assert.Greater(near, w.Environment.PollutionAt(new GridPos(2, 40)), "pollution is highest near the source");
                return near;
            }
            float dirty = Run(false), clean = Run(true);
            Assert.Greater(dirty, 0);
            Assert.Less(clean, dirty * 0.7f);
        }

        [Test]
        public void SoilMoistureDrivesFarmYields()
        {
            double Yield(float moisture)
            {
                WorldState w = TestContent.Village(2);
                int farm = TestContent.PlaceSouth(w, "farm", 4);
                TestContent.PlaceSouth(w, "power_plant", 10);
                LaborSystem.Match(w, int.MaxValue);
                w.Environment.SoilMoisture = moisture;
                w.Economy.Companies[farm].BrokenDays = 0;
                return ProductionSystem.Efficiency(w, w.Economy.Companies[farm], w.Buildings.First(b => b.Id == farm));
            }
            Assert.Greater(Yield(0.8f), Yield(0.05f) * 2, "drought cuts the harvest");
        }

        [Test]
        public void WeatherVariesDeterministically()
        {
            WorldState a = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(2));
            WorldState b = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(2));
            WorldFactory.CreateRunner(a).StepMany(120 * SimClock.TicksPerDay);
            WorldFactory.CreateRunner(b).StepMany(120 * SimClock.TicksPerDay);
            Assert.That(a.History.Select(s => s.Rain), Is.EqualTo(b.History.Select(s => s.Rain)));
            Assert.Greater(a.History.Count(s => s.Rain > 0), 10);
            Assert.Greater(a.History.Max(s => s.Temperature) - a.History.Min(s => s.Temperature), 10, "seasons");
        }

        [Test]
        public void FloodDamagesLowLyingBuildingsAndRoads()
        {
            WorldState w = TestContent.Village(2);
            int low = TestContent.PlaceSouth(w, "shop", 20);
            for (int z = 0; z <= 48; z++) for (int x = 18; x <= 24; x++) w.Terrain.SetVertexHeight(x, z, 11f); // just above sea (10)
            w.TryGetBuilding(low, out Building lowB);
            new TriggerDisasterCommand(DisasterKind.Flood, default, 2.5).Execute(w);
            Assert.Less(lowB.Condition, 1f, "the low shop flooded");
            Assert.AreEqual(1f, w.Buildings.First(b => b.Def.Id == "apartment").Condition, "higher ground stays dry");
            Assert.IsTrue(w.IsRoadDamaged(new GridPos(20, 10)) || w.IsRoadDamaged(new GridPos(21, 10)));
            Assert.IsFalse(w.IsRoadUsable(new GridPos(20, 10)) && w.IsRoadDamaged(new GridPos(20, 10)));
        }

        [Test]
        public void EarthquakeDamageFallsWithDistanceAndResilience()
        {
            float Damage(bool seismic, int x)
            {
                WorldState w = TestContent.Village(0);
                int near = TestContent.PlaceSouth(w, "shop", x);
                if (seismic) Research(w, "seismic_design");
                DisasterSystem.Earthquake(w, new GridPos(4, 8), 7.5);
                w.TryGetBuilding(near, out Building b);
                return 1 - b.Condition;
            }
            Assert.Greater(Damage(false, 4), Damage(false, 44), "closer to the epicentre is worse");
            Assert.Greater(Damage(false, 4), Damage(true, 4), "seismic design reduces damage");
        }

        [Test]
        public void DamageDisruptsProductionAndRepairsRestoreIt()
        {
            // Scenario D: damage -> lower capacity -> repair over days, faster with recovery stations.
            int DaysToRecover(bool fireStations, out double outputWhileDamaged)
            {
                WorldState w = TestContent.Village(6);
                int f = TestContent.PlaceSouth(w, "factory", 4);
                TestContent.PlaceSouth(w, "power_plant", 10);
                if (fireStations) { TestContent.PlaceSouth(w, "fire_station", 16); TestContent.PlaceSouth(w, "fire_station", 19); }
                Company co = w.Economy.Companies[f];
                co.AddStock("steel", 20000);
                co.AddStock("wood", 40000);
                SimulationRunner r = WorldFactory.CreateRunner(w);
                r.StepMany(SimClock.TicksPerDay);
                double normal = co.LastOutput;
                foreach (Building b in w.Buildings.ToList()) w.SetCondition(b, 0.3f);
                r.StepMany(SimClock.TicksPerDay);
                outputWhileDamaged = co.LastOutput / normal;
                for (int day = 1; day <= 60; day++)
                {
                    r.StepMany(SimClock.TicksPerDay);
                    if (w.Buildings.All(b => b.Condition >= 1f)) return day;
                }
                return 999;
            }
            int slow = DaysToRecover(false, out double damagedOutput);
            int fast = DaysToRecover(true, out _);
            Assert.Less(damagedOutput, 0.5, "damaged factory produces far less");
            Assert.Less(slow, 999, "the city eventually repairs everything");
            Assert.Less(fast, slow, "recovery stations speed up repairs");
        }

        [Test]
        public void PreparednessSpendingBuildsResilience()
        {
            WorldState w = TestContent.Village(2);
            double before = DisasterSystem.Resilience(w);
            new SetPolicyCommand("preparedness", 5_000_000).Execute(w);
            WorldFactory.CreateRunner(w).StepMany(60 * SimClock.TicksPerDay);
            Assert.Greater(DisasterSystem.Resilience(w), before + 0.2);
            Assert.AreEqual(0, w.Economy.TotalMoney());
        }
    }
}
