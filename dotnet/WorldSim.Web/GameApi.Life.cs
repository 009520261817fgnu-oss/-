using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.JSInterop;
using WorldSim.Core;
using WorldSim.Core.Commands;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.People;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Web
{
    /// <summary>
    /// Life mode for the browser. The mode decides the actor (what the player may do); the camera
    /// is a separate, purely visual choice made in JavaScript.
    /// </summary>
    public static partial class GameApi
    {
        private static bool _lifeMode;

        private static Actor CurrentActor =>
            _lifeMode && World.Player.IsActive ? Actor.Resident(World.Player.CitizenId) : _lifeMode ? Actor.Resident(0) : Actor.Creator;

        /// <summary>"creator" or "life". Switching never pauses the world.</summary>
        [JSInvokable]
        public static void SetMode(string mode) => _lifeMode = mode == "life";

        [JSInvokable]
        public static string LifeExecute(string json)
        {
            var o = (IDictionary<string, object>)Json.Parse(json);
            object cmd;
            switch (o.GetString("type"))
            {
                case "possess": cmd = new PossessCitizenCommand(o.GetInt("id")); break;
                case "create": cmd = new CreateResidentCommand(o.GetInt("age"), o.GetInt("education")); break;
                case "release": cmd = new ReleaseCitizenCommand(); break;
                case "walk": cmd = new WalkCommand((float)o.GetDouble("x"), (float)o.GetDouble("z"), (float)o.GetDouble("h")); break;
                case "enter": cmd = new EnterBuildingCommand(o.GetInt("id")); break;
                case "exit": cmd = new ExitBuildingCommand(); break;
                case "travel": cmd = new TravelCommand(o.GetInt("id")); break;
                case "use": cmd = new UseFixtureCommand(o.GetString("fixture")); break;
                case "buy": cmd = new BuyCommand(o.GetString("res"), o.GetDouble("qty", 1)); break;
                case "apply": cmd = new ApplyJobCommand(o.GetInt("id")); break;
                case "quit": cmd = new QuitJobCommand(); break;
                case "move": cmd = new MoveHomeCommand(o.GetInt("id")); break;
                case "talk": cmd = new TalkCommand(o.GetInt("id")); break;
                case "stop": cmd = new StopActivityCommand(); break;
                default: return Result(CommandResult.Fail("不明な操作です"));
            }
            // Choosing whom to live as is allowed from either mode; personal actions need the resident actor.
            Actor actor = cmd is IResidentCommand ? Actor.Resident(World.Player.CitizenId) : CurrentActor;
            return Result(CommandGate.Execute(World, actor, cmd));
        }

        /// <summary>Everything the life-mode HUD needs, in one call.</summary>
        [JSInvokable]
        public static string LifeState()
        {
            WorldState w = World;
            PlayerState p = w.Player;
            if (!p.IsActive || !w.Economy.Citizens.TryGetValue(p.CitizenId, out Citizen c))
                return Json.Serialize(new Dictionary<string, object> { ["active"] = false });
            long today = w.Clock.TotalDays;
            var d = new Dictionary<string, object>
            {
                ["active"] = true,
                ["id"] = c.Id, ["name"] = Names.Of(c.Id), ["age"] = c.AgeYears(today), ["education"] = c.Education,
                ["cash"] = (double)c.Cash, ["happiness"] = (double)c.Happiness, ["retired"] = c.Retired,
                ["home"] = BuildingRef(w, c.HomeId), ["job"] = BuildingRef(w, c.JobId),
                ["level"] = c.Level, ["tenure"] = c.TenureDays, ["commute"] = c.JobId != 0 && c.HomeId != 0 ? w.Network.TravelHours(c.HomeId, c.JobId) * 60.0 : 0,
                ["x"] = (double)p.X, ["z"] = (double)p.Z, ["heading"] = (double)p.Heading,
                ["inside"] = p.InsideBuildingId, ["roomX"] = (double)p.RoomX, ["roomZ"] = (double)p.RoomZ,
                ["activity"] = p.Activity.ToString(), ["hunger"] = (double)p.Hunger, ["energy"] = (double)p.Energy,
                ["rent"] = (double)c.LastRent, ["rentDue"] = c.HomeId != 0 ? (double)HousingMarket.RentFor(w, c.HomeId) : 0, ["arrears"] = c.RentArrearsDays,
                ["pantry"] = p.Pantry, ["goods"] = p.GoodsOwned, ["worked"] = (double)p.WorkedHours, ["absence"] = p.AbsenceDays,
                ["study"] = p.StudyHours, ["studyNeeded"] = AvatarSystem.StudyHoursPerLevel,
                ["travelTo"] = BuildingRef(w, p.TravelTo), ["arrive"] = p.TravelArrive,
                ["workday"] = DailySchedule.IsWorkday(today),
            };
            if (c.JobId != 0 && w.Economy.Companies.TryGetValue(c.JobId, out Company job))
                d["wage"] = (double)Math.Round(job.Wage * c.WageFactor);
            if (p.InsideBuildingId != 0 && w.TryGetBuilding(p.InsideBuildingId, out Building inside))
            {
                InteriorDef room = w.Content.InteriorFor(inside.Def) ?? new InteriorDef { Id = "default" };
                d["room"] = new Dictionary<string, object>
                {
                    ["id"] = room.Id, ["width"] = (double)room.Width, ["depth"] = (double)room.Depth,
                    ["doorX"] = (double)room.DoorX, ["doorZ"] = (double)room.DoorZ, ["building"] = inside.Def.Name,
                    ["fixtures"] = room.Fixtures.Select(f => (object)new Dictionary<string, object>
                    {
                        ["type"] = f.Type, ["label"] = f.Label ?? f.Type, ["x"] = (double)f.X, ["z"] = (double)f.Z, ["w"] = (double)f.W, ["d"] = (double)f.D,
                    }).ToList(),
                };
                d["occupants"] = DailySchedule.Occupants(w, inside.Id, c.Id).Take(12).Select(o => (object)Person(w, c, o)).ToList();
                if (w.Economy.Companies.TryGetValue(inside.Id, out Company shop) && shop.IsRetail)
                    d["shop"] = shop.Def.Sells.Select(r => (object)new Dictionary<string, object>
                    {
                        ["res"] = r, ["name"] = w.Content.GetResource(r).Name, ["unit"] = w.Content.GetResource(r).Unit,
                        ["price"] = Math.Round(BuyCommand.UnitPrice(w, shop, r)), ["stock"] = Math.Floor(shop.Stock(r)),
                    }).ToList();
            }
            else if (p.Activity != AvatarActivity.Travelling)
            {
                int near = TravelCommand.NearestBuilding(w, p.X, p.Z);
                if (near != 0 && w.TryGetBuilding(near, out Building nb))
                {
                    var (dx, dz, _, _) = w.DoorOf(nb);
                    double dist = Math.Sqrt((dx - p.X) * (dx - p.X) + (dz - p.Z) * (dz - p.Z));
                    if (dist <= EnterBuildingCommand.DoorReach)
                        d["door"] = new Dictionary<string, object>
                        {
                            ["id"] = nb.Id, ["name"] = nb.Def.Name, ["denied"] = EnterBuildingCommand.AccessDenied(w, c, nb),
                        };
                }
            }
            d["family"] = w.Economy.Citizens.Values.Where(o => o.Id != c.Id && o.HomeId != 0 && o.HomeId == c.HomeId)
                .Take(8).Select(o => (object)Person(w, c, o)).ToList();
            d["friends"] = (c.Relations ?? new SortedDictionary<int, float>()).OrderByDescending(kv => kv.Value).Take(8)
                .Where(kv => w.Economy.Citizens.ContainsKey(kv.Key)).Select(kv => (object)Person(w, c, w.Economy.Citizens[kv.Key])).ToList();
            return Json.Serialize(d);
        }

        private static Dictionary<string, object> Person(WorldState w, Citizen me, Citizen o)
        {
            var (act, _) = DailySchedule.At(w, o, w.Clock.HourOfDay, w.Clock.TotalDays);
            return new Dictionary<string, object>
            {
                ["id"] = o.Id, ["name"] = Names.Of(o.Id), ["age"] = o.AgeYears(w.Clock.TotalDays),
                ["job"] = o.JobId != 0 && w.TryGetBuilding(o.JobId, out Building jb) ? jb.Def.Name : o.Retired ? "退職" : o.AgeYears(w.Clock.TotalDays) < 18 ? "学生" : "無職",
                ["activity"] = act.ToString(), ["affinity"] = (double)me.Affinity(o.Id),
            };
        }

        private static object BuildingRef(WorldState w, int id)
        {
            if (id == 0 || !w.TryGetBuilding(id, out Building b)) return null;
            return new Dictionary<string, object> { ["id"] = b.Id, ["name"] = b.Def.Name, ["category"] = b.Def.Category.ToString() };
        }

        /// <summary>Open positions with the person's own eligibility and commute.</summary>
        [JSInvokable]
        public static string Jobs()
        {
            WorldState w = World;
            w.Economy.Citizens.TryGetValue(w.Player.CitizenId, out Citizen c);
            var list = new List<object>();
            foreach (Company co in w.Economy.Companies.Values)
            {
                if (co.Closed || co.Workers.Count >= Math.Min(co.TargetWorkers, w.Effects.JobCapacity(co))) continue;
                if (c != null && co.BuildingId == c.JobId) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = co.BuildingId, ["name"] = co.Def.Name, ["category"] = co.Def.Category.ToString(), ["wage"] = (double)co.Wage,
                    ["openings"] = Math.Min(co.TargetWorkers, w.Effects.JobCapacity(co)) - co.Workers.Count,
                    ["commute"] = c != null && c.HomeId != 0 ? Math.Round(w.Network.TravelHours(c.HomeId, co.BuildingId) * 60, 1) : -1,
                    ["education"] = co.Def.GetParam("minEducation"),
                    ["problem"] = c == null ? null : LaborSystem.Ineligible(w, c, co),
                });
            }
            return Json.Serialize(list.OrderByDescending(x => (double)((Dictionary<string, object>)x)["wage"]).ToList());
        }

        /// <summary>Vacant homes with the commute they would give.</summary>
        [JSInvokable]
        public static string Homes()
        {
            WorldState w = World;
            w.Economy.Citizens.TryGetValue(w.Player.CitizenId, out Citizen c);
            var lv = CityAnalytics.LandValues(w);
            var list = new List<object>();
            foreach (Building b in w.Buildings)
            {
                if (b.Def.Housing == 0 || b.Condition < 0.5f || !w.Network.HasAccess(b.Id)) continue;
                int free = b.Def.Housing - w.Economy.Residents(b.Id);
                if (free <= 0 || (c != null && b.Id == c.HomeId)) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = b.Id, ["name"] = b.Def.Name, ["free"] = free, ["landValue"] = lv.TryGetValue(b.Id, out double v) ? Math.Round(v) : 0,
                    ["commute"] = c != null && c.JobId != 0 ? Math.Round(w.Network.TravelHours(b.Id, c.JobId) * 60, 1) : -1,
                });
            }
            return Json.Serialize(list.Take(30).ToList());
        }

        /// <summary>Useful places to travel to: home, work, nearest shops and public buildings.</summary>
        [JSInvokable]
        public static string Places()
        {
            WorldState w = World;
            w.Economy.Citizens.TryGetValue(w.Player.CitizenId, out Citizen c);
            int from = w.Player.InsideBuildingId != 0 ? w.Player.InsideBuildingId : TravelCommand.NearestBuilding(w, w.Player.X, w.Player.Z);
            if (from == 0 && c != null) from = c.HomeId;
            var list = new List<(string label, int id, float h)>();
            void Add(string label, int id)
            {
                if (id == 0 || list.Any(x => x.id == id)) return;
                list.Add((label, id, from == 0 ? float.PositiveInfinity : w.Network.TravelHours(from, id)));
            }
            if (c != null) { Add("自宅", c.HomeId); Add("職場", c.JobId); }
            var candidates = w.Economy.Companies.Values.Where(co => !co.Closed && (co.IsRetail || co.Def.Id == "town_hall" || co.Def.Id == "school"))
                .Select(co => (co, h: from == 0 ? float.PositiveInfinity : w.Network.TravelHours(from, co.BuildingId))).OrderBy(x => x.h).ToList();
            foreach (var (co, _) in candidates.Where(x => x.co.Def.Id == "shop").Take(3)) Add("商店", co.BuildingId);
            foreach (var (co, _) in candidates.Where(x => x.co.Def.Id == "office").Take(1)) Add("オフィス（サービス）", co.BuildingId);
            foreach (var (co, _) in candidates.Where(x => x.co.Def.Id == "town_hall").Take(1)) Add("市役所（求人）", co.BuildingId);
            foreach (var (co, _) in candidates.Where(x => x.co.Def.Id == "school").Take(1)) Add("学校（職業訓練）", co.BuildingId);
            return Json.Serialize(list.Select(x => (object)new Dictionary<string, object>
            {
                ["label"] = x.label, ["id"] = x.id, ["minutes"] = float.IsPositiveInfinity(x.h) ? -1 : Math.Round(x.h * 60, 1),
                ["name"] = w.TryGetBuilding(x.id, out Building b) ? b.Def.Name : "",
            }).ToList());
        }

        /// <summary>Adults one could live as, nearest to a map point first.</summary>
        [JSInvokable]
        public static string Candidates(double x, double z)
        {
            WorldState w = World;
            long today = w.Clock.TotalDays;
            var list = w.Economy.Citizens.Values.Where(c => c.HomeId != 0 && c.AgeYears(today) >= 16)
                .Select(c =>
                {
                    w.TryGetBuilding(c.HomeId, out Building h);
                    var (dx, dz, _, _) = w.DoorOf(h);
                    return (c, d: (dx - x) * (dx - x) + (dz - z) * (dz - z));
                })
                .OrderBy(t => t.d).ThenBy(t => t.c.Id).Take(24)
                .Select(t => (object)new Dictionary<string, object>
                {
                    ["id"] = t.c.Id, ["name"] = Names.Of(t.c.Id), ["age"] = t.c.AgeYears(today), ["education"] = t.c.Education,
                    ["job"] = t.c.JobId != 0 && w.TryGetBuilding(t.c.JobId, out Building jb) ? jb.Def.Name : t.c.Retired ? "退職" : "無職",
                    ["cash"] = (double)t.c.Cash,
                }).ToList();
            return Json.Serialize(list);
        }

        /// <summary>Door position of a building (for drawing a trip along the way).</summary>
        [JSInvokable]
        public static string DoorOf(int id)
        {
            if (!World.TryGetBuilding(id, out Building b)) return Json.Serialize(new Dictionary<string, object> { ["x"] = 0.0, ["z"] = 0.0 });
            var (x, z, nx, nz) = World.DoorOf(b);
            return Json.Serialize(new Dictionary<string, object> { ["x"] = (double)(x + nx * 2.5f), ["z"] = (double)(z + nz * 2.5f) });
        }

        /// <summary>Events about the player's own life (and the world events that hit it).</summary>
        [JSInvokable]
        public static string LifeLog()
        {
            var list = World.Events.Where(e => e.Kind == "人生" || e.Kind == "災害" || e.Kind == "倒産").Reverse().Take(30)
                .Select(e => (object)new List<object> { (double)e.Tick, e.Kind, e.Message }).ToList();
            return Json.Serialize(list);
        }
    }
}
