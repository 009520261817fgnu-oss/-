using System;
using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Commands;
using WorldSim.Core.Economy;
using WorldSim.Core.Persistence;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>S1-4: indicator changes split into causes that add up to the actual change.</summary>
    public class ExplainerTests
    {
        private static WorldState Town(int days)
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(5));
            WorldFactory.CreateRunner(w).StepMany(days * SimClock.TicksPerDay);
            return w;
        }

        [Test]
        public void EveryIndicatorIsFullyExplained()
        {
            WorldState w = Town(100);
            foreach (string m in Explainer.Metrics)
            {
                Explanation x = Explainer.Explain(w, m, 60);
                Assert.IsNotNull(x, m);
                Assert.AreEqual(60, x.ToDay - x.FromDay + 1, m);
                if (Math.Abs(x.Change) > 1e-6)
                    Assert.AreEqual(1.0, x.Explained, 0.1, $"{m}: parts {x.Parts.Sum(p => p.Value)} vs change {x.Change}");
                Assert.That(x.Parts.All(p => !string.IsNullOrEmpty(p.Label)), m);
            }
        }

        [Test]
        public void LayoffsPointAtTheCompanyResponsible()
        {
            // A village whose only employer is demolished: nobody can be rehired elsewhere. (In a town
            // with vacancies people may be rehired before the day closes, and are rightly not counted.)
            WorldState w = TestContent.Village(6, outsideRoad: true);
            int factory = TestContent.PlaceSouth(w, "factory", 20);
            var r = WorldFactory.CreateRunner(w);
            r.StepMany(5 * SimClock.TicksPerDay);
            Assert.Greater(w.Economy.Companies[factory].Workers.Count, 0);
            w.TryGetBuilding(factory, out Building b);
            new BulldozeCommand(b.Origin).Execute(w);
            r.StepMany(2 * SimClock.TicksPerDay);
            Explanation x = Explainer.Explain(w, "unemployment", 3);
            Contribution demolished = x.Parts.FirstOrDefault(p => p.Label == Explainer.CauseLabel(Economy.Cause.Demolished));
            Assert.IsNotNull(demolished, "demolition appears as a cause: " + string.Join(", ", x.Parts.Select(p => p.Label + " " + p.Value)));
            Assert.Greater(demolished.Value, 0, "it raised unemployment");
            Assert.AreEqual(factory, demolished.Agents.First().id, "and points at the demolished company");
        }

        [Test]
        public void BranchDifferencesAreExplained()
        {
            WorldState root = Town(30);
            var mgr = new BranchManager(root, WorldFactory.CreateRunner);
            Branch alt = mgr.Fork("税なし");
            new SetPolicyCommand("incomeTax", 0).Execute(alt.World);
            mgr.Advance(mgr.Active.Id, 30);
            mgr.Advance(alt.Id, 30);
            foreach (WorldState bw in new[] { root, alt.World })
            {
                StatsSnapshot s0 = bw.History.Last(s => s.Tick <= alt.ForkTick), s1 = bw.History.Last();
                Explanation e = Explainer.Explain(bw, "treasury", s0, s1);
                Assert.AreEqual(e.Change, e.Parts.Sum(p => p.Value), 1.0, bw == root ? "root" : "alt");
            }
            Explanation x = Explainer.CompareBranches(alt.World, root, "treasury", alt.ForkTick);
            Assert.IsNotNull(x);
            Assert.AreEqual(alt.World.History.Last().Treasury - root.History.Last().Treasury, x.Parts.Sum(p => p.Value), 1.0);
            Assert.AreEqual(Explainer.FlowLabel(Economy.Flow.IncomeTax), x.Parts.First().Label, "missing income tax is the main difference");
            Assert.Less(x.Parts.First().Value, 0);
        }
    }
}
