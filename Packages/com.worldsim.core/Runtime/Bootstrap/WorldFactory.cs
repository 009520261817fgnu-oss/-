using System;
using System.Collections.Generic;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.Bootstrap
{
    public sealed class WorldGenOptions
    {
        public ulong Seed = 1;
        public TerrainSettings Terrain = new TerrainSettings();
        public bool BuildStartingTown = true;
        public bool Populate = true;
        /// <summary>Spacing between parallel roads in the town grid (block interior = spacing - 1).</summary>
        public int RoadSpacing = 7;
        /// <summary>Number of blocks from the centre to each edge of the town grid.</summary>
        public int TownBlocksRadius = 3;
        /// <summary>Share of housing filled by the initial population.</summary>
        public double InitialOccupancy = 0.8;
    }

    /// <summary>
    /// Creates the prototype's initial world (spec 3.2): generated terrain and forest, a town with
    /// roads, housing, shops, offices, the full production chain, power, public buildings, farms,
    /// a mine and forestry on the outskirts, a highway to the map edge, and a resident population.
    /// Everything is placed through the same validated WorldState methods the player uses.
    /// </summary>
    public static class WorldFactory
    {
        public static WorldState Create(ContentDatabase content, WorldGenOptions options)
        {
            Terrain terrain = TerrainGenerator.Generate(options.Terrain, options.Seed);
            byte[] vegetation = TerrainGenerator.GenerateVegetation(terrain, options.Seed);
            var world = new WorldState(content, options.Seed, terrain, vegetation);
            world.ChargeConstruction = false;
            if (options.BuildStartingTown) BuildTown(world, options);
            if (options.Populate) Populate(world, options.InitialOccupancy);
            world.ChargeConstruction = true;
            return world;
        }

        /// <summary>The daily pipeline, in dependency order.</summary>
        public static SimulationRunner CreateRunner(WorldState world)
        {
            var runner = new SimulationRunner(world);
            runner.AddSystem(new People.AvatarSystem()); // hourly: the controlled resident's needs and work hours
            ISimulationSystem[] daily =
            {
                new WeatherSystem(), new DisasterSystem(), new LaborSystem(), new EnergySystem(), new ResearchSystem(),
                new ProductionSystem(), new LogisticsSystem(), new RetailSystem(), new FinanceSystem(), new PopulationSystem(),
                new MarketSystem(), new TrafficSystem(), new PollutionSystem(), new RepairSystem(), new StatsHistorySystem(),
            };
            // One system per in-game hour (01:00 weather ... 15:00 statistics), in dependency order.
            for (int i = 0; i < daily.Length; i++) runner.AddSystem(daily[i], i + 1);
            return runner;
        }

        private enum Zone { Center, Residential, Industrial }

        private static void BuildTown(WorldState world, WorldGenOptions o)
        {
            int sp = o.RoadSpacing, n = o.TownBlocksRadius;
            int cx = world.Width / 2, cz = world.Depth / 2;
            int x0 = cx - n * sp, z0 = cz - n * sp, x1 = cx + n * sp, z1 = cz + n * sp;
            int lines = 2 * n + 1;

            // Road grid, segment by segment, so one bad segment (water, cliff) does not cancel the rest.
            for (int i = 0; i < lines; i++)
            for (int j = 0; j < lines - 1; j++)
            {
                world.TryBuildRoad(new GridPos(x0 + i * sp, z0 + j * sp), new GridPos(x0 + i * sp, z0 + (j + 1) * sp));
                world.TryBuildRoad(new GridPos(x0 + j * sp, z0 + i * sp), new GridPos(x0 + (j + 1) * sp, z0 + i * sp));
            }
            BuildHighway(world, x0, z0, x1, z1, sp);

            var blocks = new List<(GridRect rect, Zone zone)>();
            for (int bz = 0; bz < 2 * n; bz++)
            for (int bx = 0; bx < 2 * n; bx++)
            {
                var rect = new GridRect(x0 + bx * sp + 1, z0 + bz * sp + 1, x0 + (bx + 1) * sp, z0 + (bz + 1) * sp);
                int dx = bx - n, dz = bz - n; // block offset from centre, -n..n-1
                Zone zone = (dx == -1 || dx == 0) && (dz == -1 || dz == 0) ? Zone.Center
                    : dx >= n - 2 && dx != -1 && dx != 0 ? Zone.Industrial
                    : Zone.Residential;
                blocks.Add((rect, zone));
            }

            foreach (string id in new[] { "town_hall", "research_lab" }) PlaceFirst(world, blocks, Zone.Center, id);
            foreach (string id in new[] { "school", "fire_station" }) PlaceFirst(world, blocks, Zone.Residential, id);
            // Industry gets fixed quotas sized to the town; remaining industrial lots stay free for the player.
            foreach (string id in new[] { "power_plant", "steel_mill", "food_plant", "food_plant", "food_plant",
                         "factory", "factory", "warehouse", "warehouse" })
                PlaceFirst(world, blocks, Zone.Industrial, id);

            DeterministicRandom rng = world.Random;
            foreach (var (rect, zone) in blocks)
            {
                if (zone == Zone.Industrial) continue;
                string[] pool = zone == Zone.Center ? new[] { "office", "office", "shop", "apartment" }
                    : new[] { "house_small", "house_small", "house_small", "house_small", "apartment", "shop", "shop" };
                FillBlock(world, rect, pool, rng);
            }

            var town = new GridRect(x0, z0, x1 + 1, z1 + 1);
            PlaceOutskirts(world, town, "farm", 6, (w, c) => -Math.Abs(c.X - (town.MinX - 12)) * 0.5 - w.Terrain.CellMaxHeight(c));
            PlaceOutskirts(world, town, "mine", 2, (w, c) => w.Terrain.CellMaxHeight(c) - Distance(c, town) * 1.5);
            PlaceOutskirts(world, town, "forestry", 2, (w, c) => ForestAround(w, c) - Distance(c, town) * 30);
        }

        private static void BuildHighway(WorldState w, int x0, int z0, int x1, int z1, int sp)
        {
            // Try each grid line in each direction until one reaches the map edge.
            for (int z = z0; z <= z1; z += sp)
            {
                if (w.TryBuildRoad(new GridPos(x1, z), new GridPos(w.Width - 1, z)).Success) return;
                if (w.TryBuildRoad(new GridPos(x0, z), new GridPos(0, z)).Success) return;
            }
            for (int x = x0; x <= x1; x += sp)
            {
                if (w.TryBuildRoad(new GridPos(x, z1), new GridPos(x, w.Depth - 1)).Success) return;
                if (w.TryBuildRoad(new GridPos(x, z0), new GridPos(x, 0)).Success) return;
            }
        }

        private static double Distance(GridPos c, GridRect r)
        {
            int dx = Math.Max(0, Math.Max(r.MinX - c.X, c.X - r.MaxX));
            int dz = Math.Max(0, Math.Max(r.MinZ - c.Z, c.Z - r.MaxZ));
            return Math.Sqrt(dx * dx + dz * dz);
        }

        private static double ForestAround(WorldState w, GridPos c)
        {
            double sum = 0;
            for (int z = c.Z - 6; z <= c.Z + 6; z++)
            for (int x = c.X - 6; x <= c.X + 6; x++)
            {
                var p = new GridPos(x, z);
                if (w.Terrain.InBounds(p)) sum += w.GetVegetation(p);
            }
            return sum / 255.0;
        }

        /// <summary>Places buildings outside the town where <paramref name="score"/> is highest and links them by road.</summary>
        private static void PlaceOutskirts(WorldState w, GridRect town, string defId, int count, Func<WorldState, GridPos, double> score)
        {
            if (!w.Content.TryGetBuilding(defId, out BuildingDef def)) return;
            var candidates = new List<(double s, GridPos p)>();
            GridRect area = town.Expand(32).Clamp(w.Width, w.Depth);
            for (int z = area.MinZ; z < area.MaxZ; z += 2)
            for (int x = area.MinX; x < area.MaxX; x += 2)
            {
                var p = new GridPos(x, z);
                double d = Distance(p, town);
                if (d < 3 || d > 30) continue;
                if (!w.CanPlaceBuilding(def, p).Success) continue;
                candidates.Add((score(w, p), p));
            }
            candidates.Sort((a, b) => a.s != b.s ? b.s.CompareTo(a.s) : (a.p.Z * 1000 + a.p.X).CompareTo(b.p.Z * 1000 + b.p.X));
            int placed = 0;
            foreach (var cand in candidates)
            {
                if (placed >= count) break;
                CommandResult r = w.TryPlaceBuilding(defId, cand.p);
                if (!r.Success) continue;
                w.TryGetBuilding(r.CreatedId, out Building b);
                if (w.HasRoadAccess(b.Footprint) || ConnectToRoads(w, b)) placed++;
                else w.TryRemoveBuilding(b.Id);
            }
        }

        /// <summary>Builds an access road from the building to the nearest existing road.</summary>
        public static bool ConnectToRoads(WorldState w, Building b)
        {
            GridRect r = b.Footprint;
            var sides = new List<GridPos>();
            for (int x = r.MinX; x < r.MaxX; x++) { sides.Add(new GridPos(x, r.MinZ - 1)); sides.Add(new GridPos(x, r.MaxZ)); }
            for (int z = r.MinZ; z < r.MaxZ; z++) { sides.Add(new GridPos(r.MinX - 1, z)); sides.Add(new GridPos(r.MaxX, z)); }

            var tries = new List<(int d, GridPos from, GridPos to)>();
            for (int z = 0; z < w.Depth; z++)
            for (int x = 0; x < w.Width; x++)
            {
                var road = new GridPos(x, z);
                if (!w.HasRoad(road)) continue;
                foreach (GridPos s in sides)
                {
                    if (!w.Terrain.InBounds(s)) continue;
                    int d = Math.Abs(s.X - x) + Math.Abs(s.Z - z);
                    if (d <= 40) tries.Add((d, s, road));
                }
            }
            tries.Sort((a, b2) => a.d != b2.d ? a.d.CompareTo(b2.d)
                : (a.from.Z * 1000 + a.from.X).CompareTo(b2.from.Z * 1000 + b2.from.X));
            int attempts = 0;
            foreach (var t in tries)
            {
                if (++attempts > 60) break;
                if (w.TryBuildRoad(t.from, t.to).Success) return true;
                var corner = new GridPos(t.from.X, t.to.Z);
                if (w.CanBuildRoad(t.from, corner) && w.CanBuildRoad(corner, t.to))
                {
                    w.TryBuildRoad(t.from, corner);
                    w.TryBuildRoad(corner, t.to);
                    return true;
                }
            }
            return false;
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

        /// <summary>Fills housing with residents, stocks businesses for a few days and staffs them.</summary>
        public static void Populate(WorldState w, double occupancy)
        {
            EconomyState e = w.Economy;
            DeterministicRandom rng = w.Random;
            long today = w.Clock.TotalDays;
            foreach (Building b in w.Buildings)
            {
                int target = (int)Math.Round(b.Def.Housing * occupancy);
                for (int i = 0; i < target; i++)
                {
                    double r = rng.NextDouble();
                    int age = r < 0.2 ? rng.NextInt(0, 18) : r < 0.85 ? rng.NextInt(18, 65) : rng.NextInt(65, 86);
                    double er = rng.NextDouble();
                    var c = new Citizen
                    {
                        Id = w.AllocateId(),
                        BirthDay = today - age * SimClock.DaysPerYear - rng.NextInt(0, SimClock.DaysPerYear),
                        Education = er < 0.3 ? 0 : er < 0.8 ? 1 : 2,
                        Retired = age >= 65,
                        SchoolDays = age < 18 ? (int)(Math.Max(0, age - 6) * SimClock.DaysPerYear * 0.9) : -1,
                    };
                    e.Citizens.Add(c.Id, c);
                    e.MoveIn(c, b.Id);
                    e.Ledger.Transfer(e.External, c, rng.NextInt(100_000, 600_000), Flow.Migration);
                }
            }

            foreach (Company co in e.Companies.Values)
            {
                int jobs = co.Def.Jobs;
                if (co.Recipe != null)
                    foreach (var kv in co.Recipe.Inputs) co.AddStock(kv.Key, kv.Value * jobs * 3);
                foreach (string res in co.Def.Sells)
                    if (!co.Produces(res)) co.AddStock(res, 60);
            }

            for (int pass = 0; pass < 10; pass++) LaborSystem.Match(w, int.MaxValue);
        }
    }
}
