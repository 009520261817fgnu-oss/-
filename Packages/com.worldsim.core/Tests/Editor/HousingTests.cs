using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>S2-2: rent from land value, rent in household budgets and home choice, eviction.</summary>
    public class HousingTests
    {
        [Test]
        public void RentIsCollectedAndCutsSpendingMoney()
        {
            WorldState w = TestContent.Village(4, outsideRoad: true);
            TestContent.PlaceSouth(w, "power_plant", 20);
            var r = WorldFactory.CreateRunner(w);
            long cityBefore = w.Economy.City.Cash;
            r.StepMany(2 * SimClock.TicksPerDay);
            long rent = w.Economy.Citizens.Values.Sum(c => c.LastRent);
            Assert.Greater(rent, 0);
            Citizen adult = w.Economy.Citizens.Values.First(c => c.LastRent > 0);
            Assert.AreEqual(HousingMarket.RentFor(w, adult.HomeId), adult.LastRent);
            Assert.That(w.Economy.Causes.Days.Any(d => d.Get(CauseMetric.CityMoney, (int)Flow.Rent) > 0), "the city receives rent");
            Assert.AreEqual(0, w.Economy.TotalMoney());
        }

        [Test]
        public void RentFollowsLandValue()
        {
            WorldState w = TestContent.Village(2, outsideRoad: true);
            Building[] homes = w.Buildings.Where(b => b.Def.Housing > 0).OrderBy(b => b.Id).ToArray();
            w.SetCondition(homes[1], 0.6f); // damage lowers land value
            Assert.Greater(HousingMarket.LandValue(w, homes[0].Id), HousingMarket.LandValue(w, homes[1].Id));
            Assert.Greater(HousingMarket.RentFor(w, homes[0].Id), HousingMarket.RentFor(w, homes[1].Id));
            new Commands.SetPolicyCommand("rent", 900).Execute(w);
            Assert.AreEqual(3, HousingMarket.RentFor(w, homes[0].Id) / (double)(900 / 3 * (0.5 + HousingMarket.LandValue(w, homes[0].Id) / 200)), 0.05);
        }

        [Test]
        public void PoorHouseholdsWeighRentMoreThanRichOnes()
        {
            WorldState w = TestContent.Village(2, outsideRoad: true);
            Building[] homes = w.Buildings.Where(b => b.Def.Housing > 0).OrderBy(b => b.Id).ToArray();
            w.SetCondition(homes[1], 0.6f);
            new Commands.SetPolicyCommand("rent", 3000).Execute(w);
            var poor = new Citizen { LastNetIncome = 2000 };
            var rich = new Citizen { LastNetIncome = 30000 };
            float poorGap = PopulationSystem.HomeScore(w, homes[1], poor) - PopulationSystem.HomeScore(w, homes[0], poor);
            float richGap = PopulationSystem.HomeScore(w, homes[1], rich) - PopulationSystem.HomeScore(w, homes[0], rich);
            Assert.Greater(poorGap, richGap, "the cheaper (worse) home appeals more to the poor household");
            Assert.Less(richGap, 0, "the rich household prefers the better location");
        }

        [Test]
        public void UnpaidRentEndsInEviction()
        {
            WorldState w = TestContent.Village(1, outsideRoad: true); // no employers: nobody earns
            Citizen c = w.Economy.Citizens.Values.First();
            w.Economy.Ledger.Transfer(c, w.Economy.External, c.Cash, Flow.Migration);
            new Commands.SetPolicyCommand("unemploymentBenefit", 0).Execute(w);
            var r = WorldFactory.CreateRunner(w);
            int home = c.HomeId;
            for (int d = 0; d < HousingMarket.EvictionDays + 2 && c.HomeId == home; d++) r.StepMany(SimClock.TicksPerDay);
            Assert.AreNotEqual(home, c.HomeId, "evicted after a month of arrears");
            Assert.That(w.Economy.Causes.Notes.Any(n => n.Citizen == c.Id && n.Cause == Cause.Evicted));
            r.StepMany(20 * SimClock.TicksPerDay);
            Assert.IsFalse(w.Economy.Citizens.ContainsKey(c.Id), "with no money and no home, they leave town");
        }
    }
}
