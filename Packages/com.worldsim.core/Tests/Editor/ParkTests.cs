using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.People;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>S4-2: parks raise happiness and land value nearby, and are open to residents at any hour.</summary>
    public class ParkTests
    {
        private static WorldState Village(bool park)
        {
            WorldState w = TestContent.Village(6, outsideRoad: true);
            TestContent.PlaceSouth(w, "power_plant", 36);
            if (park) TestContent.PlaceSouth(w, "park", 8);
            WorldFactory.CreateRunner(w).StepMany(40 * SimClock.TicksPerDay);
            return w;
        }

        [Test]
        public void ParksRaiseHappinessAndLandValue()
        {
            WorldState with = Village(true), without = Village(false);
            Assert.Greater(with.Economy.Citizens.Values.Average(c => c.Happiness), without.Economy.Citizens.Values.Average(c => c.Happiness));
            Building home = with.Buildings.First(b => b.Def.Housing > 0);
            Building same = without.Buildings.First(b => b.Def.Housing > 0);
            Assert.Greater(HousingMarket.LandValue(with, home.Id), HousingMarket.LandValue(without, same.Id));
            Assert.IsNotEmpty(PopulationSystem.ParkNeighbourhood(with));
        }

        [Test]
        public void ParksAreOpenToEveryoneAtNight()
        {
            WorldState w = TestContent.Village(1, outsideRoad: true);
            int park = TestContent.PlaceSouth(w, "park", 8);
            w.TryGetBuilding(park, out Building b);
            var r = WorldFactory.CreateRunner(w);
            while (w.Clock.HourOfDay != 23) r.Step();
            Assert.IsNull(EnterBuildingCommand.AccessDenied(w, w.Economy.Citizens.Values.First(), b));
        }
    }
}
