using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Persistence;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>S5-4: regions, per-region random streams and save v4.</summary>
    public class RegionTests
    {
        [Test]
        public void OneRegionGameIsExactlyTheOldGame()
        {
            WorldGenOptions o = TestContent.SmallOptions(11);
            WorldState old = WorldFactory.Create(TestContent.Load(), o);
            WorldFactory.CreateRunner(old).StepMany(20 * SimClock.TicksPerDay);

            Atlas atlas = Atlas.Create(TestContent.Load(), o, 1);
            new AtlasRunner(atlas, WorldFactory.CreateRunner).StepMany(20 * SimClock.TicksPerDay);
            Assert.AreEqual(1, atlas.Regions[0].World.RegionId);
            Assert.AreEqual(old.ComputeHash(), atlas.Regions[0].World.ComputeHash());
        }

        [Test]
        public void RegionsHaveTheirOwnRandomStreams()
        {
            Assert.AreEqual(7UL, Atlas.RegionSeed(7, 1));
            Assert.AreNotEqual(Atlas.RegionSeed(7, 2), Atlas.RegionSeed(7, 3));
            Assert.AreEqual(Atlas.RegionSeed(7, 2), Atlas.RegionSeed(7, 2));

            WorldGenOptions o = TestContent.SmallOptions(11);
            Atlas a = Atlas.Create(TestContent.Load(), o, 2), b = Atlas.Create(TestContent.Load(), o, 2);
            Assert.AreNotEqual(a.Get(1).World.ComputeHash(), a.Get(2).World.ComputeHash(), "different maps and people");
            // An intervention in region 2 (demolish its biggest employer) changes nothing in region 1.
            WorldState r2 = b.Get(2).World;
            Building target = r2.Buildings.Where(x => r2.Economy.Companies.ContainsKey(x.Id)).OrderByDescending(x => r2.Economy.Companies[x.Id].Workers.Count).First();
            Assert.IsTrue(r2.TryRemoveBuilding(target.Id).Success);
            new AtlasRunner(a, WorldFactory.CreateRunner).StepMany(15 * SimClock.TicksPerDay);
            new AtlasRunner(b, WorldFactory.CreateRunner).StepMany(15 * SimClock.TicksPerDay);
            Assert.AreEqual(a.Get(1).World.ComputeHash(), b.Get(1).World.ComputeHash());
            Assert.AreNotEqual(a.Get(2).World.ComputeHash(), b.Get(2).World.ComputeHash());
        }

        [Test]
        public void SaveV4KeepsEveryRegionAndResumesExactly()
        {
            Atlas atlas = Atlas.Create(TestContent.Load(), TestContent.SmallOptions(3), 2);
            var runner = new AtlasRunner(atlas, WorldFactory.CreateRunner);
            runner.StepMany(5 * SimClock.TicksPerDay);
            string json = SaveSystem.SaveAtlas(atlas);
            StringAssert.Contains("\"version\":4", json);
            Atlas loaded = SaveSystem.LoadAtlas(TestContent.Load(), json);
            CollectionAssert.AreEqual(new[] { 1, 2 }, loaded.Regions.Select(r => r.Id).ToArray());
            Assert.AreEqual(2, loaded.Get(2).World.RegionId);
            Assert.AreEqual(atlas.ComputeHash(), loaded.ComputeHash());
            runner.StepMany(3 * SimClock.TicksPerDay);
            new AtlasRunner(loaded, WorldFactory.CreateRunner).StepMany(3 * SimClock.TicksPerDay);
            Assert.AreEqual(atlas.ComputeHash(), loaded.ComputeHash());
        }

        [Test]
        public void VersionThreeSavesStillLoad()
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(4));
            WorldFactory.CreateRunner(w).StepMany(3 * SimClock.TicksPerDay);
            // A v3 save is exactly a world document on its own (the v3 writer is unchanged).
            string v3 = Json.Serialize(SaveSystem.WorldDocument(w));
            StringAssert.Contains("\"version\":3", v3);
            Atlas atlas = SaveSystem.LoadAtlas(TestContent.Load(), v3);
            Assert.AreEqual(1, atlas.Regions.Count);
            Assert.AreEqual(w.ComputeHash(), atlas.Regions[0].World.ComputeHash());
            Assert.AreEqual(w.ComputeHash(), SaveSystem.Load(TestContent.Load(), v3).ComputeHash());
        }
    }
}
