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
    /// <summary>S3: autonomous decisions — cost pass-through, investors, city government.</summary>
    public class AgentsTests
    {
        [Test]
        public void ProducersPassOnRisingCosts()
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(3));
            var r = WorldFactory.CreateRunner(w);
            r.StepMany(20 * SimClock.TicksPerDay);
            new SetPolicyCommand("electricityPrice", 100).Execute(w);
            r.StepMany(60 * SimClock.TicksPerDay);
            var pushed = w.Economy.Causes.Sum(CauseMetric.PriceLog, 0, w.Clock.TotalDays).Where(kv => kv.Key % 64 == (int)Cause.PriceCostPush).Sum(kv => kv.Value);
            Assert.Greater(pushed, 0, "some price rose because producers' costs exceeded it");
            Explanation x = Explainer.Explain(w, "price.food", 60);
            Assert.AreEqual(1.0, x.Explained, 0.1);
        }

        private static WorldState HungryVillage(bool allowInvestment)
        {
            // Residents with money but no shop anywhere: food demand goes unmet every day.
            WorldState w = TestContent.Village(6, outsideRoad: true);
            w.Economy.Ledger.Transfer(w.Economy.External, w.Economy.City, 300_000_000, Flow.Investment);
            TestContent.PlaceSouth(w, "power_plant", 30);
            new SetPolicyCommand("privateInvestment", allowInvestment ? 1 : 0).Execute(w);
            WorldFactory.CreateRunner(w).StepMany(120 * SimClock.TicksPerDay);
            return w;
        }

        [Test]
        public void InvestorsOpenShopsWhereDemandGoesUnmet()
        {
            WorldState w = HungryVillage(true);
            Company shop = w.Economy.Companies.Values.FirstOrDefault(c => c.Def.Id == "shop");
            Assert.IsNotNull(shop, "an outside investor opened a shop");
            Assert.That(w.Events.Any(e => e.Kind == "投資" && e.Message.Contains("品不足")), "with the reason logged");
            Assert.That(w.Economy.Causes.Notes.Any(n => n.Cause == Cause.InvestedForUnmetDemand && n.Agent == shop.BuildingId));
            Assert.AreEqual(0, w.Economy.TotalMoney());
        }

        [Test]
        public void InvestmentCanBeSwitchedOff()
        {
            WorldState w = HungryVillage(false);
            Assert.IsFalse(w.Economy.Companies.Values.Any(c => c.Def.Id == "shop"));
        }

        [Test]
        public void InvestorsDoNotAddToStrugglingIndustries()
        {
            WorldState w = TestContent.Village(2);
            Company plant = w.Economy.Companies[TestContent.PlaceSouth(w, "food_plant", 20)];
            foreach (Citizen c in w.Economy.Citizens.Values.Take(15)) w.Economy.Hire(plant, c);
            plant.ProfitAvg = 10_000;
            Assert.IsTrue(InvestmentSystem.IncumbentsDoingWell(w, plant.Def));
            plant.ProfitAvg = -10_000;
            Assert.IsFalse(InvestmentSystem.IncumbentsDoingWell(w, plant.Def), "losing money: imports are a cost problem, not a capacity problem");
        }

        private static WorldState GovernedTown(long mode, long cityCash)
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(7));
            new SetPolicyCommand("autoGovernment", mode).Execute(w);
            EconomyState e = w.Economy;
            if (cityCash > e.City.Cash) e.Ledger.Transfer(e.External, e.City, cityCash - e.City.Cash, Flow.Investment);
            else e.Ledger.Transfer(e.City, e.External, e.City.Cash - cityCash, Flow.Investment);
            WorldFactory.CreateRunner(w).StepMany(100 * SimClock.TicksPerDay);
            return w;
        }

        [Test]
        public void TheGovernmentIsOffByDefault()
        {
            WorldState w = GovernedTown(0, 2_000_000_000);
            Assert.AreEqual(new Policy().IncomeTax, w.Economy.City.Policy.IncomeTax);
            Assert.AreEqual(0, w.Economy.City.Policy.IndustrySubsidyPerWorker);
            Assert.IsFalse(w.Events.Any(e => e.Kind == "市政"));
        }

        [Test]
        public void ARichCityCutsTaxesOrSupportsJobsAndSaysWhy()
        {
            WorldState w = GovernedTown(1, 3_000_000_000);
            Policy p = w.Economy.City.Policy;
            Assert.That(p.IncomeTax < new Policy().IncomeTax || p.IndustrySubsidyPerWorker > 0, "a large surplus is put to use");
            WorldEvent ev = w.Events.First(e => e.Kind == "市政");
            Assert.That(ev.Message.Contains("：") && ev.Message.Length > 10, "each decision states its reason: " + ev.Message);
        }

        [Test]
        public void ABrokeCityRestoresItsFinances()
        {
            WorldState w = GovernedTown(1, 20_000_000);
            Assert.Greater(w.Economy.City.Policy.IncomeTax, new Policy().IncomeTax, "low reserves: taxes go up");
        }

        [Test]
        public void OnlyWhileLivingModeWaitsForAPlayer()
        {
            WorldState w = GovernedTown(2, 3_000_000_000);
            Assert.IsFalse(w.Events.Any(e => e.Kind == "市政"), "nobody is being lived as");
            Assert.IsFalse(GovernmentSystem.Active(w));
            w.Player.CitizenId = w.Economy.Citizens.Keys.First();
            Assert.IsTrue(GovernmentSystem.Active(w));
        }

        [Test]
        public void UnitCostOnlyCountsLossMakingProducers()
        {
            WorldState w = TestContent.Village(2);
            Company plant = w.Economy.Companies[TestContent.PlaceSouth(w, "food_plant", 20)];
            plant.LastOutput = 10; plant.LastCost = 60_000; plant.LastProfit = -1000;
            Assert.AreEqual(60_000 / (10 * 60.0), MarketSystem.UnitCost(w, "food"), 1e-9);
            plant.LastProfit = 500;
            Assert.AreEqual(0, MarketSystem.UnitCost(w, "food"), "profitable producers do not push prices");
        }
    }
}
