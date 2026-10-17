using System;
using System.Collections.Generic;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.Transport;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Local wholesale prices move with yesterday's balance: rising while demand had to be imported
    /// or went unmet, falling while producers had to export surplus. Bounded around the base price.
    /// </summary>
    public sealed class MarketSystem : ISimulationSystem
    {
        public const double Step = 0.02;
        public const double MinFactor = 0.6, MaxFactor = 1.6;

        public string Name => "Market";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            Market m = w.Economy.Market;
            foreach (ResourceDef r in w.Content.Resources)
            {
                double produced = Get(m.Produced, r.Id);
                double shortage = Get(m.Unmet, r.Id) + Get(m.Imported, r.Id);
                double surplus = Get(m.Exported, r.Id);
                double scale = Math.Max(1, produced + Get(m.LocalTraded, r.Id));
                double p = m.GetPrice(r.Id);
                if (shortage > 0.05 * scale) p *= 1 + Step;
                else if (surplus > 0.05 * scale) p *= 1 - Step;
                m.Price[r.Id] = Math.Max(r.BasePrice * MinFactor, Math.Min(r.BasePrice * MaxFactor, p));
            }
        }

        private static double Get(SortedDictionary<string, double> d, string k) => d.TryGetValue(k, out double v) ? v : 0;
    }

    /// <summary>
    /// Assigns the day's trips (commutes, shopping, freight) to their fastest routes to get vehicles
    /// per road cell. The smoothed volumes drive congestion in <see cref="RoadNetwork"/>, which feeds
    /// back into commute choice, freight cost and where people live (scenario B).
    /// </summary>
    public sealed class TrafficSystem : ISimulationSystem
    {
        /// <summary>Share of commuters who drive (the rest walk, cycle or ride transit, not modelled yet).</summary>
        public const float CarShare = 0.7f;
        public const int RerouteIntervalDays = 3;

        public string Name => "Traffic";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EconomyState e = w.Economy;
            var pairs = new SortedDictionary<long, float>();
            foreach (Citizen c in e.Citizens.Values)
                if (c.JobId != 0 && c.HomeId != 0) AddPair(pairs, c.HomeId, c.JobId, 2 * CarShare);
            foreach (Trip t in w.Trips)
                AddPair(pairs, t.FromBuilding, t.ToBuilding, t.Vehicles * (t.Freight ? 2 : 1));

            var volume = new float[w.TrafficVolume.Length];
            foreach (var kv in pairs)
            {
                int from = (int)(kv.Key >> 32), to = (int)(kv.Key & 0xFFFFFFFF);
                List<int> cells = to == 0 ? w.Network.RouteCellsToOutside(from) : w.Network.RouteCells(from, to);
                foreach (int cell in cells) volume[cell] += kv.Value;
            }
            float[] tv = w.TrafficVolume;
            for (int i = 0; i < tv.Length; i++) tv[i] = tv[i] * 0.5f + volume[i] * 0.5f;

            if (w.Clock.TotalDays % RerouteIntervalDays == 0)
            {
                w.Network.Invalidate();
                foreach (Citizen c in e.Citizens.Values)
                    if (c.JobId != 0 && c.HomeId != 0)
                    {
                        float h = w.Network.TravelHours(c.HomeId, c.JobId);
                        if (float.IsPositiveInfinity(h) || h > LaborSystem.MaxCommuteHours * 1.5f) e.Fire(c);
                        else c.CommuteHours = h;
                    }
            }
        }

        private static void AddPair(SortedDictionary<long, float> pairs, int from, int to, float v)
        {
            if (from == 0 || from == to) return;
            long key = ((long)from << 32) | (uint)to;
            pairs[key] = (pairs.TryGetValue(key, out float cur) ? cur : 0) + v;
        }
    }
}
