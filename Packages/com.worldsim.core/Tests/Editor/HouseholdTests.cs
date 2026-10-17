using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>S4-1: couples, births to couples, families moving and leaving together, children leaving home.</summary>
    public class HouseholdTests
    {
        [Test]
        public void CouplesFormAmongNeighboursAndStayConsistent()
        {
            WorldState w = TestContent.Village(6, outsideRoad: true);
            TestContent.PlaceSouth(w, "power_plant", 30);
            Assert.AreEqual(0, w.Economy.Citizens.Values.Count(c => c.PartnerId != 0));
            WorldFactory.CreateRunner(w).StepMany(200 * SimClock.TicksPerDay);
            var partnered = w.Economy.Citizens.Values.Where(c => c.PartnerId != 0).ToList();
            Assert.Greater(partnered.Count, 10, "couples formed");
            foreach (Citizen c in partnered)
            {
                Citizen p = w.Economy.Citizens[c.PartnerId];
                Assert.AreEqual(c.Id, p.PartnerId, "partnership is mutual");
            }
            Assert.IsEmpty(WorldValidator.Validate(w));
            Assert.That(w.Economy.Causes.Notes.Any(n => n.Cause == Cause.Married));
        }

        [Test]
        public void ChildrenAreBornToCouples()
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), new WorldGenOptions { Seed = 1 });
            var r = WorldFactory.CreateRunner(w);
            int births = 0;
            for (int d = 0; d < 365; d++)
            {
                r.StepMany(SimClock.TicksPerDay);
                foreach (Citizen b in w.Economy.Citizens.Values.Where(c => c.BirthDay == w.Clock.TotalDays - 1 || c.BirthDay == w.Clock.TotalDays))
                {
                    if (b.ParentId == 0 || !w.Economy.Citizens.TryGetValue(b.ParentId, out Citizen parent)) continue;
                    births++;
                    Assert.AreNotEqual(0, parent.PartnerId, "the parent lives in a couple");
                }
            }
            Assert.Greater(births, 0);
        }

        [Test]
        public void FamiliesLeaveTownTogether()
        {
            WorldState w = TestContent.Village(1, outsideRoad: true);
            var adults = w.Economy.Citizens.Values.Take(2).ToList();
            Households.Marry(w.Economy, adults[0], adults[1]);
            var child = new Citizen { Id = w.AllocateId(), BirthDay = w.Clock.TotalDays - 5 * 360, ParentId = adults[0].Id, SchoolDays = 0 };
            w.Economy.Citizens.Add(child.Id, child);
            w.Economy.MoveIn(child, adults[0].HomeId);
            foreach (Citizen a in adults) { a.UnemployedDays = 400; a.Happiness = 0.1f; }
            var r = WorldFactory.CreateRunner(w);
            for (int d = 0; d < 200 && w.Economy.Citizens.ContainsKey(adults[0].Id) && w.Economy.Citizens.ContainsKey(adults[1].Id); d++)
                r.StepMany(SimClock.TicksPerDay);
            bool a0 = w.Economy.Citizens.ContainsKey(adults[0].Id), a1 = w.Economy.Citizens.ContainsKey(adults[1].Id);
            Assert.IsFalse(a0 && a1, "someone left");
            Assert.AreEqual(a0, a1, "the couple left together");
            Assert.IsFalse(w.Economy.Citizens.ContainsKey(child.Id), "with their child");
            Assert.That(w.Economy.Causes.Notes.Any(n => n.Cause == Cause.FamilyFollowed));
        }

        [Test]
        public void GrownChildrenMoveOut()
        {
            WorldState w = TestContent.Village(4, outsideRoad: true);
            new Commands.SetPolicyCommand("rent", 0).Execute(w); // no rent-driven moves in this test
            Citizen parent = w.Economy.Citizens.Values.First();
            var adultChild = new Citizen { Id = w.AllocateId(), BirthDay = w.Clock.TotalDays - 22 * 360, ParentId = parent.Id, SchoolDays = -1, Cash = 0 };
            w.Economy.Citizens.Add(adultChild.Id, adultChild);
            w.Economy.Ledger.Transfer(w.Economy.External, adultChild, 500_000, Flow.Migration);
            int home = parent.HomeId;
            Assert.IsTrue(w.TryPlaceBuilding("apartment", new GridPos(14, 11)).Success, "an empty building to move into");
            w.Economy.MoveIn(adultChild, home);
            w.Economy.Hire(w.Economy.Companies[TestContent.PlaceSouth(w, "town_hall", 30)], adultChild); // employed: no reason to leave town
            var r = WorldFactory.CreateRunner(w);
            for (int d = 0; d < 1500 && adultChild.HomeId == home; d++) r.StepMany(SimClock.TicksPerDay);
            Assert.AreNotEqual(home, adultChild.HomeId, "the grown child found a home of their own");
            Assert.That(w.Economy.Causes.Notes.Any(n => n.Citizen == adultChild.Id && n.Cause == Cause.LeftParentsHome),
                string.Join(",", w.Economy.Causes.Notes.Where(n => n.Citizen == adultChild.Id).Select(n => n.Cause + "@" + n.Tick / 24)) + $" home={adultChild.HomeId} was={home} parentHome={parent.HomeId}");
        }
    }
}
