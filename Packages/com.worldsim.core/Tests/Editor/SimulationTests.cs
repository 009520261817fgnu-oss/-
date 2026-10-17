using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Content;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    public class SimulationRunnerTests
    {
        private sealed class CountingSystem : ISimulationSystem
        {
            public int Calls;
            public string Name => "Counter";
            public int IntervalTicks { get; }
            public CountingSystem(int interval) { IntervalTicks = interval; }
            public void Tick(WorldState world) => Calls++;
        }

        [Test]
        public void PausedDoesNotAdvance()
        {
            var runner = new SimulationRunner(TestContent.FlatWorld());
            Assert.AreEqual(0, runner.Update(10));
            Assert.AreEqual(0, runner.World.Clock.Tick);
        }

        [Test]
        public void SpeedsMapToTickRates()
        {
            var runner = new SimulationRunner(TestContent.FlatWorld()) { Speed = SimSpeed.Normal };
            for (int i = 0; i < 60; i++) runner.Update(1.0 / 60);
            Assert.AreEqual(2, runner.World.Clock.Tick);

            runner.Speed = SimSpeed.Fast;
            runner.Update(1.0);
            Assert.AreEqual(10, runner.World.Clock.Tick);
        }

        [Test]
        public void LongFrameIsCappedAndReported()
        {
            var runner = new SimulationRunner(TestContent.FlatWorld()) { Speed = SimSpeed.VeryFast };
            int ran = runner.Update(10.0); // would be 320 ticks
            Assert.AreEqual(SimulationRunner.MaxTicksPerUpdate, ran);
            Assert.AreEqual(320 - SimulationRunner.MaxTicksPerUpdate, runner.DroppedTicks);
        }

        [Test]
        public void SystemsRunOnTheirInterval()
        {
            var runner = new SimulationRunner(TestContent.FlatWorld());
            var hourly = new CountingSystem(1);
            var daily = new CountingSystem(SimClock.TicksPerDay);
            runner.AddSystem(hourly);
            runner.AddSystem(daily);
            runner.StepMany(SimClock.TicksPerDay * 3);
            Assert.AreEqual(72, hourly.Calls);
            Assert.AreEqual(3, daily.Calls);
        }

        [Test]
        public void ClockCalendar()
        {
            var runner = new SimulationRunner(TestContent.FlatWorld());
            runner.StepMany(SimClock.TicksPerYear + 24 + 5);
            SimClock c = runner.World.Clock;
            Assert.AreEqual(2, c.Year);
            Assert.AreEqual(2, c.DayOfYear);
            Assert.AreEqual(5, c.HourOfDay);
        }

        [Test]
        public void FrameRateAndSpeedDoNotChangeResults()
        {
            // Same number of ticks reached through different frame timings must give identical state.
            WorldState a = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(5));
            WorldState b = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(5));
            var ra = WorldFactory.CreateRunner(a);
            var rb = WorldFactory.CreateRunner(b);
            ra.Speed = SimSpeed.Normal;
            for (int i = 0; i < 24 * 30; i++) ra.Update(1.0 / 60); // 24 ticks
            rb.Speed = SimSpeed.VeryFast;
            rb.Update(0.75); // 24 ticks in one frame
            Assert.AreEqual(24, a.Clock.Tick);
            Assert.AreEqual(a.Clock.Tick, b.Clock.Tick);
            Assert.AreEqual(a.ComputeHash(), b.ComputeHash());
        }

        [Test]
        public void StatsHistoryRecordsDailyAndSeesEdits()
        {
            WorldState w = TestContent.FlatWorld();
            SimulationRunner runner = WorldFactory.CreateRunner(w);
            var stats = runner.GetSystem<StatsHistorySystem>();
            runner.StepMany(24);
            w.TryPlaceBuilding("factory", new GridPos(2, 2));
            runner.StepMany(24);
            Assert.AreEqual(2, stats.History.Count);
            Assert.AreEqual(0, stats.History[0].Buildings);
            Assert.AreEqual(1, stats.History[1].Buildings);
            Assert.AreEqual(1, stats.History[1].ByCategory[BuildingCategory.Industrial]);
        }
    }

    public class WorldFactoryTests
    {
        [Test]
        public void SameSeedGivesIdenticalWorld()
        {
            ulong a = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(11)).ComputeHash();
            ulong b = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(11)).ComputeHash();
            ulong c = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(12)).ComputeHash();
            Assert.AreEqual(a, b);
            Assert.AreNotEqual(a, c);
        }

        [Test]
        public void TerrainHasSeaSouthAndHigherNorth()
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), new WorldGenOptions { Seed = 3, BuildStartingTown = false });
            Terrain t = w.Terrain;
            int southWater = 0, northWater = 0;
            for (int x = 0; x < t.Width; x++)
            {
                if (t.IsCellUnderwater(new GridPos(x, 0))) southWater++;
                if (t.IsCellUnderwater(new GridPos(x, t.Depth - 1))) northWater++;
            }
            Assert.Greater(southWater, t.Width / 2, "south edge should be mostly sea");
            Assert.AreEqual(0, northWater);
            float centre = t.GetVertexHeight(t.Width / 2, t.Depth / 2);
            Assert.That(centre, Is.InRange(t.SeaLevel + 2, t.SeaLevel + 12), "central plain");
        }

        [TestCase(1UL)]
        [TestCase(2UL)]
        [TestCase(77UL)]
        public void StartingTownHasEveryFunction(ulong seed)
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), new WorldGenOptions { Seed = seed });
            StatsSnapshot s = StatsHistorySystem.Capture(w);
            Assert.Greater(w.RoadCellCount, 100);
            foreach (BuildingCategory c in System.Enum.GetValues(typeof(BuildingCategory)))
                Assert.That(s.ByCategory.ContainsKey(c), $"seed {seed}: no {c} building");
            Assert.IsTrue(s.ByCategory.TryGetValue(BuildingCategory.Residential, out int homes) && homes >= 20);
            foreach (Building b in w.Buildings)
                if (b.Def.Category != BuildingCategory.Agriculture)
                    Assert.IsTrue(w.HasRoadAccess(b.Footprint), $"{b.Def.Id} #{b.Id} has no road");
        }
    }
}
