using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Content;
using WorldSim.Core.Persistence;
using WorldSim.Core.World;

namespace WorldSim.Core.Simulation
{
    /// <summary>A world size used for benchmarking (S5-0). S is the default town.</summary>
    public sealed class BenchPreset
    {
        public string Name;
        public int MapCells;
        public int TownRadius;

        public static readonly BenchPreset[] All =
        {
            new BenchPreset { Name = "S", MapCells = 128, TownRadius = 3 },
            new BenchPreset { Name = "M", MapCells = 192, TownRadius = 5 },
            new BenchPreset { Name = "L", MapCells = 256, TownRadius = 7 },
            new BenchPreset { Name = "XL", MapCells = 320, TownRadius = 9 },
        };

        public WorldGenOptions Options(ulong seed) => new WorldGenOptions
        {
            Seed = seed,
            Terrain = new TerrainSettings { Width = MapCells, Depth = MapCells },
            TownBlocksRadius = TownRadius,
        };
    }

    /// <summary>Measured numbers for one preset; all times in milliseconds.</summary>
    public sealed class BenchResult
    {
        public string Preset;
        public int Regions = 1, Population, Companies, Buildings, RoadCells;
        public double MsPerDay, GenerateMs, SaveMs, LoadMs, MemoryMB, SaveKB, Unemployment;
        public bool ResumeMatches;
        public SortedDictionary<string, double> SystemMsPerDay = new SortedDictionary<string, double>(StringComparer.Ordinal);

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["preset"] = Preset, ["regions"] = Regions, ["population"] = Population, ["companies"] = Companies,
            ["buildings"] = Buildings, ["roadCells"] = RoadCells, ["msPerDay"] = Math.Round(MsPerDay, 2),
            ["generateMs"] = Math.Round(GenerateMs), ["saveMs"] = Math.Round(SaveMs), ["loadMs"] = Math.Round(LoadMs),
            ["memoryMB"] = Math.Round(MemoryMB, 1), ["saveKB"] = Math.Round(SaveKB), ["unemployment"] = Math.Round(Unemployment, 4),
            ["resumeMatches"] = ResumeMatches,
            ["systemMsPerDay"] = SystemMsPerDay.ToDictionary(kv => kv.Key, kv => (object)Math.Round(kv.Value, 2)),
        };
    }

    /// <summary>
    /// Benchmarks (S5-0): generate a preset world, warm it up, then time simulated days. The median
    /// of several timed blocks is reported so one slow block (the first reroute, a GC) does not
    /// dominate. Also measures memory, save size and save/load time, and checks that a loaded copy
    /// continues exactly like the original.
    /// </summary>
    public static class Benchmark
    {
        public static BenchResult Run(ContentDatabase content, BenchPreset preset, ulong seed = 1, int warmUpDays = 30, int blockDays = 20, int blocks = 3)
        {
            var sw = Stopwatch.StartNew();
            WorldState w = WorldFactory.Create(content, preset.Options(seed));
            var r = new BenchResult { Preset = preset.Name, GenerateMs = sw.Elapsed.TotalMilliseconds };
            SimulationRunner runner = WorldFactory.CreateRunner(w);
            runner.StepMany(warmUpDays * SimClock.TicksPerDay);

            var times = new List<double>();
            runner.Profile = true;
            for (int i = 0; i < blocks; i++)
            {
                sw.Restart();
                runner.StepMany(blockDays * SimClock.TicksPerDay);
                times.Add(sw.Elapsed.TotalMilliseconds / blockDays);
            }
            times.Sort();
            r.MsPerDay = times[times.Count / 2];
            foreach (var kv in runner.ProfileMs) r.SystemMsPerDay[kv.Key] = kv.Value / (blockDays * blocks);

            GC.Collect();
            r.MemoryMB = GC.GetTotalMemory(true) / 1048576.0;
            sw.Restart();
            string save = SaveSystem.Save(w);
            r.SaveMs = sw.Elapsed.TotalMilliseconds;
            r.SaveKB = save.Length / 1024.0;
            sw.Restart();
            WorldState loaded = SaveSystem.Load(content, save);
            r.LoadMs = sw.Elapsed.TotalMilliseconds;
            WorldFactory.CreateRunner(w).StepMany(SimClock.TicksPerDay * 3);
            WorldFactory.CreateRunner(loaded).StepMany(SimClock.TicksPerDay * 3);
            r.ResumeMatches = loaded.ComputeHash() == w.ComputeHash();

            StatsSnapshot s = w.History.Last();
            r.Population = s.Population;
            r.Companies = w.Economy.Companies.Count;
            r.Buildings = s.Buildings;
            r.RoadCells = s.RoadCells;
            r.Unemployment = s.UnemploymentRate;
            return r;
        }

        public static string Table(IEnumerable<BenchResult> results)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("規模  人口    企業  建物  道路   1日(ms)  メモリ(MB)  保存(KB)  保存/読込(ms)  失業率  再開一致  重いシステム");
            foreach (BenchResult r in results)
            {
                string top = string.Join(" ", r.SystemMsPerDay.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key}={kv.Value:0.0}"));
                sb.AppendLine($"{r.Preset,-4}{r.Population,6}{r.Companies,6}{r.Buildings,6}{r.RoadCells,6}{r.MsPerDay,10:0.0}{r.MemoryMB,11:0.0}{r.SaveKB,10:0}{r.SaveMs,8:0}/{r.LoadMs,-6:0}{r.Unemployment,8:P0}  {(r.ResumeMatches ? "はい" : "いいえ"),-6}  {top}");
            }
            return sb.ToString();
        }
    }
}
