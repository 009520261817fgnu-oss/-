using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Commands;
using WorldSim.Core.Economy;
using WorldSim.Core.Persistence;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>S1-3: every change is booked under a cause, and the totals add up exactly to the statistics.</summary>
    public class CauseLogTests
    {
        private static WorldState Town(int days, ulong seed = 1)
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(seed));
            WorldFactory.CreateRunner(w).StepMany(days * SimClock.TicksPerDay);
            return w;
        }

        private static long Sum(WorldState w, CauseMetric m, long from, long to) => w.Economy.Causes.Sum(m, from, to).Values.Sum();

        [Test]
        public void PeopleTotalsMatchTheStatisticsExactly()
        {
            WorldState w = Town(120);
            var h = w.History;
            StatsSnapshot a = h[h.Count - 61], b = h[h.Count - 1];
            long fromDay = a.Tick / SimClock.TicksPerDay + 1, toDay = b.Tick / SimClock.TicksPerDay;
            Assert.AreEqual(b.Population - a.Population, Sum(w, CauseMetric.Population, fromDay, toDay), "population");
            Assert.AreEqual(b.LabourForce - a.LabourForce, Sum(w, CauseMetric.LabourForce, fromDay, toDay), "labour force");
            Assert.AreEqual((b.LabourForce - b.Employed) - (a.LabourForce - a.Employed), Sum(w, CauseMetric.Unemployed, fromDay, toDay), "unemployed");
            long births = h.Skip(h.Count - 60).Sum(s => s.Births), deaths = h.Skip(h.Count - 60).Sum(s => s.Deaths);
            Assert.AreEqual(births, w.Economy.Causes.Sum(CauseMetric.Population, fromDay, toDay).GetValueOrDefault((int)Cause.Born));
            Assert.AreEqual(-deaths, w.Economy.Causes.Sum(CauseMetric.Population, fromDay, toDay).GetValueOrDefault((int)Cause.Died));
        }

        [Test]
        public void MoneyAndPricesAddUpExactly()
        {
            WorldState w = Town(90);
            var h = w.History;
            StatsSnapshot a = h[h.Count - 31], b = h[h.Count - 1];
            long fromDay = a.Tick / SimClock.TicksPerDay + 1, toDay = b.Tick / SimClock.TicksPerDay;
            Assert.AreEqual(b.Treasury - a.Treasury, Sum(w, CauseMetric.CityMoney, fromDay, toDay), "treasury");
            Assert.AreEqual(b.HouseholdCash - a.HouseholdCash, Sum(w, CauseMetric.HouseholdMoney, fromDay, toDay), "household cash");
            Assert.AreEqual(b.CompanyCash - a.CompanyCash, Sum(w, CauseMetric.CompanyMoney, fromDay, toDay), "company cash");
            int index = w.Content.Resources.ToList().FindIndex(r => r.Id == "food");
            double logChange = Math.Log(b.Prices["food"] / a.Prices["food"]);
            long booked = w.Economy.Causes.Sum(CauseMetric.PriceLog, fromDay, toDay).Where(kv => kv.Key / 64 == index).Sum(kv => kv.Value);
            Assert.AreEqual(logChange, booked / 1e6, 1e-4, "food price");
        }

        [Test]
        public void LayoffsCarryTheirReason()
        {
            WorldState w = TestContent.Village(6, outsideRoad: true);
            int factory = TestContent.PlaceSouth(w, "factory", 20);
            SimulationRunner r = WorldFactory.CreateRunner(w);
            r.StepMany(3 * SimClock.TicksPerDay);
            int staff = w.Economy.Companies[factory].Workers.Count;
            Assert.Greater(staff, 0);
            w.TryGetBuilding(factory, out Building building);
            new BulldozeCommand(building.Origin).Execute(w);
            r.StepMany(SimClock.TicksPerDay);
            long today = w.Clock.TotalDays;
            var lost = w.Economy.Causes.Sum(CauseMetric.JobsLost, today - 1, today);
            Assert.AreEqual(staff, lost.GetValueOrDefault((int)Cause.Demolished));
            Assert.That(w.Economy.Causes.Notes.Any(n => n.Cause == Cause.Demolished && n.Agent == factory));
        }

        [Test]
        public void CauseLogSurvivesSaveAndLoadAndIsBounded()
        {
            WorldState w = Town(30);
            WorldState loaded = SaveSystem.Load(TestContent.Load(), SaveSystem.Save(w));
            Assert.AreEqual(w.Economy.Causes.Days.Count, loaded.Economy.Causes.Days.Count);
            Assert.AreEqual(w.Economy.Causes.Notes.Count, loaded.Economy.Causes.Notes.Count);
            WorldFactory.CreateRunner(w).StepMany(10 * SimClock.TicksPerDay);
            WorldFactory.CreateRunner(loaded).StepMany(10 * SimClock.TicksPerDay);
            Assert.AreEqual(SaveSystem.Save(w), SaveSystem.Save(loaded), "the cause log continues identically after loading");

            WorldState old = Town(CauseLog.KeepDays / 3);
            WorldFactory.CreateRunner(old).StepMany((CauseLog.KeepDays + 20) * SimClock.TicksPerDay);
            Assert.LessOrEqual(old.Economy.Causes.Days.Count, CauseLog.KeepDays);
            long oldest = old.Economy.Causes.Notes.Min(n => n.Tick);
            Assert.GreaterOrEqual(oldest, (old.Clock.TotalDays - CauseLog.KeepNoteDays - 1) * SimClock.TicksPerDay);
        }
    }
}
