using System;
using System.Collections.Generic;
using WorldSim.Core.World;

namespace WorldSim.Core.Simulation
{
    /// <summary>
    /// Advances every region of an atlas one hour at a time, in region id order (S5-4). Regions do not
    /// exchange anything yet; S5-5 settles trade and migration between them once a day, after all
    /// regions have run that day.
    /// </summary>
    public sealed class AtlasRunner
    {
        public Atlas Atlas { get; }
        private readonly List<SimulationRunner> _runners = new List<SimulationRunner>();

        public AtlasRunner(Atlas atlas, Func<WorldState, SimulationRunner> createRunner)
        {
            Atlas = atlas;
            foreach (Region r in atlas.Regions) _runners.Add(createRunner(r.World));
        }

        public SimulationRunner RunnerOf(int regionId) => _runners[Atlas.Regions.FindIndex(r => r.Id == regionId)];

        public void Step()
        {
            foreach (SimulationRunner runner in _runners) runner.Step();
        }

        public void StepMany(int ticks)
        {
            for (int i = 0; i < ticks; i++) Step();
        }
    }
}
