using System;
using WorldSim.Core.Commands;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.People
{
    // ---------------------------------------------------------------- choosing who to live as

    /// <summary>Starts living as an existing resident (allowed in either mode).</summary>
    public sealed class PossessCitizenCommand : IWorldCommand
    {
        public readonly int CitizenId;
        public PossessCitizenCommand(int citizenId) { CitizenId = citizenId; }

        public CommandResult Execute(WorldState w)
        {
            if (!w.Economy.Citizens.TryGetValue(CitizenId, out Citizen c)) return CommandResult.Fail("その住民はいません");
            if (c.AgeYears(w.Clock.TotalDays) < 16) return CommandResult.Fail("16歳以上の住民を選んでください");
            PlayerState p = w.Player;
            p.Clear();
            p.CitizenId = c.Id;
            p.Hunger = 0.8f;
            p.Energy = 0.8f;
            p.Pantry = 3;
            p.WorkedHours = 8; // no absence counted for the day control starts
            p.AbsenceDays = 0;
            p.StudyHours = 0;
            if (c.HomeId != 0 && w.TryGetBuilding(c.HomeId, out Building home)) AvatarSystem.PlaceAtDoor(w, p, home);
            else { p.X = w.Width * w.Terrain.CellSize / 2; p.Z = w.Depth * w.Terrain.CellSize / 2; }
            w.Log("人生", $"{Names.Of(c.Id)}として暮らし始めました");
            return CommandResult.Ok($"{Names.Of(c.Id)}として暮らし始めました", c.Id);
        }
    }

    /// <summary>A newcomer moves to town (savings from outside) and the player lives as them.</summary>
    public sealed class CreateResidentCommand : IWorldCommand
    {
        public readonly int Age, Education;
        public CreateResidentCommand(int age, int education) { Age = age; Education = education; }

        public CommandResult Execute(WorldState w)
        {
            if (Age < 16 || Age > 80) return CommandResult.Fail("年齢は16〜80歳");
            if (Education < 0 || Education > 2) return CommandResult.Fail("学歴が不正です");
            Building home = null;
            foreach (Building b in w.Buildings)
                if (b.Def.Housing > 0 && b.Condition >= 0.5f && w.Network.HasAccess(b.Id) && w.Economy.Residents(b.Id) < b.Def.Housing) { home = b; break; }
            if (home == null) return CommandResult.Fail("空いている住宅がありません。先に住宅を建ててください");
            long today = w.Clock.TotalDays;
            var c = new Citizen
            {
                Id = w.AllocateId(),
                BirthDay = today - Age * SimClock.DaysPerYear,
                Education = Education,
                SchoolDays = -1,
                Retired = Age >= 65,
                Happiness = 0.6f,
            };
            w.Economy.Citizens.Add(c.Id, c);
            w.Economy.MoveIn(c, home.Id);
            w.Economy.Causes.Note(c.Id, Cause.PlayerCreated, home.Id);
            w.Economy.Ledger.Transfer(w.Economy.External, c, 300_000, Flow.Migration);
            w.Log("人生", $"{Names.Of(c.Id)}（{Age}歳）が町に引っ越してきました");
            return new PossessCitizenCommand(c.Id).Execute(w);
        }
    }

    /// <summary>Stops controlling; the resident carries on living on their own.</summary>
    public sealed class ReleaseCitizenCommand : IWorldCommand
    {
        public CommandResult Execute(WorldState w)
        {
            if (!w.Player.IsActive) return CommandResult.Fail("操作中の人物はいません");
            w.Player.Clear();
            return CommandResult.Ok("人物の操作を終えました（その人物は自分で生活を続けます）");
        }
    }

    // ---------------------------------------------------------------- moving around

    public sealed class WalkCommand : IResidentCommand
    {
        public readonly float X, Z, Heading;
        public WalkCommand(float x, float z, float heading) { X = x; Z = z; Heading = heading; }

        public CommandResult Execute(WorldState w, Citizen self)
        {
            PlayerState p = w.Player;
            if (p.Activity == AvatarActivity.Travelling) return CommandResult.Fail("移動中です");
            if (p.Activity == AvatarActivity.Sleeping) p.Activity = AvatarActivity.Idle;
            p.Heading = Heading;
            if (p.InsideBuildingId != 0)
            {
                InteriorDef room = InteriorOf(w, p.InsideBuildingId);
                p.RoomX = Math.Max(0.3f, Math.Min(room.Width - 0.3f, X));
                p.RoomZ = Math.Max(0.3f, Math.Min(room.Depth - 0.3f, Z));
                return CommandResult.Ok();
            }
            float dx = X - p.X, dz = Z - p.Z;
            if (dx * dx + dz * dz > PlayerState.MaxStepMeters * PlayerState.MaxStepMeters) return CommandResult.Fail("一度に移動できる距離を超えています");
            if (!w.IsWalkable(X, Z)) return CommandResult.Fail("そこには進めません");
            p.X = X;
            p.Z = Z;
            return CommandResult.Ok();
        }

        internal static InteriorDef InteriorOf(WorldState w, int buildingId)
        {
            w.TryGetBuilding(buildingId, out Building b);
            return w.Content.InteriorFor(b.Def) ?? new InteriorDef { Id = "default" };
        }
    }

    /// <summary>Walk through a building's door. Who may enter depends on the building and the time.</summary>
    public sealed class EnterBuildingCommand : IResidentCommand
    {
        public const float DoorReach = 7f;
        public readonly int BuildingId;
        public EnterBuildingCommand(int buildingId) { BuildingId = buildingId; }

        public CommandResult Execute(WorldState w, Citizen self)
        {
            PlayerState p = w.Player;
            if (p.InsideBuildingId != 0) return CommandResult.Fail("すでに建物の中にいます");
            if (p.Activity == AvatarActivity.Travelling) return CommandResult.Fail("移動中です");
            if (!w.TryGetBuilding(BuildingId, out Building b)) return CommandResult.Fail("建物がありません");
            var (x, z, _, _) = w.DoorOf(b);
            if ((x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z) > DoorReach * DoorReach) return CommandResult.Fail("出入口まで近づいてください");
            string denied = AccessDenied(w, self, b);
            if (denied != null) return CommandResult.Fail(denied);
            InteriorDef room = w.Content.InteriorFor(b.Def) ?? new InteriorDef { Id = "default" };
            p.InsideBuildingId = b.Id;
            p.RoomX = room.DoorX;
            p.RoomZ = room.DoorZ + 1f;
            p.Activity = AvatarActivity.Idle;
            return CommandResult.Ok(b.Def.Name + "に入りました");
        }

        /// <summary>Null when allowed, otherwise the reason.</summary>
        public static string AccessDenied(WorldState w, Citizen self, Building b)
        {
            if (b.Condition < 0.3f) return "被災していて立ち入れません";
            int hour = w.Clock.HourOfDay;
            if (b.Def.Housing > 0) return b.Id == self.HomeId ? null : "他人の家には入れません";
            if (self.JobId == b.Id) return null;
            w.Economy.Companies.TryGetValue(b.Id, out Company co);
            if (co != null && co.Closed) return "休業中です";
            if (co != null && co.IsRetail) return hour >= 8 && hour < 21 ? null : "営業時間外です（8〜21時）";
            if (b.Def.Id == "town_hall" || b.Def.Id == "school") return hour >= 8 && hour < 18 ? null : "開館時間外です（8〜18時）";
            return "関係者以外は入れません（ここで働くと入れます）";
        }
    }

    public sealed class ExitBuildingCommand : IResidentCommand
    {
        public CommandResult Execute(WorldState w, Citizen self)
        {
            if (w.Player.InsideBuildingId == 0) return CommandResult.Fail("建物の中にいません");
            AvatarSystem.LeaveBuilding(w, w.Player);
            return CommandResult.Ok("外に出ました");
        }
    }

    /// <summary>Goes to a building along the roads; takes the network's travel time in game time.</summary>
    public sealed class TravelCommand : IResidentCommand
    {
        public readonly int BuildingId;
        public TravelCommand(int buildingId) { BuildingId = buildingId; }

        public CommandResult Execute(WorldState w, Citizen self)
        {
            PlayerState p = w.Player;
            if (p.Activity == AvatarActivity.Travelling) return CommandResult.Fail("移動中です");
            if (!w.TryGetBuilding(BuildingId, out Building dest)) return CommandResult.Fail("行き先がありません");
            int from = p.InsideBuildingId != 0 ? p.InsideBuildingId : NearestBuilding(w, p.X, p.Z);
            if (from == 0) from = self.HomeId;
            float hours = from == 0 ? float.PositiveInfinity : w.Network.TravelHours(from, dest.Id);
            if (float.IsPositiveInfinity(hours)) return CommandResult.Fail("道路でつながっていません");
            p.InsideBuildingId = 0;
            p.Activity = AvatarActivity.Travelling;
            p.TravelTo = dest.Id;
            p.TravelArrive = w.Clock.Tick + hours;
            return CommandResult.Ok($"{dest.Def.Name}へ移動します（約{Math.Max(1, (int)Math.Round(hours * 60))}分）");
        }

        public static int NearestBuilding(WorldState w, float x, float z)
        {
            int best = 0;
            float bestD = 40f * 40f;
            foreach (Building b in w.Buildings)
            {
                var (dx, dz, _, _) = w.DoorOf(b);
                float d = (dx - x) * (dx - x) + (dz - z) * (dz - z);
                if (d < bestD) { bestD = d; best = b.Id; }
            }
            return best;
        }
    }

    // ---------------------------------------------------------------- using fixtures

    /// <summary>Uses a fixture of the room the person is in (sleep, eat, rest, work, study).</summary>
    public sealed class UseFixtureCommand : IResidentCommand
    {
        public readonly string Type;
        public UseFixtureCommand(string type) { Type = type; }

        public CommandResult Execute(WorldState w, Citizen self)
        {
            PlayerState p = w.Player;
            if (p.InsideBuildingId == 0) return CommandResult.Fail("建物の中で使えます");
            InteriorDef room = WalkCommand.InteriorOf(w, p.InsideBuildingId);
            if (!room.Fixtures.Exists(f => f.Type == Type)) return CommandResult.Fail("この建物にはありません");
            bool atHome = p.InsideBuildingId == self.HomeId;
            switch (Type)
            {
                case "bed":
                    if (!atHome) return CommandResult.Fail("自宅で眠れます");
                    p.Activity = AvatarActivity.Sleeping;
                    return CommandResult.Ok("眠りました（体力が回復します）");
                case "sofa":
                    p.Activity = AvatarActivity.Resting;
                    return CommandResult.Ok("休んでいます");
                case "fridge":
                    if (!atHome) return CommandResult.Fail("自宅の冷蔵庫です");
                    if (p.Pantry < 1) return CommandResult.Fail("食料がありません。店で買ってください");
                    p.Pantry -= 1;
                    p.Hunger = Math.Min(1f, p.Hunger + 0.45f);
                    p.Activity = AvatarActivity.Idle;
                    return CommandResult.Ok("食事をしました");
                case "workstation":
                    if (self.JobId != p.InsideBuildingId) return CommandResult.Fail("ここの従業員ではありません");
                    p.Activity = AvatarActivity.Working;
                    int h = w.Clock.HourOfDay;
                    return CommandResult.Ok(h >= AvatarSystem.WorkStartHour && h < AvatarSystem.WorkEndHour
                        ? "働き始めました" : "働き始めました（勤務時間は8〜18時。時間外は記録されません）");
                case "classroom":
                    if (self.Education >= 2) return CommandResult.Fail("これ以上の訓練はありません");
                    p.Activity = AvatarActivity.Studying;
                    return CommandResult.Ok($"職業訓練を受けています（1時間 {AvatarSystem.TuitionPerHour}円）");
                default:
                    return CommandResult.Ok();
            }
        }
    }

    /// <summary>Buys at the shop or office the person is in, from its real stock at its real price.</summary>
    public sealed class BuyCommand : IResidentCommand
    {
        public readonly string Resource;
        public readonly double Quantity;
        public BuyCommand(string resource, double quantity) { Resource = resource; Quantity = quantity; }

        public CommandResult Execute(WorldState w, Citizen self)
        {
            PlayerState p = w.Player;
            if (Quantity <= 0 || Quantity > 50) return CommandResult.Fail("数量は1〜50");
            if (!w.Economy.Companies.TryGetValue(p.InsideBuildingId, out Company shop) || !shop.Sells(Resource))
                return CommandResult.Fail("ここでは売っていません");
            if (shop.Closed || shop.Workers.Count == 0) return CommandResult.Fail("店員がいないため買えません");
            if (shop.Stock(Resource) < Quantity) return CommandResult.Fail("在庫が足りません");
            long cost = (long)Math.Round(Quantity * UnitPrice(w, shop, Resource));
            if (self.Cash < cost) return CommandResult.Fail($"お金が足りません（{cost:N0}円）");
            w.Economy.Ledger.Transfer(self, shop, cost, Flow.Retail);
            shop.RevenueToday += cost;
            shop.Inventory[Resource] = shop.Stock(Resource) - Quantity;
            Market.Add(shop.SalesToday, Resource, Quantity);
            if (Resource == "food") p.Pantry += Quantity;
            else if (Resource == "goods") { p.GoodsOwned += Quantity; self.GoodsMet = 1f; }
            else { self.ServicesMet = 1f; self.Happiness = Math.Min(1f, self.Happiness + 0.02f * (float)Quantity); }
            return CommandResult.Ok($"{w.Content.GetResource(Resource).Name}を{Quantity:0}つ買いました（{cost:N0}円）");
        }

        public static double UnitPrice(WorldState w, Company shop, string res) =>
            w.Economy.Market.GetPrice(res) * shop.Def.GetParam("markup", 1.0);
    }

    // ---------------------------------------------------------------- work, home, people

    /// <summary>Applies for a vacancy; same rules as everyone: opening, commute, education.</summary>
    public sealed class ApplyJobCommand : IResidentCommand
    {
        public readonly int BuildingId;
        public ApplyJobCommand(int buildingId) { BuildingId = buildingId; }

        public CommandResult Execute(WorldState w, Citizen self)
        {
            if (!w.Economy.Companies.TryGetValue(BuildingId, out Company co) || co.Closed) return CommandResult.Fail("その職場は募集していません");
            if (self.JobId == BuildingId) return CommandResult.Fail("すでにここで働いています");
            if (!self.IsWorkingAge(w.Clock.TotalDays)) return CommandResult.Fail("就業年齢（18〜64歳）ではありません");
            if (co.Workers.Count >= Math.Min(co.TargetWorkers, w.Effects.JobCapacity(co))) return CommandResult.Fail("欠員がありません");
            string why = LaborSystem.Ineligible(w, self, co);
            if (why != null) return CommandResult.Fail(why);
            w.Economy.Hire(co, self);
            self.Retired = false;
            self.CommuteHours = w.Network.TravelHours(self.HomeId, co.BuildingId);
            w.Player.AbsenceDays = 0;
            w.Player.WorkedHours = 8;
            w.Log("人生", $"{Names.Of(self.Id)}が{co.Def.Name} #{co.BuildingId}に就職しました");
            return CommandResult.Ok($"{co.Def.Name}に就職しました（日給 {co.Wage:N0}円）");
        }
    }

    public sealed class QuitJobCommand : IResidentCommand
    {
        public CommandResult Execute(WorldState w, Citizen self)
        {
            if (self.JobId == 0) return CommandResult.Fail("仕事に就いていません");
            w.Economy.Fire(self, Cause.QuitByChoice);
            if (w.Player.Activity == AvatarActivity.Working) w.Player.Activity = AvatarActivity.Idle;
            return CommandResult.Ok("退職しました");
        }
    }

    /// <summary>Moves to a vacant home (moving costs are paid to outside movers).</summary>
    public sealed class MoveHomeCommand : IResidentCommand
    {
        public const long MovingCost = 50_000;
        public readonly int BuildingId;
        public MoveHomeCommand(int buildingId) { BuildingId = buildingId; }

        public CommandResult Execute(WorldState w, Citizen self)
        {
            if (!w.TryGetBuilding(BuildingId, out Building b) || b.Def.Housing == 0) return CommandResult.Fail("住宅ではありません");
            if (self.HomeId == b.Id) return CommandResult.Fail("すでに住んでいます");
            if (b.Condition < 0.5f || !w.Network.HasAccess(b.Id)) return CommandResult.Fail("住める状態ではありません");
            if (w.Economy.Residents(b.Id) >= b.Def.Housing) return CommandResult.Fail("空き部屋がありません");
            if (self.Cash < MovingCost) return CommandResult.Fail($"引っ越し費用（{MovingCost:N0}円）が足りません");
            w.Economy.Ledger.Transfer(self, w.Economy.External, MovingCost, Flow.Moving);
            if (w.Player.InsideBuildingId == self.HomeId) AvatarSystem.LeaveBuilding(w, w.Player);
            w.Economy.MoveIn(self, b.Id);
            if (self.JobId != 0) self.CommuteHours = w.Network.TravelHours(b.Id, self.JobId);
            return CommandResult.Ok($"{b.Def.Name} #{b.Id}に引っ越しました");
        }
    }

    /// <summary>Talks with someone in the same building right now, or with family.</summary>
    public sealed class TalkCommand : IResidentCommand
    {
        public readonly int CitizenId;
        public TalkCommand(int citizenId) { CitizenId = citizenId; }

        public CommandResult Execute(WorldState w, Citizen self)
        {
            if (CitizenId == self.Id || !w.Economy.Citizens.TryGetValue(CitizenId, out Citizen other)) return CommandResult.Fail("相手がいません");
            bool family = other.HomeId != 0 && other.HomeId == self.HomeId && w.Player.InsideBuildingId == self.HomeId;
            int place = w.Player.InsideBuildingId;
            bool here = place != 0 && DailySchedule.At(w, other, w.Clock.HourOfDay, w.Clock.TotalDays).buildingId == place;
            if (!family && !here) return CommandResult.Fail("近くにいません");
            self.AddAffinity(other.Id, 0.08f);
            other.AddAffinity(self.Id, 0.08f);
            self.Happiness = Math.Min(1f, self.Happiness + 0.01f);
            return CommandResult.Ok($"{Names.Of(other.Id)}と話しました（親しさ {self.Affinity(other.Id):P0}）");
        }
    }

    public sealed class StopActivityCommand : IResidentCommand
    {
        public CommandResult Execute(WorldState w, Citizen self)
        {
            if (w.Player.Activity == AvatarActivity.Travelling) return CommandResult.Fail("移動中です");
            w.Player.Activity = AvatarActivity.Idle;
            return CommandResult.Ok();
        }
    }
}
