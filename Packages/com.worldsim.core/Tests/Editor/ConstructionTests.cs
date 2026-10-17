using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>S2-1: building, upkeep and repair work goes to the local construction industry when there is one.</summary>
    public class ConstructionTests
    {
        private static (WorldState w, SimulationRunner r, Company builder) VillageWithBuilder(bool builder)
        {
            WorldState w = TestContent.Village(6, outsideRoad: true);
            w.Economy.Ledger.Transfer(w.Economy.External, w.Economy.City, 500_000_000, Flow.Investment);
            Company co = null;
            if (builder) co = w.Economy.Companies[TestContent.PlaceSouth(w, "construction", 20)];
            TestContent.PlaceSouth(w, "power_plant", 26);
            var r = WorldFactory.CreateRunner(w);
            r.StepMany(3 * SimClock.TicksPerDay);
            return (w, r, co);
        }

        [Test]
        public void LocalContractorsTakeTheWorkAndHireForIt()
        {
            var (w, r, builder) = VillageWithBuilder(true);
            Assert.Greater(builder.Workers.Count, 0, "the contractor hired local people");
            long external = w.Economy.External.Cash, before = builder.Backlog;
            long local = w.Economy.PayForWork(w.Economy.City, 2_000_000, Flow.Construction);
            Assert.AreEqual(2_000_000, local, "all of it stays in town");
            Assert.AreEqual(external, w.Economy.External.Cash);
            Assert.AreEqual(before + 2_000_000, builder.Backlog);
            r.StepMany(5 * SimClock.TicksPerDay);
            Assert.Less(builder.Backlog, before + 2_000_000, "the work gets done");
            Assert.That(w.Economy.Causes.Days.Any(d => d.Get(CauseMetric.ExternalMoney, (int)Flow.Imports) > 0 || d.Get(CauseMetric.CompanyMoney, (int)Flow.B2BSales) != 0),
                "materials were bought");
        }

        [Test]
        public void WithoutContractorsTheMoneyLeavesTown()
        {
            var (w, _, _) = VillageWithBuilder(false);
            long external = w.Economy.External.Cash;
            Assert.AreEqual(0, w.Economy.PayForWork(w.Economy.City, 2_000_000, Flow.Construction));
            Assert.AreEqual(external + 2_000_000, w.Economy.External.Cash);
        }

        [Test]
        public void OrderBooksAreLimitedByCapacity()
        {
            var (w, _, builder) = VillageWithBuilder(true);
            long capacity = EconomyState.WorkCapacity(builder) * EconomyState.ContractorBacklogDays - builder.Backlog;
            long local = w.Economy.PayForWork(w.Economy.City, capacity + 5_000_000, Flow.Construction);
            Assert.AreEqual(capacity, local, "work beyond 30 days of capacity goes to outside firms");
            Assert.AreEqual(0, w.Economy.TotalMoney());
        }

        [Test]
        public void MaterialShortagesSlowTheWork()
        {
            var (w, _, builder) = VillageWithBuilder(true);
            builder.Inventory.Clear();
            builder.Backlog = 1_000_000;
            ProductionSystem.DoContractWork(builder);
            Assert.AreEqual(1_000_000, builder.Backlog, "no materials, no progress");
            Assert.IsTrue(builder.InputShortage);
            builder.AddStock("steel", 100);
            builder.AddStock("wood", 400);
            ProductionSystem.DoContractWork(builder);
            Assert.Less(builder.Backlog, 1_000_000);
        }
    }
}
