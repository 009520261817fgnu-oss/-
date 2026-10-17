using System;
using System.Collections.Generic;
using WorldSim.Core.Content;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Bootstrap
{
    public sealed class WorldGenOptions
    {
        public ulong Seed = 1;
        public TerrainSettings Terrain = new TerrainSettings();
        public bool BuildStartingTown = true;
        /// <summary>Spacing between parallel roads in the town grid (block interior = spacing - 1).</summary>
        public int RoadSpacing = 7;
        /// <summary>Number of blocks from the centre to each edge of the town grid.</summary>
        public int TownBlocksRadius = 3;
    }

    /// <summary>
    /// Creates the prototype's initial world (spec 3.2): generated terrain, forest, and a small town
    /// with roads, housing, shops, offices, industry, a power plant, public buildings and farms.
    /// Everything is placed through the same validated WorldState methods the player uses.
    /// </summary>
    public static class WorldFactory
    {
        public static WorldState Create(ContentDatabase content, WorldGenOptions options)
        {
            Terrain terrain = TerrainGenerator.Generate(options.Terrain, options.Seed);
            byte[] vegetation = TerrainGenerator.GenerateVegetation(terrain, options.Seed);
            var world = new WorldState(content, options.Seed, terrain, vegetation);
            if (options.BuildStartingTown) BuildTown(world, options);
            return world;
        }

        /// <summary>Runner with the systems that exist so far.</summary>
        public static SimulationRunner CreateRunner(WorldState world)
        {
            var runner = new SimulationRunner(world);
            runner.AddSystem(new StatsHistorySystem());
            return runner;
        }

        private enum Zone { Center, Residential, Industrial }

        private static void BuildTown(WorldState world, WorldGenOptions o)
        {
            int sp = o.RoadSpacing, n = o.TownBlocksRadius;
            int cx = world.Width / 2, cz = world.Depth / 2;
            int x0 = cx - n * sp, z0 = cz - n * sp;
            int lines = 2 * n + 1;

            // Road grid, segment by segment, so one bad segment (water, cliff) does not cancel the rest.
            for (int i = 0; i < lines; i++)
            for (int j = 0; j < lines - 1; j++)
            {
                world.TryBuildRoad(new GridPos(x0 + i * sp, z0 + j * sp), new GridPos(x0 + i * sp, z0 + (j + 1) * sp));
                world.TryBuildRoad(new GridPos(x0 + j * sp, z0 + i * sp), new GridPos(x0 + (j + 1) * sp, z0 + i * sp));
            }

            var blocks = new List<(GridRect rect, Zone zone)>();
            for (int bz = 0; bz < 2 * n; bz++)
            for (int bx = 0; bx < 2 * n; bx++)
            {
                var rect = new GridRect(x0 + bx * sp + 1, z0 + bz * sp + 1, x0 + (bx + 1) * sp, z0 + (bz + 1) * sp);
                int dx = bx - n, dz = bz - n; // block offset from centre, -n..n-1
                Zone zone = (dx == -1 || dx == 0) && (dz == -1 || dz == 0) ? Zone.Center
                    : dx >= n - 1 ? Zone.Industrial
                    : Zone.Residential;
                blocks.Add((rect, zone));
            }

            DeterministicRandom rng = world.Random;
            PlaceFirst(world, blocks, Zone.Center, "town_hall");
            PlaceFirst(world, blocks, Zone.Residential, "school");
            PlaceFirst(world, blocks, Zone.Industrial, "power_plant");

            foreach (var (rect, zone) in blocks)
            {
                string[] pool = zone == Zone.Center ? new[] { "office", "shop", "shop", "apartment" }
                    : zone == Zone.Industrial ? new[] { "factory", "warehouse" }
                    : new[] { "house_small", "house_small", "house_small", "apartment", "shop" };
                FillBlock(world, rect, pool, rng);
            }

            // Farms on open land west of town, served by short access roads off the grid edge.
            int farmsPlaced = 0;
            for (int z = z0; z < z0 + 2 * n * sp && farmsPlaced < 6; z += 5)
            for (int x = x0 - 2; x > x0 - 30 && farmsPlaced < 6; x -= 5)
            {
                if (!world.Content.TryGetBuilding("farm", out BuildingDef farm)) return;
                var origin = new GridPos(x - farm.Width + 1, z);
                if (!world.CanPlaceBuilding(farm, origin).Success) continue;
                if (world.TryPlaceBuilding("farm", origin).Success) farmsPlaced++;
            }
            ConnectFarms(world, x0);
        }

        private static void PlaceFirst(WorldState world, List<(GridRect rect, Zone zone)> blocks, Zone zone, string defId)
        {
            if (!world.Content.TryGetBuilding(defId, out BuildingDef def)) return;
            foreach (var (rect, z) in blocks)
            {
                if (z != zone) continue;
                if (TryPlaceInBlock(world, rect, def)) return;
            }
        }

        private static bool TryPlaceInBlock(WorldState world, GridRect block, BuildingDef def)
        {
            for (int z = block.MinZ; z + def.Depth <= block.MaxZ; z++)
            for (int x = block.MinX; x + def.Width <= block.MaxX; x++)
            {
                var origin = new GridPos(x, z);
                if (!world.HasRoadAccess(GridRect.FromSize(origin, def.Width, def.Depth))) continue;
                if (world.TryPlaceBuilding(def.Id, origin).Success) return true;
            }
            return false;
        }

        private static void FillBlock(WorldState world, GridRect block, string[] pool, DeterministicRandom rng)
        {
            for (int z = block.MinZ; z < block.MaxZ; z++)
            for (int x = block.MinX; x < block.MaxX; x++)
            {
                var origin = new GridPos(x, z);
                if (world.IsOccupied(origin)) continue;
                int start = rng.NextInt(0, pool.Length);
                for (int k = 0; k < pool.Length; k++)
                {
                    if (!world.Content.TryGetBuilding(pool[(start + k) % pool.Length], out BuildingDef def)) continue;
                    GridRect r = GridRect.FromSize(origin, def.Width, def.Depth);
                    if (r.MaxX > block.MaxX || r.MaxZ > block.MaxZ || !world.HasRoadAccess(r)) continue;
                    if (world.TryPlaceBuilding(def.Id, origin).Success) break;
                }
            }
        }

        /// <summary>Links each farm without road access to the town grid with a straight westward road.</summary>
        private static void ConnectFarms(WorldState world, int gridWestX)
        {
            foreach (Building b in new List<Building>(world.Buildings))
            {
                if (b.Def.Category != BuildingCategory.Agriculture || world.HasRoadAccess(b.Footprint)) continue;
                GridRect r = b.Footprint;
                int z = r.MinZ - 1;
                if (z < 0) continue;
                world.TryBuildRoad(new GridPos(r.MinX, z), new GridPos(Math.Min(gridWestX, world.Width - 1), z));
            }
        }
    }
}
