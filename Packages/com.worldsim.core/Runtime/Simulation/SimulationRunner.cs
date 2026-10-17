using System;
using System.Collections.Generic;
using WorldSim.Core.World;

namespace WorldSim.Core.Simulation
{
    public enum SimSpeed
    {
        Paused = 0,
        Normal = 1,
        Fast = 2,
        VeryFast = 3,
    }

    /// <summary>A unit of simulation logic run on a fixed tick interval.</summary>
    public interface ISimulationSystem
    {
        string Name { get; }
        /// <summary>Run every N ticks (1 = hourly, 24 = daily).</summary>
        int IntervalTicks { get; }
        void Tick(WorldState world);
    }

    /// <summary>
    /// Fixed-step simulation loop, independent of rendering frame rate (spec 5.2). The host calls
    /// <see cref="Update"/> with real elapsed seconds; whole ticks are executed from an accumulator.
    /// Every tick runs identically regardless of speed, so acceleration never changes results.
    /// </summary>
    public sealed class SimulationRunner
    {
        /// <summary>Upper bound on ticks per Update so a long frame cannot stall the game.</summary>
        public const int MaxTicksPerUpdate = 48;

        private readonly List<ISimulationSystem> _systems = new List<ISimulationSystem>();
        private readonly List<int> _offsets = new List<int>();
        private double _accumulator;

        public WorldState World { get; }
        public SimSpeed Speed { get; set; } = SimSpeed.Paused;
        /// <summary>Ticks discarded because the host could not keep up (shown as a warning).</summary>
        public long DroppedTicks { get; private set; }

        /// <summary>When true, wall-clock milliseconds per system are accumulated in <see cref="ProfileMs"/>.</summary>
        public bool Profile { get; set; }
        public Dictionary<string, double> ProfileMs { get; } = new Dictionary<string, double>();

        public SimulationRunner(WorldState world) { World = world ?? throw new ArgumentNullException(nameof(world)); }

        public IReadOnlyList<ISimulationSystem> Systems => _systems;

        /// <param name="offsetTicks">
        /// Phase within the interval. Daily systems get consecutive hours so the day's work is spread
        /// over several frames instead of one long frame (important for WebAssembly and mobile).
        /// </param>
        public void AddSystem(ISimulationSystem system, int offsetTicks = 0)
        {
            if (system.IntervalTicks < 1) throw new ArgumentException("IntervalTicks must be >= 1");
            if (offsetTicks < 0 || offsetTicks >= system.IntervalTicks) throw new ArgumentOutOfRangeException(nameof(offsetTicks));
            _systems.Add(system);
            _offsets.Add(offsetTicks);
        }

        public T GetSystem<T>() where T : class, ISimulationSystem
        {
            foreach (ISimulationSystem s in _systems)
                if (s is T t) return t;
            return null;
        }

        /// <summary>In-game hours per real second for each speed.</summary>
        public static double TicksPerSecond(SimSpeed speed)
        {
            switch (speed)
            {
                case SimSpeed.Normal: return 2;
                case SimSpeed.Fast: return 8;
                case SimSpeed.VeryFast: return 32;
                default: return 0;
            }
        }

        /// <returns>Number of ticks executed.</returns>
        public int Update(double realDeltaSeconds)
        {
            if (Speed == SimSpeed.Paused || realDeltaSeconds <= 0) return 0;
            _accumulator += realDeltaSeconds * TicksPerSecond(Speed);
            // Epsilon absorbs float drift: 60 frames of 1/60 s must give exactly the expected ticks.
            int ticks = (int)Math.Floor(_accumulator + 1e-9);
            if (ticks > MaxTicksPerUpdate)
            {
                DroppedTicks += ticks - MaxTicksPerUpdate;
                ticks = MaxTicksPerUpdate;
                _accumulator = 0;
            }
            else
            {
                _accumulator -= ticks;
            }
            for (int i = 0; i < ticks; i++) Step();
            return ticks;
        }

        /// <summary>Executes exactly one tick. Systems run in registration order.</summary>
        public void Step()
        {
            World.AdvanceClock();
            long tick = World.Clock.Tick;
            for (int i = 0; i < _systems.Count; i++)
            {
                ISimulationSystem s = _systems[i];
                if ((tick - _offsets[i]) % s.IntervalTicks != 0) continue;
                if (!Profile) { s.Tick(World); continue; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                s.Tick(World);
                ProfileMs.TryGetValue(s.Name, out double ms);
                ProfileMs[s.Name] = ms + sw.Elapsed.TotalMilliseconds;
            }
        }

        public void StepMany(int ticks)
        {
            for (int i = 0; i < ticks; i++) Step();
        }
    }
}
