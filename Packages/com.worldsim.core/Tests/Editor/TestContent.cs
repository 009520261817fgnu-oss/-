using System;
using System.IO;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Content;
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

        public static WorldGenOptions SmallOptions(ulong seed) => new WorldGenOptions
        {
            Seed = seed,
            Terrain = new TerrainSettings { Width = 96, Depth = 96 },
            TownBlocksRadius = 2,
        };
    }
}
