using System;
using System.Collections.Generic;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Content;

namespace WorldSim.Core.World
{
    /// <summary>How a region is simulated (S5). Only Detailed exists so far; Aggregate comes in S5-6.</summary>
    public enum RegionDetail { Detailed = 0 }

    /// <summary>
    /// One region of the world (S5-4). A detailed region is a complete <see cref="WorldState"/>: its own
    /// map, people, companies, ledger and random stream. The world's "External" account and its
    /// imports, exports and migration are the region's boundary; later stages connect boundaries of
    /// neighbouring regions instead of sending everything to the outside world.
    /// </summary>
    public sealed class Region
    {
        public int Id;
        public string Name;
        public RegionDetail Detail = RegionDetail.Detailed;
        public WorldState World;
    }

    /// <summary>
    /// The set of regions that make up a game (S5-4, docs/S5_PLAN.md 3.0-3.1). Regions are kept and
    /// processed in id order. Each region draws from its own random stream derived from the world
    /// seed and its id, so what happens in one region never shifts the dice of another.
    /// </summary>
    public sealed class Atlas
    {
        public ulong Seed;
        public readonly List<Region> Regions = new List<Region>();

        /// <summary>
        /// Seed of a region's world. Region 1 uses the world seed itself, so a one-region game is exactly
        /// the game before regions existed (same map, same history, same hash).
        /// </summary>
        public static ulong RegionSeed(ulong worldSeed, int regionId)
        {
            if (regionId < 1) throw new ArgumentOutOfRangeException(nameof(regionId));
            if (regionId == 1) return worldSeed;
            ulong s = DeterministicRandom.SplitMix(worldSeed ^ ((ulong)regionId * 0xD1B54A32D192ED03UL));
            return s == 0 ? 1 : s;
        }

        public Region Get(int id)
        {
            foreach (Region r in Regions) if (r.Id == id) return r;
            throw new KeyNotFoundException($"region {id}");
        }

        public void Add(Region r)
        {
            if (r.World == null) throw new ArgumentException("a detailed region needs a world");
            if (Regions.Exists(x => x.Id == r.Id)) throw new ArgumentException($"region {r.Id} exists");
            r.World.RegionId = r.Id;
            Regions.Add(r);
            Regions.Sort((a, b) => a.Id.CompareTo(b.Id));
        }

        /// <summary>A game with one region that is the given world (how every pre-S5 world is seen).</summary>
        public static Atlas Single(WorldState w, string name = null)
        {
            var atlas = new Atlas { Seed = w.Seed };
            atlas.Add(new Region { Id = w.RegionId, Name = name ?? DefaultName(w.RegionId), World = w });
            return atlas;
        }

        /// <summary>Generates <paramref name="regions"/> detailed regions from one world seed.</summary>
        public static Atlas Create(ContentDatabase content, WorldGenOptions options, int regions = 1)
        {
            var atlas = new Atlas { Seed = options.Seed };
            for (int id = 1; id <= regions; id++)
            {
                WorldGenOptions o = options.With(RegionSeed(options.Seed, id));
                atlas.Add(new Region { Id = id, Name = DefaultName(id), World = WorldFactory.Create(content, o) });
            }
            return atlas;
        }

        public static string DefaultName(int id) => $"地域{id}";

        /// <summary>Fingerprint of all regions, combined in id order.</summary>
        public ulong ComputeHash()
        {
            var h = new StateHasher();
            h.Add(Seed);
            foreach (Region r in Regions)
            {
                h.Add(r.Id);
                h.Add((int)r.Detail);
                h.Add(r.World.ComputeHash());
            }
            return h.Value;
        }
    }
}
