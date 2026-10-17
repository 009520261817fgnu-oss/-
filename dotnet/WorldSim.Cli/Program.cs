using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WorldSim.Core;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Commands;
using WorldSim.Core.Content;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

// Headless runner for the WorldSim core.
//   dotnet run --project WorldSim.Cli -- [--seed N] [--days N] [--png map.png] [--demo-edits]
ulong seed = 1;
int days = 7;
string png = null;
bool demoEdits = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed": seed = ulong.Parse(args[++i]); break;
        case "--days": days = int.Parse(args[++i]); break;
        case "--png": png = args[++i]; break;
        case "--demo-edits": demoEdits = true; break;
        default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
    }
}

string contentDir = Path.Combine(AppContext.BaseDirectory, "WorldSimContent");
var docs = Directory.GetFiles(contentDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText).ToArray();
ContentDatabase content = ContentDatabase.FromJson(docs);

var sw = Stopwatch.StartNew();
WorldState world = WorldFactory.Create(content, new WorldGenOptions { Seed = seed });
double genMs = sw.Elapsed.TotalMilliseconds;
SimulationRunner runner = WorldFactory.CreateRunner(world);

Console.WriteLine($"World seed={seed} size={world.Width}x{world.Depth} cells ({world.Width * world.Terrain.CellSize / 1000:0.#} km square), generated in {genMs:0} ms");
PrintStats(StatsHistorySystem.Capture(world));

if (demoEdits)
{
    // Player-style edits through the same command path the Unity UI uses.
    int cx = world.Width / 2, cz = world.Depth / 2;
    IWorldCommand[] edits =
    {
        new FlattenTerrainCommand(cx + 30, cz, 6, world.Terrain.GetVertexHeight(cx + 30, cz)),
        new PlaceBuildingCommand("factory", new GridPos(cx + 28, cz - 2)),
        new BuildRoadCommand(new GridPos(cx + 21, cz - 3), new GridPos(cx + 33, cz - 3)),
        new RaiseTerrainCommand(cx - 20, cz + 30, 5, 8f),
        new PlaceBuildingCommand("factory", new GridPos(cx, cz)), // occupied: must fail
    };
    foreach (IWorldCommand cmd in edits) Console.WriteLine($"  {cmd.GetType().Name,-22} -> {cmd.Execute(world)}");
}

sw.Restart();
runner.StepMany(days * SimClock.TicksPerDay);
double simMs = sw.Elapsed.TotalMilliseconds;
Console.WriteLine($"Ran {days} days ({days * SimClock.TicksPerDay} ticks) in {simMs:0.0} ms -> clock {world.Clock}");
var history = runner.GetSystem<StatsHistorySystem>().History;
Console.WriteLine($"Daily snapshots recorded: {history.Count}");
PrintStats(history.Count > 0 ? history[history.Count - 1] : StatsHistorySystem.Capture(world));
Console.WriteLine($"State hash: {world.ComputeHash():X16}");

if (png != null)
{
    MapRenderer.Save(world, png, 4);
    Console.WriteLine($"Map written to {Path.GetFullPath(png)}");
}
return 0;

static void PrintStats(StatsSnapshot s)
{
    Console.WriteLine($"  buildings={s.Buildings} roadCells={s.RoadCells} " +
        string.Join(" ", s.ByCategory.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
}
