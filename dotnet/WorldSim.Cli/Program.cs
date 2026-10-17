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
bool companies = false;
bool profile = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed": seed = ulong.Parse(args[++i]); break;
        case "--days": days = int.Parse(args[++i]); break;
        case "--png": png = args[++i]; break;
        case "--demo-edits": demoEdits = true; break;
        case "--companies": companies = true; break;
        case "--profile": profile = true; break;
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

runner.Profile = profile;
sw.Restart();
runner.StepMany(days * SimClock.TicksPerDay);
double simMs = sw.Elapsed.TotalMilliseconds;
Console.WriteLine($"Ran {days} days ({days * SimClock.TicksPerDay} ticks) in {simMs:0.0} ms -> clock {world.Clock}");
var history = world.History;
Console.WriteLine($"Daily snapshots recorded: {history.Count}");
int every = Math.Max(1, history.Count / 12);
for (int i = every - 1; i < history.Count; i += every) PrintStats(history[i]);
foreach (var ev in world.Events.TakeLast(15)) Console.WriteLine($"  [{ev.Tick / 24}日] {ev.Kind}: {ev.Message}");
if (profile)
    foreach (var kv in runner.ProfileMs.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  {kv.Key,-12} {kv.Value / days,8:0.000} ms/day");
if (companies)
{
    foreach (var g in world.Economy.Companies.Values.GroupBy(c => c.Def.Id))
    {
        var list = g.ToList();
        Console.WriteLine($"  {g.Key,-12} n={list.Count,2} open={list.Count(c => !c.Closed),2} workers={list.Sum(c => c.Workers.Count),4}/{list.Sum(c => c.Def.Jobs),4} " +
            $"target={list.Sum(c => c.TargetWorkers),4} profitAvg={list.Average(c => c.ProfitAvg),9:0} cash={list.Average(c => (double)c.Cash) / 1e6,6:0.0}M wage={list.Average(c => (double)c.Wage),5:0} " +
            $"shortage={list.Count(c => c.InputShortage)} stock: " + string.Join(",", list.SelectMany(c => c.Inventory).GroupBy(kv => kv.Key).Select(x => $"{x.Key}={x.Sum(kv => kv.Value):0}")));
    }
}
Console.WriteLine($"State hash: {world.ComputeHash():X16}");

if (png != null)
{
    MapRenderer.Save(world, png, 4);
    Console.WriteLine($"Map written to {Path.GetFullPath(png)}");
}
return 0;

static void PrintStats(StatsSnapshot s)
{
    Console.WriteLine($"  day={s.Tick / 24} buildings={s.Buildings} roads={s.RoadCells} pop={s.Population} labour={s.LabourForce} employed={s.Employed} " +
        $"unemp={s.UnemploymentRate:P1} jobs={s.Jobs} vac={s.Vacancies} happy={s.AvgHappiness:0.00} commute={s.AvgCommuteHours * 60:0.0}min");
    Console.WriteLine($"    treasury={s.Treasury / 1e6:0.0}M companies={s.CompaniesOpen}/{s.CompaniesClosed}closed cash={s.CompanyCash / 1e6:0.0}M hh={s.HouseholdCash / 1e6:0.0}M " +
        $"wage={s.AvgWage:0} wages={s.Wages / 1e6:0.00}M retail={s.RetailSales / 1e6:0.00}M imp={s.Imports / 1e6:0.00}M exp={s.Exports / 1e6:0.00}M tax={s.Taxes / 1e6:0.00}M profit={s.CompanyProfit / 1e6:0.00}M");
    Console.WriteLine($"    prod: " + string.Join(" ", s.Production.Select(kv => $"{kv.Key}={kv.Value:0}")) +
        " | price: " + string.Join(" ", s.Prices.Select(kv => $"{kv.Key}={kv.Value:0}")));
    Console.WriteLine($"    energy={s.EnergySupply:0}/{s.EnergyDemand:0} pollution={s.AvgPollution:0.0} temp={s.Temperature:0.0} traffic={s.TrafficTotal:0} congested={s.CongestedCells} " +
        $"research={s.ResearchPoints:0.0} techs={s.TechsCompleted} damaged={s.DamagedBuildings}/{s.DamagedRoads} births={s.Births} deaths={s.Deaths} in={s.Immigrants} out={s.Emigrants} totalMoney={s.TotalMoney}");
}
