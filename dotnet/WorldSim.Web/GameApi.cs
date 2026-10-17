using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.JSInterop;
using WorldSim.Core;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Commands;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Persistence;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Web
{
    /// <summary>
    /// The browser's window into the simulation. Every method is a thin adapter: JSON or base64 in
    /// and out, all rules stay in WorldSim.Core. Called synchronously from JS via DotNet.invokeMethod.
    /// </summary>
    public static partial class GameApi
    {
        private static ContentDatabase _content;
        private static BranchManager _branches;

        private static WorldState World => _branches.Active.World;
        private static SimulationRunner Runner => _branches.Active.Runner;

        // ------------------------------------------------------------------ setup

        [JSInvokable]
        public static string Init(int seed)
        {
            _content ??= LoadContent();
            WorldState w = WorldFactory.Create(_content, new WorldGenOptions { Seed = (ulong)Math.Max(1, seed) });
            _branches = new BranchManager(w, WorldFactory.CreateRunner);
            Runner.Speed = SimSpeed.Normal;
            return Meta();
        }

        private static ContentDatabase LoadContent()
        {
            Assembly asm = typeof(GameApi).Assembly;
            var docs = asm.GetManifestResourceNames().Where(n => n.StartsWith("WorldSimContent.")).OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => new StreamReader(asm.GetManifestResourceStream(n)).ReadToEnd()).ToArray();
            return ContentDatabase.FromJson(docs);
        }

        private static string Meta()
        {
            WorldState w = World;
            var buildings = _content.Buildings.Select(b => (object)new Dictionary<string, object>
            {
                ["id"] = b.Id, ["name"] = b.Name, ["category"] = b.Category.ToString(), ["w"] = b.Width, ["d"] = b.Depth,
                ["h"] = (double)b.Height, ["color"] = b.Color, ["cost"] = (double)b.Cost, ["requiresTech"] = b.RequiresTech,
                ["jobs"] = b.Jobs, ["housing"] = b.Housing, ["recipe"] = b.Recipe, ["sells"] = b.Sells.Cast<object>().ToList(),
            }).ToList();
            var resources = _content.Resources.Select(r => (object)new Dictionary<string, object>
            {
                ["id"] = r.Id, ["name"] = r.Name, ["tier"] = r.Tier.ToString(), ["unit"] = r.Unit, ["price"] = r.BasePrice,
            }).ToList();
            return Json.Serialize(new Dictionary<string, object>
            {
                ["width"] = w.Width, ["depth"] = w.Depth, ["cellSize"] = (double)w.Terrain.CellSize,
                ["seaLevel"] = (double)w.Terrain.SeaLevel, ["buildings"] = buildings, ["resources"] = resources,
                ["policyKeys"] = Policy.Keys.Cast<object>().ToList(), ["series"] = StatsSnapshot.SeriesKeys.Cast<object>().ToList(),
                ["roadCost"] = (double)EconomyState.RoadCostPerCell,
            });
        }

        // ------------------------------------------------------------------ frame loop

        [JSInvokable]
        public static string Tick(double dt)
        {
            int ran = Runner.Update(Math.Min(dt, 0.25));
            WorldState w = World;
            Core.People.AvatarSystem.TryArrive(w, w.Clock.Tick + Runner.TickProgress);
            EconomyState e = w.Economy;
            StatsSnapshot last = w.History.Count > 0 ? w.History[w.History.Count - 1] : null;
            ResearchState rs = e.City.Research;
            TechDef current = rs.Current != null ? _content.GetTech(rs.Current) : null;
            int employed = 0, labour = 0;
            long today = w.Clock.TotalDays;
            double happiness = 0;
            foreach (Citizen c in e.Citizens.Values)
            {
                happiness += c.Happiness;
                if (c.Retired || !c.IsWorkingAge(today)) continue;
                labour++;
                if (c.JobId != 0) employed++;
            }
            return Json.Serialize(new Dictionary<string, object>
            {
                ["ran"] = ran,
                ["tick"] = (double)w.Clock.Tick,
                ["progress"] = Runner.TickProgress,
                ["clock"] = w.Clock.ToString(),
                ["hour"] = w.Clock.HourOfDay,
                ["speed"] = (int)Runner.Speed,
                ["versions"] = w.Versions.Cast<object>().ToList(),
                ["netVersion"] = w.Network.Version,
                ["cash"] = (double)e.City.Cash,
                ["population"] = e.Citizens.Count,
                ["unemployment"] = labour == 0 ? 0 : 1 - (double)employed / labour,
                ["happiness"] = e.Citizens.Count == 0 ? 0 : happiness / e.Citizens.Count,
                ["energy"] = w.Environment.EnergyRatio,
                ["temperature"] = (double)w.Environment.Temperature,
                ["rain"] = (double)w.Environment.RainToday,
                ["floodLevel"] = (double)w.Environment.FloodLevel,
                ["research"] = current == null ? null : new Dictionary<string, object>
                {
                    ["id"] = current.Id, ["name"] = current.Name, ["progress"] = rs.Progress, ["cost"] = current.Cost,
                },
                ["events"] = w.Events.Count,
                ["lastEventTick"] = w.Events.Count > 0 ? (double)w.Events[w.Events.Count - 1].Tick : -1,
                ["dropped"] = (double)Runner.DroppedTicks,
                ["branch"] = _branches.Active.Name,
                ["branchId"] = _branches.Active.Id,
                ["days"] = w.History.Count,
                ["pollution"] = last?.AvgPollution ?? 0,
                ["mode"] = _lifeMode ? "life" : "creator",
                ["player"] = w.Player.CitizenId,
            });
        }

        [JSInvokable]
        public static void SetSpeed(int speed) => Runner.Speed = (SimSpeed)Math.Max(0, Math.Min(4, speed));

        // ------------------------------------------------------------------ world layers

        [JSInvokable]
        public static string GetTerrain() => SaveSystemFloats(World.Terrain.RawHeights);

        [JSInvokable]
        public static string GetVegetation()
        {
            WorldState w = World;
            var bytes = new byte[w.Width * w.Depth];
            for (int z = 0; z < w.Depth; z++)
            for (int x = 0; x < w.Width; x++)
                bytes[z * w.Width + x] = w.GetVegetation(new GridPos(x, z));
            return Convert.ToBase64String(bytes);
        }

        [JSInvokable]
        public static string GetRoads()
        {
            WorldState w = World;
            var bytes = new byte[w.Width * w.Depth];
            for (int z = 0; z < w.Depth; z++)
            for (int x = 0; x < w.Width; x++)
            {
                var c = new GridPos(x, z);
                bytes[z * w.Width + x] = (byte)(!w.HasRoad(c) ? 0 : w.IsRoadDamaged(c) ? 2 : 1);
            }
            return Convert.ToBase64String(bytes);
        }

        [JSInvokable]
        public static string GetBuildings()
        {
            WorldState w = World;
            var list = new List<object>();
            foreach (Building b in w.Buildings)
            {
                w.Terrain.HeightRange(b.Footprint, out float min, out _);
                int state = 0; // 0 ok, 1 closed, 2 broken, 3 unstaffed
                if (w.Economy.Companies.TryGetValue(b.Id, out Company co))
                    state = co.Closed ? 1 : co.BrokenDays > 0 ? 2 : co.Workers.Count == 0 ? 3 : 0;
                list.Add(new List<object> { b.Id, b.Def.Id, b.Origin.X, b.Origin.Z, (double)b.BaseHeight, (double)b.Condition, (double)min, state });
            }
            return Json.Serialize(list);
        }

        /// <summary>Per-cell values for map overlays: traffic, pollution, landValue.</summary>
        [JSInvokable]
        public static string GetOverlay(string kind)
        {
            WorldState w = World;
            var v = new float[w.Width * w.Depth];
            switch (kind)
            {
                case "traffic":
                    for (int i = 0; i < v.Length; i++) v[i] = w.TrafficVolume[i] / Core.Transport.RoadNetwork.CellCapacityPerDay;
                    break;
                case "pollution":
                    for (int z = 0; z < w.Depth; z++)
                    for (int x = 0; x < w.Width; x++)
                        v[z * w.Width + x] = w.Environment.PollutionAt(new GridPos(x, z)) / 40f;
                    break;
                case "landValue":
                    foreach (var kv in CityAnalytics.LandValues(w))
                        if (w.TryGetBuilding(kv.Key, out Building b))
                        {
                            GridRect r = b.Footprint;
                            for (int z = r.MinZ; z < r.MaxZ; z++)
                            for (int x = r.MinX; x < r.MaxX; x++)
                                v[z * w.Width + x] = (float)(kv.Value / 200.0);
                        }
                    break;
            }
            return SaveSystemFloats(v);
        }

        /// <summary>
        /// Commuters on the move right now, as x,z world positions (float32 base64). Morning 7-9
        /// home→work, evening 17-19 work→home, interpolated along their actual road route.
        /// </summary>
        [JSInvokable]
        public static string GetAgents(int max)
        {
            WorldState w = World;
            double hour = w.Clock.HourOfDay + Runner.TickProgress;
            bool morning = hour >= 7 && hour < 9, evening = hour >= 17 && hour < 19;
            var pos = new List<float>();
            if (morning || evening)
            {
                double phase = (hour - (morning ? 7 : 17)) / 2.0;
                float cs = w.Terrain.CellSize;
                int count = 0;
                foreach (Citizen c in w.Economy.Citizens.Values)
                {
                    if (c.JobId == 0 || c.HomeId == 0) continue;
                    if (count++ >= max) break;
                    // Each commuter leaves at a slightly different time.
                    double start = ((uint)(c.Id * 2654435761u) % 1000) / 1000.0 * 0.5;
                    double t = (phase - start) / 0.5;
                    if (t < 0 || t > 1) continue;
                    List<int> route = morning ? w.Network.RouteCells(c.HomeId, c.JobId) : w.Network.RouteCells(c.JobId, c.HomeId);
                    if (route.Count == 0) continue;
                    double f = t * (route.Count - 1);
                    int i0 = (int)Math.Floor(f), i1 = Math.Min(route.Count - 1, i0 + 1);
                    double k = f - i0;
                    float x = (float)(((route[i0] % w.Width) * (1 - k) + (route[i1] % w.Width) * k + 0.5) * cs);
                    float z = (float)(((route[i0] / w.Width) * (1 - k) + (route[i1] / w.Width) * k + 0.5) * cs);
                    pos.Add(x);
                    pos.Add(z);
                }
            }
            return SaveSystemFloats(pos.ToArray());
        }

        // ------------------------------------------------------------------ commands

        [JSInvokable]
        public static string Execute(string json)
        {
            var o = (IDictionary<string, object>)Json.Parse(json);
            IWorldCommand cmd;
            switch (o.GetString("type"))
            {
                case "build": cmd = new PlaceBuildingCommand(o.GetString("def"), P(o, "x", "z")); break;
                case "road": cmd = new BuildRoadCommand(P(o, "x1", "z1"), P(o, "x2", "z2")); break;
                case "bulldoze": cmd = new BulldozeCommand(P(o, "x", "z")); break;
                case "raise": cmd = new RaiseTerrainCommand(o.GetInt("x"), o.GetInt("z"), (float)o.GetDouble("r", 3), (float)o.GetDouble("d")); break;
                case "flatten": cmd = new FlattenTerrainCommand(o.GetInt("x"), o.GetInt("z"), (float)o.GetDouble("r", 4), (float)o.GetDouble("h")); break;
                case "policy": cmd = new SetPolicyCommand(o.GetString("key"), o.GetDouble("value")); break;
                case "research": cmd = new SetResearchCommand(o.GetString("tech")); break;
                case "disaster":
                    cmd = new TriggerDisasterCommand(o.GetString("kind") == "flood" ? DisasterKind.Flood : DisasterKind.Earthquake,
                        P(o, "x", "z"), o.GetDouble("mag"));
                    break;
                default: return Result(CommandResult.Fail("不明な操作です"));
            }
            return Result(CommandGate.Execute(World, CurrentActor, cmd));
        }

        [JSInvokable]
        public static string CanPlace(string defId, int x, int z)
        {
            if (!_content.TryGetBuilding(defId, out BuildingDef def)) return Result(CommandResult.Fail("未定義の建物"));
            CommandResult r = World.CanPlaceBuilding(def, new GridPos(x, z));
            if (r.Success && World.Economy.City.Cash < def.Cost) r = CommandResult.Fail("予算不足です");
            if (r.Success && !World.HasRoadAccess(GridRect.FromSize(new GridPos(x, z), def.Width, def.Depth)))
                return Json.Serialize(new Dictionary<string, object> { ["ok"] = true, ["msg"] = "道路に接していません（通勤・物流ができません）", ["warn"] = true });
            return Result(r);
        }

        /// <summary>
        /// Nearest origin (ring by ring, up to <paramref name="radius"/> cells) where the building can be
        /// placed, preferring spots that touch a road. Returns {found, x, z, warn, msg}.
        /// </summary>
        [JSInvokable]
        public static string FindSpot(string defId, int x, int z, int radius)
        {
            if (!_content.TryGetBuilding(defId, out BuildingDef def)
                || !World.FindBuildableSpot(def, x, z, radius, out GridPos o, out bool road)) return "{\"found\":false}";
            return Json.Serialize(new Dictionary<string, object> { ["found"] = true, ["x"] = o.X, ["z"] = o.Z, ["warn"] = !road });
        }

        [JSInvokable]
        public static string RoadPreview(int x1, int z1, int x2, int z2)
        {
            WorldState w = World;
            var cells = WorldState.RoadPath(new GridPos(x1, z1), new GridPos(x2, z2));
            int fresh = cells.Count(c => w.Terrain.InBounds(c) && !w.HasRoad(c));
            return Json.Serialize(new Dictionary<string, object>
            {
                ["ok"] = w.CanBuildRoad(new GridPos(x1, z1), new GridPos(x2, z2)),
                ["cells"] = cells.SelectMany(c => new object[] { c.X, c.Z }).ToList(),
                ["cost"] = (double)(fresh * EconomyState.RoadCostPerCell),
            });
        }

        // ------------------------------------------------------------------ inspection & analysis

        [JSInvokable]
        public static string Inspect(int x, int z)
        {
            WorldState w = World;
            var c = new GridPos(x, z);
            if (!w.Terrain.InBounds(c)) return "null";
            var d = new Dictionary<string, object>
            {
                ["x"] = x, ["z"] = z,
                ["height"] = (double)w.Terrain.CellMinHeight(c),
                ["pollution"] = (double)w.Environment.PollutionAt(c),
                ["forest"] = (int)w.GetVegetation(c),
            };
            if (w.HasRoad(c))
                d["road"] = new Dictionary<string, object>
                {
                    ["traffic"] = (double)w.TrafficVolume[z * w.Width + x],
                    ["capacity"] = (double)Core.Transport.RoadNetwork.CellCapacityPerDay,
                    ["damaged"] = w.IsRoadDamaged(c),
                };
            Building b = w.GetBuildingAt(c);
            if (b != null)
            {
                var bd = new Dictionary<string, object>
                {
                    ["id"] = b.Id, ["def"] = b.Def.Id, ["name"] = b.Def.Name, ["category"] = b.Def.Category.ToString(),
                    ["condition"] = (double)b.Condition, ["roadAccess"] = w.Network.HasAccess(b.Id),
                };
                if (b.Def.Housing > 0)
                {
                    var residents = w.Economy.Citizens.Values.Where(r => r.HomeId == b.Id).ToList();
                    long today = w.Clock.TotalDays;
                    bd["housing"] = b.Def.Housing;
                    bd["residents"] = residents.Count;
                    bd["employed"] = residents.Count(r => r.JobId != 0);
                    bd["children"] = residents.Count(r => r.AgeYears(today) < 18);
                    bd["retired"] = residents.Count(r => r.Retired);
                    bd["happiness"] = residents.Count == 0 ? 0 : residents.Average(r => r.Happiness);
                    bd["commute"] = residents.Where(r => r.JobId != 0).Select(r => (double)r.CommuteHours).DefaultIfEmpty(0).Average() * 60;
                    bd["savings"] = residents.Count == 0 ? 0 : residents.Average(r => (double)r.Cash);
                    Citizen sample = residents.FirstOrDefault();
                    if (sample != null)
                        bd["sample"] = new Dictionary<string, object>
                        {
                            ["id"] = sample.Id, ["age"] = sample.AgeYears(today), ["education"] = sample.Education,
                            ["job"] = sample.JobId != 0 && w.TryGetBuilding(sample.JobId, out Building jb) ? jb.Def.Name + " #" + jb.Id : (sample.Retired ? "退職" : "なし"),
                            ["income"] = (double)sample.LastNetIncome, ["savings"] = (double)sample.Cash,
                            ["happiness"] = (double)sample.Happiness, ["commute"] = sample.CommuteHours * 60.0,
                            ["food"] = (double)sample.FoodMet, ["goods"] = (double)sample.GoodsMet,
                        };
                }
                if (w.Economy.Companies.TryGetValue(b.Id, out Company co))
                    bd["company"] = new Dictionary<string, object>
                    {
                        ["public"] = co.IsPublic, ["workers"] = co.Workers.Count, ["target"] = co.TargetWorkers,
                        ["capacity"] = w.Effects.JobCapacity(co), ["wage"] = (double)co.Wage, ["cash"] = (double)co.Cash,
                        ["profitAvg"] = co.ProfitAvg, ["closed"] = co.Closed, ["broken"] = co.BrokenDays,
                        ["output"] = co.LastOutput, ["outputCapacity"] = co.OutputCapacity, ["shortage"] = co.InputShortage,
                        ["energy"] = co.EnergyUse, ["recipe"] = co.Recipe?.Id,
                        ["contractor"] = co.IsContractor, ["backlog"] = (double)co.Backlog, ["workPerDay"] = (double)EconomyState.WorkCapacity(co),
                        ["inventory"] = co.Inventory.ToDictionary(kv => kv.Key, kv => (object)Math.Round(kv.Value)),
                        ["techs"] = co.AdoptedTechs.Cast<object>().ToList(),
                    };
                d["building"] = bd;
            }
            return Json.Serialize(d);
        }

        [JSInvokable]
        public static string Analysis()
        {
            WorldState w = World;
            var findings = CityAnalytics.Diagnose(w).Select(f => (object)new Dictionary<string, object>
            {
                ["severity"] = f.Severity.ToString(), ["title"] = f.Title, ["detail"] = f.Detail,
            }).ToList();
            var forecast = new Dictionary<string, object>();
            foreach (string k in new[] { "population", "treasury", "unemployment", "happiness" })
                forecast[k] = new List<object>
                {
                    w.History.Count > 0 ? w.History[w.History.Count - 1].Get(k) : 0,
                    CityAnalytics.Forecast(w.History, k, 90),
                };
            var s = w.History.Count > 0 ? w.History[w.History.Count - 1] : StatsHistorySystem.Capture(w);
            var market = _content.Resources.Select(r => (object)new Dictionary<string, object>
            {
                ["id"] = r.Id, ["name"] = r.Name, ["price"] = w.Economy.Market.GetPrice(r.Id), ["base"] = r.BasePrice,
                ["produced"] = s.Production.TryGetValue(r.Id, out double p) ? p : 0,
                ["imported"] = s.Imported.TryGetValue(r.Id, out double im) ? im : 0,
                ["unmet"] = s.Unmet.TryGetValue(r.Id, out double u) ? u : 0,
            }).ToList();
            var ledger = new Dictionary<string, object>();
            foreach (Flow f in Enum.GetValues(typeof(Flow))) ledger[f.ToString()] = (double)w.Economy.Ledger.Yesterday[(int)f];
            return Json.Serialize(new Dictionary<string, object>
            {
                ["findings"] = findings, ["forecast"] = forecast, ["market"] = market, ["flows"] = ledger,
                ["summary"] = new Dictionary<string, object>
                {
                    ["population"] = s.Population, ["labour"] = s.LabourForce, ["employed"] = s.Employed, ["jobs"] = s.Jobs,
                    ["vacancies"] = s.Vacancies, ["companies"] = s.CompaniesOpen, ["closed"] = s.CompaniesClosed,
                    ["avgWage"] = s.AvgWage, ["commute"] = s.AvgCommuteHours * 60, ["energyDemand"] = s.EnergyDemand,
                    ["energySupply"] = s.EnergySupply, ["traffic"] = s.TrafficTotal, ["congested"] = s.CongestedCells,
                    ["resilience"] = DisasterSystem.Resilience(w), ["warming"] = w.Environment.Warming,
                },
                ["problems"] = WorldValidator.Validate(w).Cast<object>().ToList(),
            });
        }

        [JSInvokable]
        public static string Series(string keysCsv, int lastDays)
        {
            WorldState w = World;
            int start = Math.Max(0, w.History.Count - Math.Max(1, lastDays));
            var series = new Dictionary<string, object>();
            foreach (string key in keysCsv.Split(','))
            {
                var col = new List<object>();
                for (int i = start; i < w.History.Count; i++) col.Add(w.History[i].Get(key));
                series[key] = col;
            }
            return Json.Serialize(new Dictionary<string, object> { ["startDay"] = start + 1, ["series"] = series });
        }

        [JSInvokable]
        public static string GetPolicy() => PolicyJson();

        private static string PolicyJson()
        {
            var d = new Dictionary<string, object>();
            foreach (string k in Policy.Keys) d[k] = World.Economy.City.Policy.Get(k);
            return Json.Serialize(d);
        }

        [JSInvokable]
        public static string GetResearch()
        {
            WorldState w = World;
            ResearchState rs = w.Economy.City.Research;
            var techs = _content.Techs.Select(t => (object)new Dictionary<string, object>
            {
                ["id"] = t.Id, ["name"] = t.Name, ["description"] = t.Description, ["cost"] = t.Cost,
                ["prereqs"] = t.Prereqs.Cast<object>().ToList(), ["adoptionCost"] = (double)t.AdoptionCost,
                ["done"] = rs.Completed.Contains(t.Id), ["available"] = ResearchSystem.Available(w, t.Id),
                ["current"] = rs.Current == t.Id, ["progress"] = rs.Current == t.Id ? rs.Progress : 0,
                ["adopters"] = w.Economy.Companies.Values.Count(c => c.AdoptedTechs.Contains(t.Id)),
            }).ToList();
            return Json.Serialize(new Dictionary<string, object> { ["points"] = rs.PointsYesterday, ["techs"] = techs });
        }

        [JSInvokable]
        public static string GetEvents(int since)
        {
            var list = World.Events.Skip(Math.Max(0, since)).Select(e => (object)new List<object> { (double)e.Tick, e.Kind, e.Message }).ToList();
            return Json.Serialize(new Dictionary<string, object> { ["total"] = World.Events.Count, ["events"] = list });
        }

        // ------------------------------------------------------------------ persistence & branches

        [JSInvokable]
        public static string SaveAll() => _branches.SaveAll();

        [JSInvokable]
        public static string LoadAll(string json)
        {
            try
            {
                _content ??= LoadContent();
                _branches = BranchManager.LoadAll(_content, json, WorldFactory.CreateRunner);
                Runner.Speed = SimSpeed.Paused;
                return Json.Serialize(new Dictionary<string, object> { ["ok"] = true, ["meta"] = Json.Parse(Meta()) });
            }
            catch (Exception ex)
            {
                return Json.Serialize(new Dictionary<string, object> { ["ok"] = false, ["msg"] = ex.Message });
            }
        }

        [JSInvokable]
        public static string Branches()
        {
            var list = _branches.Branches.Select(b => (object)new Dictionary<string, object>
            {
                ["id"] = b.Id, ["name"] = b.Name, ["parent"] = b.ParentId, ["forkDay"] = (double)(b.ForkTick / SimClock.TicksPerDay),
                ["clock"] = b.World.Clock.ToString(), ["days"] = b.World.History.Count, ["active"] = b == _branches.Active,
                ["population"] = b.World.Economy.Citizens.Count, ["cash"] = (double)b.World.Economy.City.Cash,
            }).ToList();
            return Json.Serialize(list);
        }

        [JSInvokable]
        public static string Fork(string name)
        {
            try { return Json.Serialize(new Dictionary<string, object> { ["ok"] = true, ["id"] = _branches.Fork(name).Id }); }
            catch (Exception ex) { return Json.Serialize(new Dictionary<string, object> { ["ok"] = false, ["msg"] = ex.Message }); }
        }

        [JSInvokable]
        public static void SwitchBranch(int id) => _branches.Switch(id);

        [JSInvokable]
        public static void AdvanceBranch(int id, int days) => _branches.Advance(id, Math.Max(1, Math.Min(365, days)));

        [JSInvokable]
        public static string RemoveBranch(int id)
        {
            try { _branches.Remove(id); return Result(CommandResult.Ok("削除しました")); }
            catch (Exception ex) { return Result(CommandResult.Fail(ex.Message)); }
        }

        [JSInvokable]
        public static string Compare(string key)
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in _branches.Compare(key)) d[kv.Key.ToString()] = kv.Value.Cast<object>().ToList();
            return Json.Serialize(d);
        }

        // ------------------------------------------------------------------ helpers

        private static GridPos P(IDictionary<string, object> o, string x, string z) => new GridPos(o.GetInt(x), o.GetInt(z));

        private static string Result(CommandResult r) => Json.Serialize(new Dictionary<string, object>
        {
            ["ok"] = r.Success, ["msg"] = r.Message, ["id"] = r.CreatedId,
        });

        private static string SaveSystemFloats(float[] values)
        {
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }
    }
}
