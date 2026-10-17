using System;
using System.IO;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>Loads the shipped content JSON from the dotnet output folder or the Unity package path.</summary>
    internal static class TestContent
    {
        private static ContentDatabase _cached;

        public static ContentDatabase Load()
        {
            if (_cached != null) return _cached;
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "WorldSimContent"),
                Path.Combine("Packages", "com.worldsim.core", "Content", "Resources", "WorldSimContent"),
            };
            foreach (string dir in candidates)
            {
                if (!Directory.Exists(dir)) continue;
                string[] files = Directory.GetFiles(dir, "*.json");
                Array.Sort(files, StringComparer.Ordinal);
                var docs = new string[files.Length];
                for (int i = 0; i < files.Length; i++) docs[i] = File.ReadAllText(files[i]);
                return _cached = ContentDatabase.FromJson(docs);
            }
            throw new DirectoryNotFoundException("WorldSimContent folder not found");
        }

        /// <summary>A flat dry world for edit tests: every vertex at <paramref name="height"/>.</summary>
        public static WorldState FlatWorld(int size = 32, float height = 20f, float seaLevel = 10f)
        {
            var terrain = new Terrain(size, size, 10f, seaLevel);
            for (int z = 0; z <= size; z++)
            for (int x = 0; x <= size; x++)
                terrain.SetVertexHeight(x, z, height);
            return new WorldState(Load(), 42, terrain);
        }

        /// <summary>
        /// Flat world with an east-west road along z=10, homes north of it filled with working-age
        /// residents, and nothing else. Tests add the businesses they need.
        /// </summary>
        public static WorldState Village(int homes = 6, bool outsideRoad = false)
        {
            WorldState w = FlatWorld(48);
            w.TryBuildRoad(new GridPos(outsideRoad ? 0 : 2, 10), new GridPos(45, 10));
            for (int i = 0; i < homes; i++) w.TryPlaceBuilding("apartment", new GridPos(2 + i * 3, 11));
            foreach (Building b in w.Buildings)
                for (int k = 0; k < b.Def.Housing; k++)
                {
                    var c = new Citizen { Id = w.AllocateId(), BirthDay = -30 * 360, Education = 1, SchoolDays = -1 };
                    w.Economy.Citizens.Add(c.Id, c);
                    w.Economy.MoveIn(c, b.Id);
                    w.Economy.Ledger.Transfer(w.Economy.External, c, 300_000, Flow.Migration);
                }
            return w;
        }

        /// <summary>Places a building south of the village road (z=10) at the given x; returns its id.</summary>
        public static int PlaceSouth(WorldState w, string defId, int x)
        {
            BuildingDef def = w.Content.GetBuilding(defId);
            CommandResult r = w.TryPlaceBuilding(defId, new GridPos(x, 10 - def.Depth));
            if (!r.Success) throw new InvalidOperationException($"{defId} at {x}: {r.Message}");
            return r.CreatedId;
        }

        /// <summary>Some cell where the building fits next to an existing road, scanning from the centre.</summary>
        public static GridPos FindSpot(WorldState w, string defId)
        {
            BuildingDef def = w.Content.GetBuilding(defId);
            int cx = w.Width / 2, cz = w.Depth / 2;
            for (int r = 0; r < w.Width; r++)
            for (int z = cz - r; z <= cz + r; z++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                if (Math.Max(Math.Abs(x - cx), Math.Abs(z - cz)) != r) continue;
                var p = new GridPos(x, z);
                if (w.CanPlaceBuilding(def, p).Success && w.HasRoadAccess(GridRect.FromSize(p, def.Width, def.Depth))) return p;
            }
            throw new InvalidOperationException("no spot for " + defId);
        }

        public static WorldGenOptions SmallOptions(ulong seed) => new WorldGenOptions
        {
            Seed = seed,
            Terrain = new TerrainSettings { Width = 96, Depth = 96 },
            TownBlocksRadius = 2,
        };
    }
}
