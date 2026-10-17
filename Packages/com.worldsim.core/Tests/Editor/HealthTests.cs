using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>S2-3: industry → pollution → health → mortality, productivity, happiness; clinics restore health.</summary>
    public class HealthTests
    {
        /// <summary>Homes next to a busy steel mill, with or without a clinic. Returns residents' mean health.</summary>
        private static (double health, WorldState w) PollutedVillage(bool clinic, int days)
        {
            WorldState w = TestContent.Village(6, outsideRoad: true);
            w.Economy.Ledger.Transfer(w.Economy.External, w.Economy.City, 500_000_000, Flow.Investment);
            int mill = TestContent.PlaceSouth(w, "steel_mill", 3);
            TestContent.PlaceSouth(w, "power_plant", 9);
            if (clinic) TestContent.PlaceSouth(w, "clinic", 30);
            Company co = w.Economy.Companies[mill];
            WorldFactory.CreateRunner(w).StepMany(SimClock.TicksPerDay);
            var r = WorldFactory.CreateRunner(w);
            for (int d = 0; d < days; d++)
            {
                co.AddStock("iron_ore", 1000);
                r.StepMany(SimClock.TicksPerDay);
            }
            return (w.Economy.Citizens.Values.Average(c => c.Health), w);
        }

        [Test]
        public void PollutedAirWearsHealthDownAndClinicsHelp()
        {
            var (dirty, w) = PollutedVillage(false, 60);
            var (treated, _) = PollutedVillage(true, 60);
            Assert.Less(dirty, HealthSystem.Initial, "living next to a steel mill harms health");
            Assert.Greater(treated, dirty, "a clinic within reach restores health");
            Assert.Less(w.History.Last().AvgHealth, HealthSystem.Initial);
        }

        [Test]
        public void IllHealthRaisesMortalityAndLowersProductivity()
        {
            var healthy = new Citizen { Health = 1f };
            var ill = new Citizen { Health = 0.3f };
            Assert.Greater(HealthSystem.MortalityFactor(ill), HealthSystem.MortalityFactor(healthy) * 2);

            WorldState w = TestContent.Village(2, outsideRoad: true);
            int factory = TestContent.PlaceSouth(w, "factory", 20);
            Company co = w.Economy.Companies[factory];
            foreach (Citizen c in w.Economy.Citizens.Values.Take(10)) w.Economy.Hire(co, c);
            double normal = HealthSystem.ProductivityFactor(w, co);
            foreach (int id in co.Workers) w.Economy.Citizens[id].Health = 0.3f;
            Assert.Less(HealthSystem.ProductivityFactor(w, co), normal * 0.8);
        }

        [Test]
        public void HealthSurvivesSaveAndLoad()
        {
            var (_, w) = PollutedVillage(false, 10);
            WorldState loaded = Persistence.SaveSystem.Load(TestContent.Load(), Persistence.SaveSystem.Save(w));
            Assert.AreEqual(w.Economy.Citizens.Values.Sum(c => (double)c.Health), loaded.Economy.Citizens.Values.Sum(c => (double)c.Health), 1e-4);
        }
    }
}
