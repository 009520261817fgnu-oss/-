using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>S2-4: business services are an optional input — offices gain business customers, industry is more productive with them.</summary>
    public class ServicesTests
    {
        private static (WorldState w, Company food, Company office) Run(bool office)
        {
            WorldState w = TestContent.Village(6);
            int plant = TestContent.PlaceSouth(w, "food_plant", 20);
            TestContent.PlaceSouth(w, "power_plant", 26);
            Company of = null;
            if (office) of = w.Economy.Companies[TestContent.PlaceSouth(w, "office", 32)];
            Company co = w.Economy.Companies[plant];
            var r = WorldFactory.CreateRunner(w);
            for (int d = 0; d < 6; d++) { co.AddStock("grain", 2000); r.StepMany(SimClock.TicksPerDay); }
            return (w, co, of);
        }

        [Test]
        public void IndustryRunsWithoutServicesButBetterWithThem()
        {
            var (_, without, _) = Run(false);
            var (w, with, office) = Run(true);
            Assert.Greater(without.LastOutput, 0, "services are not essential: production continues");
            Assert.That(with.Inventory.ContainsKey("services") || w.Economy.Causes.Days.Exists(d => d.Get(CauseMetric.CompanyMoney, (int)Flow.B2BSales) != 0),
                "the plant bought services from the office");
            Assert.Greater(with.LastOutput / with.Workers.Count, without.LastOutput / without.Workers.Count * 1.05, "more output per worker with services");
            Assert.Greater(office.Workers.Count, 0);
        }
    }
}
