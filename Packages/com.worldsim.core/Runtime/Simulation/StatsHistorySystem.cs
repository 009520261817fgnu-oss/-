using System.Collections.Generic;
using WorldSim.Core.Content;
using WorldSim.Core.World;

namespace WorldSim.Core.Simulation
{
    public sealed class StatsSnapshot
    {
        public long Tick;
        public int Buildings;
        public int RoadCells;
        public readonly Dictionary<BuildingCategory, int> ByCategory = new Dictionary<BuildingCategory, int>();
    }

    /// <summary>
    /// Records a daily snapshot of world statistics. This is the data source for the graphs in
    /// later phases; population, money etc. are added here when those systems exist.
    /// </summary>
    public sealed class StatsHistorySystem : ISimulationSystem
    {
        private readonly List<StatsSnapshot> _history = new List<StatsSnapshot>();

        public string Name => "Stats";
        public int IntervalTicks => SimClock.TicksPerDay;
        public IReadOnlyList<StatsSnapshot> History => _history;

        public void Tick(WorldState world) => _history.Add(Capture(world));

        public static StatsSnapshot Capture(WorldState world)
        {
            var s = new StatsSnapshot { Tick = world.Clock.Tick, Buildings = world.BuildingCount, RoadCells = world.RoadCellCount };
            foreach (Building b in world.Buildings)
            {
                s.ByCategory.TryGetValue(b.Def.Category, out int n);
                s.ByCategory[b.Def.Category] = n + 1;
            }
            return s;
        }
    }
}
