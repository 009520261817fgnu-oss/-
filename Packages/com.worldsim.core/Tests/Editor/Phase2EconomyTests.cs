using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>Phase 2: residents, companies, production chain, employment, logistics.</summary>
    public class Phase2EconomyTests
    {
        [Test]
        public void MoneyIsConservedEveryDay()
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(4));
            Assert.AreEqual(0, w.Economy.TotalMoney(), "initial endowments come from the external account");
            SimulationRunner r = WorldFactory.CreateRunner(w);
            r.StepMany(60 * SimClock.TicksPerDay);
            Assert.AreEqual(60, w.History.Count);
            Assert.That(w.History.Select(s => s.TotalMoney), Is.All.EqualTo(0L));
            Assert.That(w.Economy.Citizens.Values.Select(c => c.Cash), Is.All.GreaterThanOrEqualTo(0L));
        }

        [Test]
        public void InitialTownIsPopulatedAndStaffed()
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), new WorldGenOptions { Seed = 1 });
            StatsSnapshot s = StatsHistorySystem.Capture(w);
            Assert.Greater(s.Population, 500);
            Assert.Greater(s.Employed, s.LabourForce * 0.7, "most of the labour force starts employed");
            Assert.IsTrue(w.Network.HasOutsideConnection, "a highway links the town to the map edge");
            foreach (string id in new[] { "farm", "mine", "forestry", "steel_mill", "food_plant", "factory", "shop", "office", "power_plant" })
                Assert.That(w.Economy.Companies.Values.Any(c => c.Def.Id == id), $"no {id}");
            foreach (WorldState world in new[] { w, WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(8)) })
            {
                WorldFactory.CreateRunner(world).StepMany(SimClock.TicksPerDay);
                Assert.AreEqual(1.0, world.Environment.EnergyRatio, 1e-9, "power plants are staffed and cover demand");
            }
        }

        [Test]
        public void NewFactoryCreatesJobsAndIncome()
        {
            // Scenario A, first half: same world with and without one extra factory.
            WorldState a = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(8));
            WorldState b = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(8));
            GridPos spot = TestContent.FindSpot(b, "food_plant");
            CommandResult built = b.TryPlaceBuilding("food_plant", spot);
            Assert.IsTrue(built.Success, built.Message);

            SimulationRunner ra = WorldFactory.CreateRunner(a), rb = WorldFactory.CreateRunner(b);
            Company plant = b.Economy.Companies[built.CreatedId];
            double plantOutput = 0;
            for (int day = 0; day < 10; day++)
            {
                ra.StepMany(SimClock.TicksPerDay);
                rb.StepMany(SimClock.TicksPerDay);
                plantOutput += plant.LastOutput;
            }

            Assert.Greater(plant.Workers.Count, 0, "the new plant hired");
            Assert.Greater(b.History.Last().Employed, a.History.Last().Employed, "more people are employed");
            Assert.Greater(b.History.Sum(s => s.Wages), a.History.Sum(s => s.Wages), "more wages were paid");
            Assert.Greater(plantOutput, 0, $"the new plant bought grain and produced food (grain={plant.Stock("grain")} shortage={plant.InputShortage} outside={b.Network.HasOutsideConnection} eff={b.Environment.EnergyRatio} broken={plant.BrokenDays} workers={plant.Workers.Count} cap={plant.OutputCapacity} grainprice={b.Economy.Market.GetPrice("grain")} power={string.Join(",", b.Economy.Companies.Values.Where(c => c.PowerOutput > 0).Select(c => $"{c.Workers.Count}/{c.TargetWorkers} closed={c.Closed} cond={(b.TryGetBuilding(c.BuildingId, out var pb) ? pb.Condition : -1)}"))} supply={b.Environment.EnergySupply} demand={b.Environment.EnergyDemand})");
        }

        [Test]
        public void ThreeStageChainRunsLocally()
        {
            // farm(grain) -> food_plant(food) -> shop -> residents, all in one village without imports.
            WorldState w = TestContent.Village(6);
            int farm = TestContent.PlaceSouth(w, "farm", 2);
            int plant = TestContent.PlaceSouth(w, "food_plant", 8);
            int shop = TestContent.PlaceSouth(w, "shop", 13);
            TestContent.PlaceSouth(w, "power_plant", 17);
            SimulationRunner r = WorldFactory.CreateRunner(w);
            r.StepMany(15 * SimClock.TicksPerDay);

            EconomyState e = w.Economy;
            Assert.Greater(e.Companies[farm].Workers.Count, 0);
            Assert.Greater(w.History.Sum(s => s.Production.TryGetValue("grain", out double g) ? g : 0), 0);
            Assert.Greater(w.History.Sum(s => s.Production.TryGetValue("food", out double f) ? f : 0), 0, "plant turned grain into food");
            Assert.Greater(w.History.Sum(s => s.RetailSales), 0, "residents bought at the shop");
            Assert.That(e.Citizens.Values.Average(c => c.FoodMet), Is.GreaterThan(0.5));
            Assert.AreEqual(0, w.History.Sum(s => s.Imports), "no outside connection, so no imports");
            Assert.That(w.History.Select(s => s.TotalMoney), Is.All.EqualTo(0L));
            Assert.IsFalse(e.Companies[plant].Closed);
            Assert.IsFalse(e.Companies[shop].Closed);
        }

        [Test]
        public void MissingInputsStopProductionUntilSupplied()
        {
            WorldState w = TestContent.Village(4);
            int factory = TestContent.PlaceSouth(w, "factory", 2);
            TestContent.PlaceSouth(w, "power_plant", 8);
            SimulationRunner r = WorldFactory.CreateRunner(w);
            Company co = w.Economy.Companies[factory];
            r.StepMany(2 * SimClock.TicksPerDay);
            Assert.Greater(co.Workers.Count, 0);
            Assert.IsTrue(co.InputShortage, "no steel or wood anywhere");
            Assert.AreEqual(0, co.LastOutput);

            co.AddStock("steel", 500);
            co.AddStock("wood", 1000);
            r.StepMany(SimClock.TicksPerDay);
            Assert.Greater(co.LastOutput, 0, "production resumes once inputs arrive");
            Assert.Greater(co.Stock("goods"), 0);
        }

        [Test]
        public void BusinessWithoutRoadAccessCannotHire()
        {
            WorldState w = TestContent.Village(4);
            CommandResult r = w.TryPlaceBuilding("shop", new GridPos(30, 30)); // far from any road
            Assert.IsTrue(r.Success);
            w.Economy.Companies[r.CreatedId].Workers.Clear();
            LaborSystem.Match(w, int.MaxValue);
            Assert.AreEqual(0, w.Economy.Companies[r.CreatedId].Workers.Count);
        }

        [Test]
        public void ImportsNeedAnOutsideConnection()
        {
            WorldState closed = TestContent.Village(4, outsideRoad: false);
            WorldState open = TestContent.Village(4, outsideRoad: true);
            foreach (WorldState w in new[] { closed, open })
            {
                TestContent.PlaceSouth(w, "factory", 4);
                TestContent.PlaceSouth(w, "power_plant", 10);
                WorldFactory.CreateRunner(w).StepMany(3 * SimClock.TicksPerDay);
            }
            Assert.IsFalse(closed.Network.HasOutsideConnection);
            Assert.IsTrue(open.Network.HasOutsideConnection);
            Assert.AreEqual(0, closed.History.Sum(s => s.Imports));
            Assert.Greater(open.History.Sum(s => s.Imports), 0, "the factory imports steel and wood");
        }

        [Test]
        public void UnpaidPayrollLeadsToBankruptcy()
        {
            WorldState w = TestContent.Village(4);
            int shop = TestContent.PlaceSouth(w, "shop", 4);
            Company co = w.Economy.Companies[shop];
            w.Economy.Ledger.Transfer(co, w.Economy.External, co.Cash, Flow.Divestment); // broke
            co.Inventory.Clear();
            SimulationRunner r = WorldFactory.CreateRunner(w);
            r.StepMany((FinanceSystem.DistressDaysToClose + 2) * SimClock.TicksPerDay);
            Assert.IsTrue(co.Closed);
            Assert.AreEqual(0, co.Workers.Count);
            Assert.That(w.Events.Any(ev => ev.Kind == "倒産"));
            Assert.AreEqual(0, w.Economy.TotalMoney());
        }

        [Test]
        public void ConstructionIsPaidFromTreasury()
        {
            WorldState w = TestContent.FlatWorld();
            long before = w.Economy.City.Cash;
            Assert.IsTrue(w.TryPlaceBuilding("factory", new GridPos(2, 2)).Success);
            Assert.AreEqual(before - w.Content.GetBuilding("factory").Cost - EconomyState.CompanyStartCapital * 0, w.Economy.City.Cash);
            w.Economy.Ledger.Transfer(w.Economy.City, w.Economy.External, w.Economy.City.Cash, Flow.Construction);
            CommandResult r = w.TryPlaceBuilding("factory", new GridPos(10, 10));
            Assert.IsFalse(r.Success);
            StringAssert.Contains("予算不足", r.Message);
            Assert.IsFalse(w.TryBuildRoad(new GridPos(0, 20), new GridPos(5, 20)).Success);
        }

        [Test]
        public void DemolishingAnEmployerReleasesWorkersAndKeepsMoneyBalanced()
        {
            WorldState w = TestContent.Village(4);
            int shop = TestContent.PlaceSouth(w, "office", 4);
            LaborSystem.Match(w, int.MaxValue);
            int[] staff = w.Economy.Companies[shop].Workers.ToArray();
            Assert.IsNotEmpty(staff);
            w.TryRemoveBuilding(shop);
            Assert.IsFalse(w.Economy.Companies.ContainsKey(shop));
            Assert.That(staff.Select(id => w.Economy.Citizens[id].JobId), Is.All.EqualTo(0));
            Assert.AreEqual(0, w.Economy.TotalMoney());
        }

        [Test]
        public void DemolishingHomesMakesResidentsHomeless()
        {
            WorldState w = TestContent.Village(2);
            Building home = w.Buildings.First(b => b.Def.Housing > 0);
            int residents = w.Economy.Residents(home.Id);
            Assert.Greater(residents, 0);
            w.TryRemoveBuilding(home.Id);
            Assert.AreEqual(residents, w.Economy.Citizens.Values.Count(c => c.HomeId == 0));
        }

        [Test]
        public void ContentCrossReferencesAreValidated()
        {
            Assert.Throws<System.FormatException>(() => ContentDatabase.FromJson(
                "{\"recipes\":[{\"id\":\"r\",\"outputs\":{\"unobtainium\":1}}]}"));
            Assert.Throws<System.FormatException>(() => ContentDatabase.FromJson(
                "{\"buildings\":[{\"id\":\"b\",\"category\":\"Industrial\",\"recipe\":\"nope\"}]}"));
            Assert.Throws<System.FormatException>(() => ContentDatabase.FromJson(
                "{\"techs\":[{\"id\":\"t\",\"cost\":10,\"prereqs\":[\"missing\"]}]}"));
        }
    }
}
