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
