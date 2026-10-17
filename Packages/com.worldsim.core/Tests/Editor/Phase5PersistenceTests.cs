using System;
using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Commands;
using WorldSim.Core.Persistence;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>Phase 5: save/load, exact resume, history branching and comparison.</summary>
    public class Phase5PersistenceTests
    {
        private static WorldState Played(ulong seed, int days)
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(seed));
            SimulationRunner r = WorldFactory.CreateRunner(w);
            r.StepMany(days * SimClock.TicksPerDay);
            new TriggerDisasterCommand(DisasterKind.Earthquake, new GridPos(48, 48), 6.5).Execute(w);
            new SetPolicyCommand("incomeTax", 0.25).Execute(w);
            r.StepMany(5 * SimClock.TicksPerDay + 7); // save mid-day on purpose
            return w;
        }

        [Test]
        public void SaveLoadRoundTripPreservesEverything()
        {
            WorldState w = Played(3, 20);
            string json = SaveSystem.Save(w);
            WorldState loaded = SaveSystem.Load(TestContent.Load(), json);
            Assert.AreEqual(w.ComputeHash(), loaded.ComputeHash());
            Assert.AreEqual(w.History.Count, loaded.History.Count);
            Assert.AreEqual(w.History.Last().Population, loaded.History.Last().Population);
            Assert.AreEqual(w.History.Last().Prices["steel"], loaded.History.Last().Prices["steel"]);
            Assert.AreEqual(w.Events.Count, loaded.Events.Count);
            Assert.AreEqual(w.Economy.TotalMoney(), loaded.Economy.TotalMoney());
            Assert.AreEqual(0.25, loaded.Economy.City.Policy.IncomeTax);
            Assert.AreEqual(json, SaveSystem.Save(loaded), "saving the loaded world gives identical data");
        }

        [Test]
        public void LoadedGameContinuesExactlyLikeTheOriginal()
        {
            WorldState original = Played(5, 15);
            WorldState loaded = SaveSystem.Load(TestContent.Load(), SaveSystem.Save(original));
            WorldFactory.CreateRunner(original).StepMany(30 * SimClock.TicksPerDay);
            WorldFactory.CreateRunner(loaded).StepMany(30 * SimClock.TicksPerDay);
            Assert.AreEqual(original.ComputeHash(), loaded.ComputeHash());
            Assert.AreEqual(original.History.Last().TotalMoney, loaded.History.Last().TotalMoney);
        }

        [Test]
        public void InvalidSavesAreRejectedClearly()
        {
            Assert.Throws<FormatException>(() => SaveSystem.Load(TestContent.Load(), "{\"format\":\"other\"}"));
            string json = SaveSystem.Save(Played(2, 1));
            Assert.Throws<FormatException>(() => SaveSystem.Load(TestContent.Load(), json.Replace($"\"version\":{SaveSystem.Version}", "\"version\":99")));
            Assert.Throws<FormatException>(() => SaveSystem.Load(TestContent.Load(), json.Replace("\"factory\"", "\"spaceport\"")));
        }

        [Test]
        public void BranchesEvolveIndependentlyAndCanBeCompared()
        {
            WorldState root = Played(7, 10);
            var mgr = new BranchManager(root, WorldFactory.CreateRunner);
            long forkTick = root.Clock.Tick;
            Branch alt = mgr.Fork("高税率");
            Assert.AreEqual(root.ComputeHash(), alt.World.ComputeHash(), "a fork starts identical");

            new SetPolicyCommand("incomeTax", 0.45).Execute(alt.World);
            new SetPolicyCommand("unemploymentBenefit", 0).Execute(alt.World);
            mgr.Advance(mgr.Active.Id, 40);
            mgr.Advance(alt.Id, 40);

            Assert.AreEqual(root.Clock.Tick, alt.World.Clock.Tick);
            Assert.AreNotEqual(root.ComputeHash(), alt.World.ComputeHash(), "different policies, different futures");
            Assert.AreEqual(0.25, root.Economy.City.Policy.IncomeTax, "the original timeline is unaffected");
            Assert.AreEqual(forkTick, alt.ForkTick);

            var cmp = mgr.Compare("treasury");
            int forkDay = (int)(forkTick / SimClock.TicksPerDay);
            Assert.AreEqual(cmp[1][forkDay - 1], cmp[alt.Id][forkDay - 1], "shared past");
            Assert.Greater(cmp[alt.Id].Last(), cmp[1].Last(), "higher taxes, fuller treasury");

            mgr.Switch(alt.Id);
            Assert.AreSame(alt, mgr.Active);
            Assert.Throws<InvalidOperationException>(() => mgr.Remove(alt.Id));
        }

        [Test]
        public void AllBranchesSaveAndLoadTogether()
        {
            WorldState root = Played(9, 5);
            var mgr = new BranchManager(root, WorldFactory.CreateRunner);
            Branch b = mgr.Fork("防災投資");
            new SetPolicyCommand("preparedness", 5_000_000).Execute(b.World);
            mgr.Advance(b.Id, 20);
            mgr.Switch(b.Id);

            BranchManager loaded = BranchManager.LoadAll(TestContent.Load(), mgr.SaveAll(), WorldFactory.CreateRunner);
            Assert.AreEqual(2, loaded.Branches.Count);
            Assert.AreEqual(b.Id, loaded.Active.Id);
            Assert.AreEqual("防災投資", loaded.Active.Name);
            Assert.AreEqual(b.World.ComputeHash(), loaded.Active.World.ComputeHash());
            Assert.AreEqual(root.ComputeHash(), loaded.Get(1).World.ComputeHash());
            Assert.Greater(DisasterSystem.Resilience(loaded.Active.World), DisasterSystem.Resilience(loaded.Get(1).World));
        }

        [Test]
        public void EverySeriesIsAvailableForCharts()
        {
            WorldState w = Played(4, 3);
            foreach (string key in StatsSnapshot.SeriesKeys)
                Assert.DoesNotThrow(() => w.History.Last().Get(key), key);
            Assert.Greater(w.History.Last().Get("population"), 0);
        }
    }
}
