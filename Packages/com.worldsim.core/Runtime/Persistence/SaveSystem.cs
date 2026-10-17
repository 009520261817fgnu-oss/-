using System;
using System.Collections.Generic;
using System.Globalization;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Environment;
using WorldSim.Core.Simulation;
using WorldSim.Core.Transport;
using WorldSim.Core.World;

namespace WorldSim.Core.Persistence
{
    /// <summary>
    /// Saves and loads the complete play state as versioned JSON (spec 5.4): world, economy,
    /// environment, clock, RNG state, history and even mid-day accumulators, so a loaded game
    /// continues exactly as the original would have. Large grids are base64; history is columnar.
    /// Definitions (content) are not saved; saves refer to content ids and fail clearly if missing.
    /// </summary>
    public static class SaveSystem
    {
        public const string Format = "WorldSim.Save";
        /// <summary>2: adds the controlled resident (life mode), tenure and relationships. 1 still loads.</summary>
        public const int Version = 3;

        public static string Save(WorldState w)
        {
            var root = new Dictionary<string, object>
            {
                ["format"] = Format,
                ["version"] = Version,
                ["worldFormat"] = WorldState.FormatVersion,
                ["seed"] = Hex(w.Seed),
                ["tick"] = (double)w.Clock.Tick,
                ["rng"] = Hex(w.Random.State),
                ["nextId"] = w.NextId,
                ["terrain"] = new Dictionary<string, object>
                {
                    ["width"] = w.Width, ["depth"] = w.Depth,
                    ["cellSize"] = (double)w.Terrain.CellSize, ["seaLevel"] = (double)w.Terrain.SeaLevel,
                    ["heights"] = Floats(w.Terrain.RawHeights),
                },
                ["vegetation"] = Convert.ToBase64String(w.RawVegetation),
                ["roads"] = Convert.ToBase64String(RoadBytes(w)),
                ["traffic"] = Floats(w.TrafficVolume),
                ["congestion"] = Floats(w.CongestionBasis),
                ["buildings"] = Buildings(w),
                ["economy"] = Economy(w),
                ["environment"] = Environment(w.Environment),
                ["events"] = Events(w),
                ["trips"] = Trips(w),
                ["history"] = HistoryCodec.Encode(w.History),
                ["player"] = Player(w.Player),
                ["causes"] = Causes(w.Economy.Causes),
                ["landValues"] = LandValues(w),
            };
            return Json.Serialize(root);
        }

        public static WorldState Load(ContentDatabase content, string json)
        {
            if (!(Json.Parse(json) is IDictionary<string, object> root) || root.GetString("format") != Format)
                throw new FormatException("WorldSimのセーブデータではありません");
            int version = root.GetInt("version");
            if (version > Version) throw new FormatException($"新しいバージョンのセーブデータです（{version} > {Version}）");

            var t = Obj(root, "terrain");
            var terrain = new Terrain(t.GetInt("width"), t.GetInt("depth"), (float)t.GetDouble("cellSize"), (float)t.GetDouble("seaLevel"));
            ReadFloats(t.GetString("heights"), terrain.RawHeights);
            byte[] veg = Convert.FromBase64String(root.GetString("vegetation"));
            var w = new WorldState(content, ParseHex(root.GetString("seed")), terrain, veg);
            w.Clock.Tick = (long)root.GetDouble("tick");
            w.Random.State = ParseHex(root.GetString("rng"));

            byte[] roads = Convert.FromBase64String(root.GetString("roads"));
            for (int i = 0; i < roads.Length; i++) if (roads[i] != 0) w.RestoreRoad(i, roads[i] == 2);
            ReadFloats(root.GetString("traffic"), w.TrafficVolume);
            ReadFloats(root.GetString("congestion"), w.CongestionBasis);

            foreach (object o in Arr(root, "buildings"))
            {
                var a = (IList<object>)o;
                string defId = (string)a[1];
                if (!content.TryGetBuilding(defId, out BuildingDef def))
                    throw new FormatException($"セーブデータの建物 '{defId}' が定義データにありません");
                w.RestoreBuilding(I(a[0]), def, new GridPos(I(a[2]), I(a[3])), (float)D(a[4]), (float)D(a[5]));
            }
            LoadEconomy(w, Obj(root, "economy"));
            LoadEnvironment(w.Environment, Obj(root, "environment"));
            foreach (object o in Arr(root, "events"))
            {
                var a = (IList<object>)o;
                w.Events.Add(new WorldEvent { Tick = (long)D(a[0]), Kind = (string)a[1], Message = (string)a[2] });
            }
            foreach (object o in Arr(root, "trips"))
            {
                var a = (IList<object>)o;
                w.Trips.Add(new Trip(I(a[0]), I(a[1]), (float)D(a[2]), (bool)a[3]));
            }
            w.History.AddRange(HistoryCodec.Decode(Obj(root, "history")));
            if (version >= 2) LoadPlayer(w, Obj(root, "player"));
            // Rebuilding the world moved money through the ledger (start funds, company capital); those are
            // not real flows of the saved day, so they must not reach the cause record.
            Array.Clear(w.Economy.Causes.Money, 0, w.Economy.Causes.Money.Length);
            if (version >= 3) LoadCauses(w.Economy.Causes, Obj(root, "causes"));
            if (root.ContainsKey("landValues"))
            {
                IList<object> lv = Arr(root, "landValues");
                w.LandValueWeek = (long)D(lv[0]);
                for (int i = 1; i + 1 < lv.Count; i += 2) w.LandValueIndex[I(lv[i])] = D(lv[i + 1]);
            }
            w.RestoreNextId(root.GetInt("nextId"));
            return w;
        }

        /// <summary>Independent deep copy via save/load: the basis of history branching.</summary>
        public static WorldState Clone(WorldState w) => Load(w.Content, Save(w));

        private static List<object> LandValues(WorldState w)
        {
            var l = new List<object> { (double)w.LandValueWeek };
            foreach (var kv in w.LandValueIndex) { l.Add(kv.Key); l.Add(kv.Value); }
            return l;
        }

        // ------------------------------------------------------------------ causes (v3)

        private static Dictionary<string, object> Causes(CauseLog c)
        {
            List<object> Notes(IEnumerable<CauseNote> notes)
            {
                var l = new List<object>();
                foreach (CauseNote n in notes) { l.Add((double)n.Tick); l.Add(n.Citizen); l.Add((int)n.Cause); l.Add(n.Agent); }
                return l;
            }
            List<object> Day(CauseDay d)
            {
                var l = new List<object> { (double)d.Day };
                foreach (var kv in d.Totals) { l.Add(kv.Key); l.Add((double)kv.Value); }
                return l;
            }
            var last = new List<object>();
            var ids = new List<int>(c.Last.Keys);
            ids.Sort();
            foreach (int id in ids) { last.Add(id); last.Add((int)c.Last[id]); }
            var gone = new List<object>();
            foreach (int id in c.Gone) gone.Add(id);
            return new Dictionary<string, object>
            {
                ["now"] = (double)c.Now, ["primed"] = c.Primed,
                ["notes"] = Notes(c.Notes), ["today"] = Notes(c.Today.Values),
                ["days"] = c.Days.ConvertAll(d => (object)Day(d)), ["open"] = Day(c.OpenWithMoney()), ["last"] = last, ["gone"] = gone,
            };
        }

        private static void LoadCauses(CauseLog c, IDictionary<string, object> o)
        {
            IEnumerable<CauseNote> Notes(IList<object> a)
            {
                for (int i = 0; i + 3 < a.Count; i += 4)
                    yield return new CauseNote { Tick = (long)D(a[i]), Citizen = I(a[i + 1]), Cause = (Cause)I(a[i + 2]), Agent = I(a[i + 3]) };
            }
            CauseDay Day(IList<object> a)
            {
                var d = new CauseDay { Day = (long)D(a[0]) };
                for (int i = 1; i + 1 < a.Count; i += 2) d.Totals[I(a[i])] = (long)D(a[i + 1]);
                return d;
            }
            c.Now = (long)o.GetDouble("now");
            c.Primed = (bool)o["primed"];
            c.Notes.AddRange(Notes(Arr(o, "notes")));
            foreach (CauseNote n in Notes(Arr(o, "today"))) c.Today[n.Citizen] = n;
            foreach (object d in Arr(o, "days")) c.Days.Add(Day((IList<object>)d));
            c.Open = Day(Arr(o, "open"));
            IList<object> last = Arr(o, "last");
            for (int i = 0; i + 1 < last.Count; i += 2) c.Last[I(last[i])] = (byte)I(last[i + 1]);
            foreach (object id in Arr(o, "gone")) c.Gone.Add(I(id));
        }

        // ------------------------------------------------------------------ economy

        private static Dictionary<string, object> Economy(WorldState w)
        {
            EconomyState e = w.Economy;
            City city = e.City;
            var policy = new Dictionary<string, object>();
            foreach (string k in Policy.Keys) policy[k] = city.Policy.Get(k);
            var citizens = new List<object>();
            foreach (Citizen c in e.Citizens.Values)
                citizens.Add(new List<object>
                {
                    c.Id, (double)c.Cash, (double)c.BirthDay, c.HomeId, c.JobId, c.Education, c.Retired,
                    (double)c.Happiness, (double)c.CommuteHours, c.UnemployedDays, (double)c.LastNetIncome,
                    (double)c.FoodMet, (double)c.GoodsMet, (double)c.ServicesMet, c.SchoolDays, c.TenureDays,
                    Relations(c), (double)c.LastRent, c.RentArrearsDays, (double)c.Health, c.PartnerId, c.ParentId,
                });
            var companies = new List<object>();
            foreach (Company co in e.Companies.Values)
                companies.Add(new Dictionary<string, object>
                {
                    ["b"] = co.BuildingId, ["cash"] = (double)co.Cash, ["wage"] = (double)co.Wage, ["target"] = co.TargetWorkers,
                    ["workers"] = new List<object>(co.Workers.ConvertAll(x => (object)x)),
                    ["inv"] = Dict(co.Inventory), ["salesAvg"] = Dict(co.SalesAvg), ["salesToday"] = Dict(co.SalesToday),
                    ["closed"] = co.Closed, ["closedDays"] = co.ClosedDays, ["distress"] = co.DistressDays,
                    ["vacancyDays"] = co.VacancyDays, ["broken"] = co.BrokenDays,
                    ["techs"] = new List<object>(co.AdoptedTechs),
                    ["rev"] = (double)co.RevenueToday, ["cost"] = (double)co.CostToday,
                    ["profitAvg"] = co.ProfitAvg, ["lastProfit"] = (double)co.LastProfit, ["lastCost"] = (double)co.LastCost,
                    ["lastOutput"] = co.LastOutput, ["outputCap"] = co.OutputCapacity,
                    ["shortage"] = co.InputShortage, ["energy"] = co.EnergyUse, ["targetCause"] = (int)co.TargetCause, ["backlog"] = (double)co.Backlog,
                });
            return new Dictionary<string, object>
            {
                ["cityCash"] = (double)city.Cash,
                ["externalCash"] = (double)e.External.Cash,
                ["preparedness"] = city.PreparednessStock,
                ["policy"] = policy,
                ["research"] = new Dictionary<string, object>
                {
                    ["completed"] = new List<object>(city.Research.Completed),
                    ["current"] = city.Research.Current,
                    ["progress"] = city.Research.Progress,
                    ["points"] = city.Research.PointsYesterday,
                },
                ["prices"] = Dict(e.Market.Price),
                ["lastInvestment"] = (double)e.LastInvestmentDay,
                ["marketToday"] = new Dictionary<string, object>
                {
                    ["produced"] = Dict(e.Market.Produced), ["traded"] = Dict(e.Market.LocalTraded),
                    ["imported"] = Dict(e.Market.Imported), ["exported"] = Dict(e.Market.Exported), ["unmet"] = Dict(e.Market.Unmet),
                },
                ["ledgerToday"] = Longs(e.Ledger.Today),
                ["ledgerYesterday"] = Longs(e.Ledger.Yesterday),
                ["daily"] = new List<object> { e.Daily.Births, e.Daily.Deaths, e.Daily.Immigrants, e.Daily.Emigrants, e.Daily.Moves },
                ["citizens"] = citizens,
                ["companies"] = companies,
            };
        }

        private static void LoadEconomy(WorldState w, IDictionary<string, object> o)
        {
            EconomyState e = w.Economy;
            e.City.Cash = (long)o.GetDouble("cityCash");
            e.External.Cash = (long)o.GetDouble("externalCash");
            e.City.PreparednessStock = o.GetDouble("preparedness");
            foreach (var kv in Obj(o, "policy"))
            {
                string err = e.City.Policy.Set(kv.Key, D(kv.Value));
                if (err != null) throw new FormatException("政策値が不正です: " + err);
            }
            var r = Obj(o, "research");
            foreach (object id in (IList<object>)r["completed"])
            {
                if (!w.Content.TryGetTech((string)id, out _)) throw new FormatException($"セーブデータの技術 '{id}' が定義データにありません");
                e.City.Research.Completed.Add((string)id);
            }
            e.City.Research.Current = r.GetString("current");
            e.City.Research.Progress = r.GetDouble("progress");
            e.City.Research.PointsYesterday = r.GetDouble("points");
            ReadDict(Obj(o, "prices"), e.Market.Price);
            var mt = Obj(o, "marketToday");
            ReadDict(Obj(mt, "produced"), e.Market.Produced);
            ReadDict(Obj(mt, "traded"), e.Market.LocalTraded);
            ReadDict(Obj(mt, "imported"), e.Market.Imported);
            ReadDict(Obj(mt, "exported"), e.Market.Exported);
            ReadDict(Obj(mt, "unmet"), e.Market.Unmet);
            if (o.ContainsKey("lastInvestment")) e.LastInvestmentDay = (long)o.GetDouble("lastInvestment");
            ReadLongs(Arr(o, "ledgerToday"), e.Ledger.Today);
            ReadLongs(Arr(o, "ledgerYesterday"), e.Ledger.Yesterday);
            var daily = Arr(o, "daily");
            e.Daily.Births = I(daily[0]); e.Daily.Deaths = I(daily[1]); e.Daily.Immigrants = I(daily[2]);
            e.Daily.Emigrants = I(daily[3]); e.Daily.Moves = I(daily[4]);

            foreach (object item in Arr(o, "citizens"))
            {
                var a = (IList<object>)item;
                var c = new Citizen
                {
                    Id = I(a[0]), Cash = (long)D(a[1]), BirthDay = (long)D(a[2]), JobId = I(a[4]), Education = I(a[5]),
                    Retired = (bool)a[6], Happiness = (float)D(a[7]), CommuteHours = (float)D(a[8]), UnemployedDays = I(a[9]),
                    LastNetIncome = (long)D(a[10]), FoodMet = (float)D(a[11]), GoodsMet = (float)D(a[12]),
                    ServicesMet = (float)D(a[13]), SchoolDays = I(a[14]),
                };
                if (a.Count > 15) c.TenureDays = I(a[15]);
                if (a.Count > 16 && a[16] is IDictionary<string, object> rel)
                    foreach (var kv in rel) c.AddAffinity(int.Parse(kv.Key, CultureInfo.InvariantCulture), (float)D(kv.Value));
                if (a.Count > 18) { c.LastRent = (long)D(a[17]); c.RentArrearsDays = I(a[18]); }
                if (a.Count > 19) c.Health = (float)D(a[19]);
                if (a.Count > 21) { c.PartnerId = I(a[20]); c.ParentId = I(a[21]); }
                e.Citizens.Add(c.Id, c);
                e.MoveIn(c, I(a[3]));
            }
            foreach (object item in Arr(o, "companies"))
            {
                var d = (IDictionary<string, object>)item;
                int bid = d.GetInt("b");
                if (!w.TryGetBuilding(bid, out Building b)) throw new FormatException($"企業の建物 #{bid} がありません");
                var co = new Company
                {
                    BuildingId = bid, Def = b.Def, Recipe = b.Def.Recipe != null ? w.Content.GetRecipe(b.Def.Recipe) : null,
                    IsPublic = b.Def.IsPublic, Cash = (long)d.GetDouble("cash"), Wage = (long)d.GetDouble("wage"),
                    TargetWorkers = d.GetInt("target"), Closed = (bool)d["closed"], ClosedDays = d.GetInt("closedDays"),
                    DistressDays = d.GetInt("distress"), VacancyDays = d.GetInt("vacancyDays"), BrokenDays = d.GetInt("broken"),
                    RevenueToday = (long)d.GetDouble("rev"), CostToday = (long)d.GetDouble("cost"),
                    ProfitAvg = d.GetDouble("profitAvg"), LastProfit = (long)d.GetDouble("lastProfit"),
                    LastOutput = d.GetDouble("lastOutput"), OutputCapacity = d.GetDouble("outputCap"),
                    InputShortage = (bool)d["shortage"], EnergyUse = d.GetDouble("energy"),
                };
                if (d.ContainsKey("targetCause")) co.TargetCause = (Cause)d.GetInt("targetCause");
                if (d.ContainsKey("backlog")) co.Backlog = (long)d.GetDouble("backlog");
                if (d.ContainsKey("lastCost")) co.LastCost = (long)d.GetDouble("lastCost");
                foreach (object x in (IList<object>)d["workers"]) co.Workers.Add(I(x));
                ReadDict(Obj(d, "inv"), co.Inventory);
                ReadDict(Obj(d, "salesAvg"), co.SalesAvg);
                ReadDict(Obj(d, "salesToday"), co.SalesToday);
                foreach (object x in (IList<object>)d["techs"]) co.AdoptedTechs.Add((string)x);
                e.Companies.Add(bid, co);
            }
        }

        private static object Relations(Citizen c)
        {
            if (c.Relations == null || c.Relations.Count == 0) return null;
            var d = new Dictionary<string, object>();
            foreach (var kv in c.Relations) d[kv.Key.ToString(CultureInfo.InvariantCulture)] = (double)kv.Value;
            return d;
        }

        private static Dictionary<string, object> Player(People.PlayerState p) => new Dictionary<string, object>
        {
            ["citizen"] = p.CitizenId, ["x"] = (double)p.X, ["z"] = (double)p.Z, ["heading"] = (double)p.Heading,
            ["inside"] = p.InsideBuildingId, ["roomX"] = (double)p.RoomX, ["roomZ"] = (double)p.RoomZ,
            ["activity"] = p.Activity.ToString(), ["hunger"] = (double)p.Hunger, ["energy"] = (double)p.Energy,
            ["pantry"] = p.Pantry, ["goods"] = p.GoodsOwned, ["worked"] = (double)p.WorkedHours, ["absence"] = p.AbsenceDays,
            ["study"] = p.StudyHours, ["travelTo"] = p.TravelTo, ["arrive"] = p.TravelArrive,
        };

        private static void LoadPlayer(WorldState w, IDictionary<string, object> o)
        {
            People.PlayerState p = w.Player;
            p.CitizenId = o.GetInt("citizen");
            if (p.CitizenId != 0 && !w.Economy.Citizens.ContainsKey(p.CitizenId)) throw new FormatException("操作中の人物がセーブデータにいません");
            p.X = (float)o.GetDouble("x"); p.Z = (float)o.GetDouble("z"); p.Heading = (float)o.GetDouble("heading");
            p.InsideBuildingId = o.GetInt("inside"); p.RoomX = (float)o.GetDouble("roomX"); p.RoomZ = (float)o.GetDouble("roomZ");
            p.Activity = (People.AvatarActivity)Enum.Parse(typeof(People.AvatarActivity), o.GetString("activity", "Idle"));
            p.Hunger = (float)o.GetDouble("hunger"); p.Energy = (float)o.GetDouble("energy");
            p.Pantry = o.GetDouble("pantry"); p.GoodsOwned = o.GetDouble("goods");
            p.WorkedHours = (float)o.GetDouble("worked"); p.AbsenceDays = o.GetInt("absence"); p.StudyHours = o.GetDouble("study");
            p.TravelTo = o.GetInt("travelTo"); p.TravelArrive = o.GetDouble("arrive");
        }

        // ------------------------------------------------------------------ environment, events

        private static Dictionary<string, object> Environment(EnvironmentState env) => new Dictionary<string, object>
        {
            ["temperature"] = (double)env.Temperature, ["rain"] = (double)env.RainToday, ["moisture"] = (double)env.SoilMoisture,
            ["rain3"] = (double)env.RainLast3Days, ["wetStreak"] = env.WetStreak, ["warming"] = env.Warming,
            ["emissions"] = env.CumulativeEmissions, ["pollution"] = Floats(env.Pollution),
            ["supply"] = env.EnergySupply, ["demand"] = env.EnergyDemand, ["ratio"] = env.EnergyRatio,
            ["floodLevel"] = (double)env.FloodLevel, ["floodDays"] = env.FloodDaysLeft,
        };

        private static void LoadEnvironment(EnvironmentState env, IDictionary<string, object> o)
        {
            env.Temperature = (float)o.GetDouble("temperature");
            env.RainToday = (float)o.GetDouble("rain");
            env.SoilMoisture = (float)o.GetDouble("moisture");
            env.RainLast3Days = (float)o.GetDouble("rain3");
            env.WetStreak = o.GetInt("wetStreak");
            env.Warming = o.GetDouble("warming");
            env.CumulativeEmissions = o.GetDouble("emissions");
            ReadFloats(o.GetString("pollution"), env.Pollution);
            env.EnergySupply = o.GetDouble("supply");
            env.EnergyDemand = o.GetDouble("demand");
            env.EnergyRatio = o.GetDouble("ratio");
            env.FloodLevel = (float)o.GetDouble("floodLevel");
            env.FloodDaysLeft = o.GetInt("floodDays");
        }

        private static List<object> Buildings(WorldState w)
        {
            var list = new List<object>();
            foreach (Building b in w.Buildings)
                list.Add(new List<object> { b.Id, b.Def.Id, b.Origin.X, b.Origin.Z, (double)b.BaseHeight, (double)b.Condition });
            return list;
        }

        private static List<object> Events(WorldState w)
        {
            var list = new List<object>();
            foreach (WorldEvent ev in w.Events) list.Add(new List<object> { (double)ev.Tick, ev.Kind, ev.Message });
            return list;
        }

        private static List<object> Trips(WorldState w)
        {
            var list = new List<object>();
            foreach (Trip t in w.Trips) list.Add(new List<object> { t.FromBuilding, t.ToBuilding, (double)t.Vehicles, t.Freight });
            return list;
        }

        private static byte[] RoadBytes(WorldState w)
        {
            bool[] r = w.RawRoads, d = w.RawRoadDamage;
            var bytes = new byte[r.Length];
            for (int i = 0; i < r.Length; i++) bytes[i] = (byte)(r[i] ? (d[i] ? 2 : 1) : 0);
            return bytes;
        }

        // ------------------------------------------------------------------ helpers

        internal static string Floats(float[] values)
        {
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            if (!BitConverter.IsLittleEndian) SwapEndian(bytes);
            return Convert.ToBase64String(bytes);
        }

        internal static void ReadFloats(string b64, float[] target)
        {
            byte[] bytes = Convert.FromBase64String(b64);
            if (bytes.Length != target.Length * 4) throw new FormatException("配列の長さが一致しません");
            if (!BitConverter.IsLittleEndian) SwapEndian(bytes);
            Buffer.BlockCopy(bytes, 0, target, 0, bytes.Length);
        }

        private static void SwapEndian(byte[] b)
        {
            for (int i = 0; i < b.Length; i += 4) { Array.Reverse(b, i, 4); }
        }

        private static List<object> Longs(long[] a)
        {
            var l = new List<object>(a.Length);
            foreach (long v in a) l.Add((double)v);
            return l;
        }

        private static void ReadLongs(IList<object> src, long[] dst)
        {
            for (int i = 0; i < Math.Min(src.Count, dst.Length); i++) dst[i] = (long)D(src[i]);
        }

        private static Dictionary<string, object> Dict(SortedDictionary<string, double> d)
        {
            var o = new Dictionary<string, object>();
            foreach (var kv in d) o[kv.Key] = kv.Value;
            return o;
        }

        private static void ReadDict(IDictionary<string, object> src, SortedDictionary<string, double> dst)
        {
            dst.Clear();
            foreach (var kv in src) dst[kv.Key] = D(kv.Value);
        }

        private static string Hex(ulong v) => v.ToString("X16", CultureInfo.InvariantCulture);
        private static ulong ParseHex(string s) => ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        private static double D(object o) => o is double d ? d : throw new FormatException("数値が必要です");
        private static int I(object o) => (int)D(o);

        private static IDictionary<string, object> Obj(IDictionary<string, object> o, string key) =>
            o.GetObject(key) ?? throw new FormatException($"'{key}' がありません");

        private static IList<object> Arr(IDictionary<string, object> o, string key) =>
            o.GetArray(key) ?? throw new FormatException($"'{key}' がありません");
    }

    /// <summary>Columnar encoding of the stats history: one numeric array per series.</summary>
    internal static class HistoryCodec
    {
        private static readonly string[] Scalars =
        {
            "tick", "buildings", "roadCells", "population", "labourForce", "employed", "jobs", "vacancies", "retired", "children",
            "companiesOpen", "companiesClosed", "treasury", "companyCash", "householdCash", "totalMoney",
            "avgWage", "avgHappiness", "avgCommute", "wages", "retail", "b2b", "imports", "exports", "freight", "taxes", "profit",
            "births", "deaths", "immigrants", "emigrants", "moves", "traffic", "congested",
            "energyDemand", "energySupply", "pollution", "temperature", "rain", "moisture", "research", "techs",
            "damagedBuildings", "damagedRoads", "avgHealth",
        };

        private static double Get(StatsSnapshot s, string k)
        {
            switch (k)
            {
                case "tick": return s.Tick; case "buildings": return s.Buildings; case "roadCells": return s.RoadCells;
                case "population": return s.Population; case "labourForce": return s.LabourForce; case "employed": return s.Employed;
                case "jobs": return s.Jobs; case "vacancies": return s.Vacancies; case "retired": return s.Retired; case "children": return s.Children;
                case "companiesOpen": return s.CompaniesOpen; case "companiesClosed": return s.CompaniesClosed;
                case "treasury": return s.Treasury; case "companyCash": return s.CompanyCash; case "householdCash": return s.HouseholdCash;
                case "totalMoney": return s.TotalMoney; case "avgWage": return s.AvgWage; case "avgHappiness": return s.AvgHappiness;
                case "avgCommute": return s.AvgCommuteHours; case "wages": return s.Wages; case "retail": return s.RetailSales;
                case "b2b": return s.B2BSales; case "imports": return s.Imports; case "exports": return s.Exports;
                case "freight": return s.Freight; case "taxes": return s.Taxes; case "profit": return s.CompanyProfit;
                case "births": return s.Births; case "deaths": return s.Deaths; case "immigrants": return s.Immigrants;
                case "emigrants": return s.Emigrants; case "moves": return s.Moves; case "traffic": return s.TrafficTotal;
                case "congested": return s.CongestedCells; case "energyDemand": return s.EnergyDemand; case "energySupply": return s.EnergySupply;
                case "pollution": return s.AvgPollution; case "temperature": return s.Temperature; case "rain": return s.Rain;
                case "moisture": return s.SoilMoisture; case "research": return s.ResearchPoints; case "techs": return s.TechsCompleted;
                case "damagedBuildings": return s.DamagedBuildings; case "damagedRoads": return s.DamagedRoads;
                case "avgHealth": return s.AvgHealth;
                default: throw new ArgumentException(k);
            }
        }

        private static void Set(StatsSnapshot s, string k, double v)
        {
            switch (k)
            {
                case "tick": s.Tick = (long)v; break; case "buildings": s.Buildings = (int)v; break; case "roadCells": s.RoadCells = (int)v; break;
                case "population": s.Population = (int)v; break; case "labourForce": s.LabourForce = (int)v; break; case "employed": s.Employed = (int)v; break;
                case "jobs": s.Jobs = (int)v; break; case "vacancies": s.Vacancies = (int)v; break; case "retired": s.Retired = (int)v; break;
                case "children": s.Children = (int)v; break; case "companiesOpen": s.CompaniesOpen = (int)v; break;
                case "companiesClosed": s.CompaniesClosed = (int)v; break; case "treasury": s.Treasury = (long)v; break;
                case "companyCash": s.CompanyCash = (long)v; break; case "householdCash": s.HouseholdCash = (long)v; break;
                case "totalMoney": s.TotalMoney = (long)v; break; case "avgWage": s.AvgWage = v; break; case "avgHappiness": s.AvgHappiness = v; break;
                case "avgCommute": s.AvgCommuteHours = v; break; case "wages": s.Wages = (long)v; break; case "retail": s.RetailSales = (long)v; break;
                case "b2b": s.B2BSales = (long)v; break; case "imports": s.Imports = (long)v; break; case "exports": s.Exports = (long)v; break;
                case "freight": s.Freight = (long)v; break; case "taxes": s.Taxes = (long)v; break; case "profit": s.CompanyProfit = (long)v; break;
                case "births": s.Births = (int)v; break; case "deaths": s.Deaths = (int)v; break; case "immigrants": s.Immigrants = (int)v; break;
                case "emigrants": s.Emigrants = (int)v; break; case "moves": s.Moves = (int)v; break; case "traffic": s.TrafficTotal = v; break;
                case "congested": s.CongestedCells = v; break; case "energyDemand": s.EnergyDemand = v; break; case "energySupply": s.EnergySupply = v; break;
                case "pollution": s.AvgPollution = v; break; case "temperature": s.Temperature = v; break; case "rain": s.Rain = v; break;
                case "moisture": s.SoilMoisture = v; break; case "research": s.ResearchPoints = v; break; case "techs": s.TechsCompleted = (int)v; break;
                case "damagedBuildings": s.DamagedBuildings = (int)v; break; case "damagedRoads": s.DamagedRoads = (int)v; break;
                case "avgHealth": s.AvgHealth = v; break;
            }
        }

        public static Dictionary<string, object> Encode(List<StatsSnapshot> history)
        {
            var cols = new Dictionary<string, object> { ["count"] = history.Count };
            foreach (string k in Scalars)
            {
                var col = new List<object>(history.Count);
                foreach (StatsSnapshot s in history) col.Add(Get(s, k));
                cols[k] = col;
            }
            EncodeMap(cols, history, "prod.", s => s.Production);
            EncodeMap(cols, history, "price.", s => s.Prices);
            EncodeMap(cols, history, "imp.", s => s.Imported);
            EncodeMap(cols, history, "unmet.", s => s.Unmet);
            var cats = new List<object>(history.Count);
            foreach (StatsSnapshot s in history)
            {
                var d = new Dictionary<string, object>();
                foreach (var kv in s.ByCategory) d[kv.Key.ToString()] = kv.Value;
                cats.Add(d);
            }
            cols["categories"] = cats;
            return cols;
        }

        private static void EncodeMap(Dictionary<string, object> cols, List<StatsSnapshot> h, string prefix,
            Func<StatsSnapshot, SortedDictionary<string, double>> map)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (StatsSnapshot s in h) foreach (string k in map(s).Keys) keys.Add(k);
            foreach (string k in keys)
            {
                var col = new List<object>(h.Count);
                foreach (StatsSnapshot s in h) col.Add(map(s).TryGetValue(k, out double v) ? (object)v : null);
                cols[prefix + k] = col;
            }
        }

        public static List<StatsSnapshot> Decode(IDictionary<string, object> cols)
        {
            int n = cols.GetInt("count");
            var list = new List<StatsSnapshot>(n);
            for (int i = 0; i < n; i++) list.Add(new StatsSnapshot());
            foreach (var kv in cols)
            {
                if (kv.Key == "count") continue;
                var col = (IList<object>)kv.Value;
                if (kv.Key == "categories")
                {
                    for (int i = 0; i < n; i++)
                        foreach (var c in (IDictionary<string, object>)col[i])
                            list[i].ByCategory[(BuildingCategory)Enum.Parse(typeof(BuildingCategory), c.Key)] = (int)(double)c.Value;
                    continue;
                }
                int dot = kv.Key.IndexOf('.');
                for (int i = 0; i < n; i++)
                {
                    if (!(col[i] is double v)) continue; // null = series had no value that day
                    if (dot < 0) { Set(list[i], kv.Key, v); continue; }
                    string prefix = kv.Key.Substring(0, dot), res = kv.Key.Substring(dot + 1);
                    SortedDictionary<string, double> map = prefix == "prod" ? list[i].Production
                        : prefix == "price" ? list[i].Prices : prefix == "imp" ? list[i].Imported : list[i].Unmet;
                    map[res] = v;
                }
            }
            return list;
        }
    }
}
