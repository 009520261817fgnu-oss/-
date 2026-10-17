using System.Collections.Generic;
using NUnit.Framework;
using WorldSim.Core.Commands;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    public class BuildingPlacementTests
    {
        [Test]
        public void PlaceAndRemoveBuilding()
        {
            WorldState w = TestContent.FlatWorld();
            var changes = new List<WorldChange>();
            w.Changed += changes.Add;

            CommandResult r = new PlaceBuildingCommand("factory", new GridPos(4, 4)).Execute(w);
            Assert.IsTrue(r.Success, r.Message);
            Assert.AreEqual(1, w.BuildingCount);
            Assert.AreEqual(r.CreatedId, w.GetBuildingAt(new GridPos(7, 7)).Id);
            Assert.IsNull(w.GetBuildingAt(new GridPos(8, 8)));
            Assert.That(changes, Has.Some.Matches<WorldChange>(c => c.Kind == WorldChangeKind.Buildings));

            Assert.IsTrue(new BulldozeCommand(new GridPos(5, 6)).Execute(w).Success);
            Assert.AreEqual(0, w.BuildingCount);
            Assert.IsFalse(w.IsOccupied(new GridPos(4, 4)));
        }

        [Test]
        public void IdsAreNeverReused()
        {
            WorldState w = TestContent.FlatWorld();
            int a = w.TryPlaceBuilding("shop", new GridPos(0, 0)).CreatedId;
            w.TryRemoveBuilding(a);
            int b = w.TryPlaceBuilding("shop", new GridPos(0, 0)).CreatedId;
            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void OverlapIsRejected()
        {
            WorldState w = TestContent.FlatWorld();
            Assert.IsTrue(w.TryPlaceBuilding("factory", new GridPos(4, 4)).Success);
            Assert.IsFalse(w.TryPlaceBuilding("shop", new GridPos(7, 7)).Success);
            Assert.IsTrue(w.TryPlaceBuilding("shop", new GridPos(8, 8)).Success);
        }

        [Test]
        public void OutOfBoundsAndUnknownAreRejected()
        {
            WorldState w = TestContent.FlatWorld(16);
            Assert.IsFalse(w.TryPlaceBuilding("factory", new GridPos(13, 0)).Success);
            Assert.IsFalse(w.TryPlaceBuilding("factory", new GridPos(-1, 0)).Success);
            Assert.IsFalse(w.TryPlaceBuilding("no_such_building", new GridPos(0, 0)).Success);
            Assert.AreEqual(0, w.BuildingCount);
        }

        [Test]
        public void UnderwaterAndSteepAreRejected()
        {
            WorldState w = TestContent.FlatWorld();
            w.Terrain.SetVertexHeight(2, 2, 5f); // below sea level 10
            Assert.IsFalse(w.TryPlaceBuilding("shop", new GridPos(1, 1)).Success);

            w.Terrain.SetVertexHeight(20, 20, 30f); // 10 m bump
            Assert.IsFalse(w.TryPlaceBuilding("shop", new GridPos(19, 19)).Success);
            Assert.IsTrue(w.TryPlaceBuilding("shop", new GridPos(24, 24)).Success);
        }

        [Test]
        public void BaseHeightIsHighestFootprintVertex()
        {
            WorldState w = TestContent.FlatWorld();
            w.Terrain.SetVertexHeight(5, 5, 21.5f);
            int id = w.TryPlaceBuilding("shop", new GridPos(4, 4)).CreatedId;
            w.TryGetBuilding(id, out Building b);
            Assert.AreEqual(21.5f, b.BaseHeight);
        }
    }

    public class RoadTests
    {
        [Test]
        public void RoadPathIsLShapedAndInclusive()
        {
            List<GridPos> p = WorldState.RoadPath(new GridPos(0, 0), new GridPos(3, 2));
            CollectionAssert.AreEqual(new[]
            {
                new GridPos(0, 0), new GridPos(1, 0), new GridPos(2, 0), new GridPos(3, 0),
                new GridPos(3, 1), new GridPos(3, 2),
            }, p);
        }

        [Test]
        public void BuildRoadAndCrossExisting()
        {
            WorldState w = TestContent.FlatWorld();
            Assert.IsTrue(new BuildRoadCommand(new GridPos(0, 5), new GridPos(10, 5)).Execute(w).Success);
            Assert.AreEqual(11, w.RoadCellCount);
            Assert.IsTrue(new BuildRoadCommand(new GridPos(5, 0), new GridPos(5, 10)).Execute(w).Success);
            Assert.AreEqual(21, w.RoadCellCount); // crossing cell counted once
        }

        [Test]
        public void RoadIsAtomicWhenBlocked()
        {
            WorldState w = TestContent.FlatWorld();
            w.TryPlaceBuilding("shop", new GridPos(6, 4));
            CommandResult r = w.TryBuildRoad(new GridPos(0, 5), new GridPos(10, 5));
            Assert.IsFalse(r.Success);
            Assert.AreEqual(0, w.RoadCellCount);
        }

        [Test]
        public void BuildingCannotBePlacedOnRoad()
        {
            WorldState w = TestContent.FlatWorld();
            w.TryBuildRoad(new GridPos(0, 5), new GridPos(10, 5));
            Assert.IsFalse(w.TryPlaceBuilding("shop", new GridPos(3, 4)).Success);
            Assert.IsTrue(w.TryPlaceBuilding("shop", new GridPos(3, 6)).Success);
            Assert.IsTrue(w.HasRoadAccess(GridRect.FromSize(new GridPos(3, 6), 2, 2)));
            Assert.IsFalse(w.HasRoadAccess(GridRect.FromSize(new GridPos(3, 8), 2, 2)));
        }

        [Test]
        public void BulldozeRemovesRoadCell()
        {
            WorldState w = TestContent.FlatWorld();
            w.TryBuildRoad(new GridPos(0, 5), new GridPos(3, 5));
            Assert.IsTrue(w.TryBulldoze(new GridPos(2, 5)).Success);
            Assert.AreEqual(3, w.RoadCellCount);
            Assert.IsFalse(w.HasRoad(new GridPos(2, 5)));
            Assert.IsFalse(w.TryBulldoze(new GridPos(2, 5)).Success);
        }
    }

    public class TerrainEditTests
    {
        [Test]
        public void RaiseChangesHeightsWithFalloffAndRaisesEvent()
        {
            WorldState w = TestContent.FlatWorld();
            var changes = new List<WorldChange>();
            w.Changed += changes.Add;

            Assert.IsTrue(new RaiseTerrainCommand(16, 16, 4, 5f).Execute(w).Success);
            float center = w.Terrain.GetVertexHeight(16, 16);
            float mid = w.Terrain.GetVertexHeight(18, 16);
            float outside = w.Terrain.GetVertexHeight(21, 16);
            Assert.AreEqual(25f, center, 1e-4);
            Assert.That(mid, Is.GreaterThan(20f).And.LessThan(25f));
            Assert.AreEqual(20f, outside);
            Assert.That(changes, Has.Some.Matches<WorldChange>(c => c.Kind == WorldChangeKind.Terrain && c.Region.Contains(new GridPos(16, 16))));
        }

        [Test]
        public void LowerIsClampedAtZero()
        {
            WorldState w = TestContent.FlatWorld();
            w.TryRaiseTerrain(5, 5, 2, -500f);
            Assert.AreEqual(Terrain.MinHeight, w.Terrain.GetVertexHeight(5, 5));
        }

        [Test]
        public void VerticesUnderStructuresAreProtected()
        {
            WorldState w = TestContent.FlatWorld();
            w.TryPlaceBuilding("factory", new GridPos(10, 10)); // vertices 10..14
            w.TryBuildRoad(new GridPos(10, 20), new GridPos(14, 20));
            w.TryRaiseTerrain(12, 12, 6, 8f);
            w.TryRaiseTerrain(12, 20, 3, 8f);
            for (int z = 10; z <= 14; z++)
            for (int x = 10; x <= 14; x++)
                Assert.AreEqual(20f, w.Terrain.GetVertexHeight(x, z), $"vertex {x},{z}");
            Assert.AreEqual(20f, w.Terrain.GetVertexHeight(12, 21));
            Assert.Greater(w.Terrain.GetVertexHeight(12, 16), 20f);
        }

        [Test]
        public void EditOnlyUnderStructureFails()
        {
            WorldState w = TestContent.FlatWorld();
            w.TryPlaceBuilding("factory", new GridPos(10, 10));
            Assert.IsFalse(w.TryRaiseTerrain(12, 12, 1, 3f).Success);
        }

        [Test]
        public void FlattenMakesAreaBuildable()
        {
            WorldState w = TestContent.FlatWorld();
            w.TryRaiseTerrain(16, 16, 3, 6f);
            Assert.IsFalse(w.CanPlaceBuilding(w.Content.GetBuilding("factory"), new GridPos(14, 14)).Success);
            for (int i = 0; i < 5; i++) w.TryFlattenTerrain(16, 16, 5, 20f);
            Assert.IsTrue(w.CanPlaceBuilding(w.Content.GetBuilding("factory"), new GridPos(14, 14)).Success);
        }

        [Test]
        public void SinkingBelowSeaRemovesVegetation()
        {
            var terrain = new Terrain(8, 8, 10f, 10f);
            for (int z = 0; z <= 8; z++)
            for (int x = 0; x <= 8; x++)
                terrain.SetVertexHeight(x, z, 15f);
            var veg = new byte[64];
            for (int i = 0; i < veg.Length; i++) veg[i] = 200;
            var w = new WorldState(TestContent.Load(), 1, terrain, veg);
            w.TryRaiseTerrain(4, 4, 2, -20f);
            Assert.AreEqual(0, w.GetVegetation(new GridPos(3, 3)));
            Assert.AreEqual(200, w.GetVegetation(new GridPos(0, 0)));
        }
    }
}
