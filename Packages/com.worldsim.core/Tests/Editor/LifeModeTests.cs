using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Commands;
using WorldSim.Core.Economy;
using WorldSim.Core.People;
using WorldSim.Core.Persistence;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>Life mode stage 1: living as one resident inside the same simulated world.</summary>
    public class LifeModeTests
    {
        private sealed class Life
        {
            public WorldState W;
            public SimulationRunner R;
            public Citizen Me;
            public int Shop, Office, Home;
            public Actor Actor => Actor.Resident(Me.Id);
            public CommandResult Do(object cmd) => CommandGate.Execute(W, Actor, cmd);

            /// <summary>Runs until the clock shows the given hour (next occurrence).</summary>
            public void RunToHour(int hour)
            {
                do R.Step(); while (W.Clock.HourOfDay != hour);
            }
        }

        /// <summary>Village with homes, a stocked shop, an office with vacancies and power; the player lives as a new adult.</summary>
        private static Life NewLife(int education = 1)
        {
            WorldState w = TestContent.Village(2, outsideRoad: true);
            int shop = TestContent.PlaceSouth(w, "shop", 8);
            int office = TestContent.PlaceSouth(w, "office", 12);
            int plant = TestContent.PlaceSouth(w, "power_plant", 20);
            w.Economy.Ledger.Transfer(w.Economy.External, w.Economy.Companies[plant], 100_000_000, Flow.Investment);
            w.Economy.Companies[shop].AddStock("food", 5000);
            w.Economy.Companies[shop].AddStock("goods", 20);
            w.TryPlaceBuilding("apartment", new GridPos(30, 11)); // vacant home for the newcomer
            var l = new Life { W = w, R = WorldFactory.CreateRunner(w), Shop = shop, Office = office };
            CommandResult r = CommandGate.Execute(w, Actor.Creator, new CreateResidentCommand(30, education));
            Assert.IsTrue(r.Success, r.Message);
            l.Me = w.Economy.Citizens[r.CreatedId];
            l.Home = l.Me.HomeId;
            l.RunToHour(9); // shop and office open
            return l;
        }

        /// <summary>Makes room for one more employee (someone retires) without exceeding job capacity.</summary>
        private static void OpenVacancy(WorldState w, Company co)
        {
            int cap = w.Effects.JobCapacity(co);
            while (co.Workers.Count >= cap) w.Economy.Fire(w.Economy.Citizens[co.Workers[co.Workers.Count - 1]]);
            co.TargetWorkers = cap;
        }

        private static void GoInto(Life l, int buildingId)
        {
            if (l.W.Player.InsideBuildingId != 0) Assert.IsTrue(l.Do(new ExitBuildingCommand()).Success);
            l.W.TryGetBuilding(buildingId, out Building b);
            var (x, z, nx, nz) = l.W.DoorOf(b);
            l.W.Player.X = x + nx * 2; // teleport for the test; walking is tested separately
            l.W.Player.Z = z + nz * 2;
            CommandResult r = l.Do(new EnterBuildingCommand(buildingId));
            Assert.IsTrue(r.Success, r.Message);
        }

        [Test]
        public void ModeIsPermissionNotView()
        {
            Life l = NewLife();
            CommandResult build = l.Do(new PlaceBuildingCommand("shop", new GridPos(40, 30)));
            Assert.IsFalse(build.Success, "a resident cannot edit the world");
            StringAssert.Contains("世界を編集できません", build.Message);
            Assert.IsTrue(CommandGate.Execute(l.W, Actor.Creator, new PlaceBuildingCommand("shop", new GridPos(40, 30))).Success, "the creator still can");
            Assert.IsFalse(CommandGate.Execute(l.W, Actor.Creator, new QuitJobCommand()).Success, "personal actions need a resident actor");
            Assert.IsFalse(CommandGate.Execute(l.W, Actor.Resident(l.Me.Id + 999), new ExitBuildingCommand()).Success, "only the controlled resident");
            long tick = l.W.Clock.Tick;
            l.R.StepMany(3);
            Assert.AreEqual(tick + 3, l.W.Clock.Tick, "living as someone does not stop the world");
        }

        [Test]
        public void NewcomerIsAnOrdinaryResident()
        {
            Life l = NewLife();
            Assert.AreEqual(0, l.W.Economy.TotalMoney(), "their savings came from outside through the ledger");
            Assert.AreNotEqual(0, l.Me.HomeId);
            Assert.AreEqual(l.Me.Id, l.W.Player.CitizenId);
            Assert.IsFalse(string.IsNullOrEmpty(Names.Of(l.Me.Id)));
            Assert.IsTrue(CommandGate.Execute(l.W, Actor.Creator, new ReleaseCitizenCommand()).Success);
            Assert.IsFalse(l.W.Player.IsActive);
            Assert.IsTrue(l.W.Economy.Citizens.ContainsKey(l.Me.Id), "released residents keep living");
        }

        [Test]
        public void WalkingRespectsBuildingsWaterAndStepLimits()
        {
            Life l = NewLife();
            PlayerState p = l.W.Player;
            float x0 = p.X, z0 = p.Z;
            Assert.IsTrue(l.Do(new WalkCommand(x0 + 3, z0, 0)).Success);
            Assert.IsFalse(l.Do(new WalkCommand(x0 + 200, z0, 0)).Success, "no teleporting");
            l.W.TryGetBuilding(l.Shop, out Building shop);
            float sx = (shop.Origin.X + 1) * l.W.Terrain.CellSize, sz = (shop.Origin.Z + 1) * l.W.Terrain.CellSize;
            p.X = sx - 10; p.Z = sz;
            Assert.IsFalse(l.Do(new WalkCommand(sx, sz, 0)).Success, "cannot walk through a building");
        }

        [Test]
        public void DoorsAndOpeningHours()
        {
            Life l = NewLife();
            int otherHome = l.W.Buildings.First(b => b.Def.Housing > 0 && b.Id != l.Home).Id;
            GoInto(l, l.Home);
            Assert.IsTrue(l.Do(new ExitBuildingCommand()).Success);
            l.W.TryGetBuilding(otherHome, out Building other);
            var (x, z, _, _) = l.W.DoorOf(other);
            l.W.Player.X = x; l.W.Player.Z = z - 1;
            StringAssert.Contains("他人の家", l.Do(new EnterBuildingCommand(otherHome)).Message);
            l.W.Player.X = 0; l.W.Player.Z = 0;
            StringAssert.Contains("近づいて", l.Do(new EnterBuildingCommand(l.Shop)).Message);
            GoInto(l, l.Shop);
            l.Do(new ExitBuildingCommand());
            l.RunToHour(23);
            l.W.TryGetBuilding(l.Shop, out Building shop);
            var d = l.W.DoorOf(shop);
            l.W.Player.X = d.x; l.W.Player.Z = d.z - 1;
            StringAssert.Contains("営業時間外", l.Do(new EnterBuildingCommand(l.Shop)).Message);
        }

        [Test]
        public void ShoppingUsesTheShopsRealStockPriceAndLedger()
        {
            Life l = NewLife();
            Company shop = l.W.Economy.Companies[l.Shop];
            GoInto(l, l.Shop);
            double stock = shop.Stock("food");
            long cash = l.Me.Cash, shopCash = shop.Cash;
            double unit = BuyCommand.UnitPrice(l.W, shop, "food");
            CommandResult r = l.Do(new BuyCommand("food", 5));
            Assert.IsTrue(r.Success, r.Message);
            long cost = (long)System.Math.Round(5 * unit);
            Assert.AreEqual(cash - cost, l.Me.Cash);
            Assert.AreEqual(shopCash + cost, shop.Cash);
            Assert.AreEqual(stock - 5, shop.Stock("food"), 1e-9);
            Assert.AreEqual(3 + 5, l.W.Player.Pantry, 1e-9);
            Assert.AreEqual(0, l.W.Economy.TotalMoney());

            // Prices are the market's: when food gets dearer, the same basket costs more.
            l.W.Economy.Market.Price["food"] *= 1.5;
            Assert.Greater(BuyCommand.UnitPrice(l.W, shop, "food"), unit * 1.4);
            Assert.IsFalse(l.Do(new BuyCommand("food", 1000)).Success, "not more than the stock");
        }

        [Test]
        public void EatingAndSleepingFollowTheClock()
        {
            Life l = NewLife();
            PlayerState p = l.W.Player;
            GoInto(l, l.Shop);
            Assert.IsFalse(l.Do(new UseFixtureCommand("bed")).Success, "no bed in the shop");
            l.Do(new ExitBuildingCommand());
            float hunger = p.Hunger;
            l.R.StepMany(6);
            Assert.Less(p.Hunger, hunger, "hunger grows over time");
            GoInto(l, l.Home);
            double pantry = p.Pantry;
            Assert.IsTrue(l.Do(new UseFixtureCommand("fridge")).Success);
            Assert.AreEqual(pantry - 1, p.Pantry);
            p.Energy = 0.2f;
            Assert.IsTrue(l.Do(new UseFixtureCommand("bed")).Success);
            Assert.AreEqual(AvatarActivity.Sleeping, p.Activity);
            l.R.StepMany(6);
            Assert.Greater(p.Energy, 0.9f, "sleep restores energy");
        }

        [Test]
        public void WorkingHoursEarnWagesAndAbsenceCostsTheJob()
        {
            Life l = NewLife();
            Company office = l.W.Economy.Companies[l.Office];
            // Start on a workday morning; apply, then go to the desk.
            do l.RunToHour(8); while (!DailySchedule.IsWorkday(l.W.Clock.TotalDays));
            OpenVacancy(l.W, office);
            office.ProfitAvg = 100_000_000; // a thriving office: no staff cuts during this test
            l.W.Economy.Ledger.Transfer(l.W.Economy.External, office, 100_000_000, Flow.Investment);
            CommandResult hired = l.Do(new ApplyJobCommand(l.Office));
            Assert.IsTrue(hired.Success, "apply: " + hired.Message);
            Assert.AreEqual(l.Office, l.Me.JobId);
            GoInto(l, l.Office);
            l.W.Player.Energy = 1f; // well rested: an exhausted person gets no work done
            l.W.Player.Hunger = 1f;
            CommandResult work = l.Do(new UseFixtureCommand("workstation"));
            Assert.IsTrue(work.Success, work.Message);
            l.RunToHour(18);
            Assert.GreaterOrEqual(l.W.Player.WorkedHours, 8f);
            l.RunToHour(10); // payday runs at 09:00
            Assert.AreEqual(l.Office, l.Me.JobId, "still employed");
            long wagesPaid = l.W.Economy.Ledger.Yesterday[(int)Flow.Wages];
            Assert.Greater(wagesPaid, 0);
            Assert.GreaterOrEqual(l.Me.LastNetIncome, (long)(office.Wage * (1 - l.W.Economy.City.Policy.IncomeTax)) - 2, "a full day's pay after tax");

            // Then staying home on workdays: unpaid and, after a few days, dismissed.
            l.Do(new ExitBuildingCommand());
            for (int i = 0; i < 10 && l.Me.JobId != 0; i++)
            {
                office.TargetWorkers = office.Def.Jobs; // a profitable office does not cut staff,
                office.ProfitAvg = 10_000_000;          // so only absence can cost the job here
                l.RunToHour(10);
            }
            Assert.AreEqual(0, l.Me.JobId, "missing work gets you fired");
            Assert.That(l.W.Events.Any(e => e.Message.Contains("解雇")), string.Join(" | ", l.W.Events.Skip(System.Math.Max(0, l.W.Events.Count - 8)).Select(e => e.Message)) + $" job={l.Me.JobId} abs={l.W.Player.AbsenceDays}");
        }

        [Test]
        public void EmployerBankruptcyMakesThePlayerUnemployed()
        {
            Life l = NewLife();
            Company shop = l.W.Economy.Companies[l.Shop];
            OpenVacancy(l.W, shop);
            CommandResult hired = l.Do(new ApplyJobCommand(l.Shop));
            Assert.IsTrue(hired.Success, hired.Message);
            l.W.Economy.Ledger.Transfer(shop, l.W.Economy.External, shop.Cash, Flow.Divestment);
            shop.Inventory.Clear();
            l.R.StepMany(20 * SimClock.TicksPerDay);
            Assert.IsTrue(shop.Closed);
            Assert.AreEqual(0, l.Me.JobId, "the world's economy reaches the player");
        }

        [Test]
        public void TheWorldDoesNotChooseForThePlayer()
        {
            Life l = NewLife();
            Company office = l.W.Economy.Companies[l.Office];
            office.TargetWorkers = office.Def.Jobs;
            long cash = l.Me.Cash;
            double pantry = l.W.Player.Pantry;
            l.R.StepMany(3 * SimClock.TicksPerDay);
            Assert.AreEqual(0, l.Me.JobId, "no automatic job for the player");
            Assert.GreaterOrEqual(l.Me.Cash, cash, "no automatic shopping either (only benefit and dividends come in)");
            Assert.AreEqual(pantry, l.W.Player.Pantry);
            Assert.That(l.W.Economy.Citizens.Values.Where(c => c.Id != l.Me.Id).Any(c => c.JobId == l.Office), "other residents still look for work");
        }

        [Test]
        public void TravelTimeComesFromTheRoadNetwork()
        {
            Life l = NewLife();
            GoInto(l, l.Home);
            Assert.IsTrue(l.Do(new TravelCommand(l.Office)).Success);
            double direct = l.W.Player.TravelArrive - l.W.Clock.Tick;
            Assert.AreEqual(AvatarActivity.Travelling, l.W.Player.Activity);
            l.R.StepMany(2);
            Assert.AreEqual(AvatarActivity.Idle, l.W.Player.Activity, "arrived");
            Assert.AreEqual(0, l.W.Player.InsideBuildingId);

            // Make the trip longer by jamming the road: the same trip now takes longer.
            for (int i = 0; i < l.W.CongestionBasis.Length; i++) l.W.CongestionBasis[i] = 5000;
            l.W.Network.Invalidate();
            GoInto(l, l.Home);
            Assert.IsTrue(l.Do(new TravelCommand(l.Office)).Success);
            Assert.Greater(l.W.Player.TravelArrive - l.W.Clock.Tick, direct * 2);
        }

        [Test]
        public void TalkingNeedsSomeoneNearbyAndBuildsRelationships()
        {
            Life l = NewLife();
            Citizen stranger = l.W.Economy.Citizens.Values.First(c => c.Id != l.Me.Id);
            GoInto(l, l.Home);
            Assert.IsFalse(l.Do(new TalkCommand(stranger.Id)).Success, "not here");
            int workerId = l.W.Economy.Companies[l.Office].Workers.First();
            l.RunToHour(10);
            GoInto(l, l.Shop);
            l.Do(new ExitBuildingCommand());
            Company office = l.W.Economy.Companies[l.Office];
            OpenVacancy(l.W, office);
            CommandResult hired = l.Do(new ApplyJobCommand(l.Office));
            Assert.IsTrue(hired.Success, hired.Message);
            GoInto(l, l.Office);
            CommandResult r = l.Do(new TalkCommand(workerId));
            Assert.IsTrue(r.Success, r.Message);
            Assert.Greater(l.Me.Affinity(workerId), 0f);
            Assert.Greater(l.W.Economy.Citizens[workerId].Affinity(l.Me.Id), 0f, "relationships are mutual");
        }

        [Test]
        public void TrainingRaisesEducationForTuition()
        {
            Life l = NewLife(education: 0);
            int school = TestContent.PlaceSouth(l.W, "school", 24);
            Company office = l.W.Economy.Companies[l.Office];
            OpenVacancy(l.W, office);
            CommandResult r = l.Do(new ApplyJobCommand(l.Office));
            StringAssert.Contains("中等教育", r.Message, "offices need secondary education");
            l.RunToHour(9);
            GoInto(l, school);
            long cityCash = l.W.Economy.City.Cash;
            Assert.IsTrue(l.Do(new UseFixtureCommand("classroom")).Success);
            for (int i = 0; i < AvatarSystem.StudyHoursPerLevel + 2 && l.Me.Education == 0; i++) l.R.Step();
            Assert.AreEqual(1, l.Me.Education);
            Assert.AreEqual(0, l.W.Economy.TotalMoney());
        }

        [Test]
        public void SaveLoadKeepsThePersonAndTheFuture()
        {
            Life l = NewLife();
            GoInto(l, l.Shop);
            l.Do(new BuyCommand("food", 2));
            l.Do(new ExitBuildingCommand());
            l.Me.AddAffinity(l.W.Economy.Citizens.Keys.First(), 0.3f);
            l.R.StepMany(5);
            WorldState loaded = SaveSystem.Load(TestContent.Load(), SaveSystem.Save(l.W));
            Assert.AreEqual(l.W.ComputeHash(), loaded.ComputeHash());
            Assert.AreEqual(l.Me.Id, loaded.Player.CitizenId);
            Assert.AreEqual(l.W.Player.Pantry, loaded.Player.Pantry);
            WorldFactory.CreateRunner(l.W).StepMany(48);
            WorldFactory.CreateRunner(loaded).StepMany(48);
            Assert.AreEqual(l.W.ComputeHash(), loaded.ComputeHash(), "same future after loading");
        }

        [Test]
        public void VersionOneSavesStillLoad()
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(3));
            string v2 = SaveSystem.Save(w);
            string v1 = v2.Replace("\"version\":2", "\"version\":1");
            WorldState loaded = SaveSystem.Load(TestContent.Load(), v1);
            Assert.IsFalse(loaded.Player.IsActive);
            Assert.AreEqual(w.Economy.Citizens.Count, loaded.Economy.Citizens.Count);
        }

        [Test]
        public void LongLifeKeepsTheWorldConsistent()
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(6));
            Citizen adult = w.Economy.Citizens.Values.First(c => c.IsWorkingAge(0) && c.JobId != 0);
            Assert.IsTrue(CommandGate.Execute(w, Actor.Creator, new PossessCitizenCommand(adult.Id)).Success);
            SimulationRunner r = WorldFactory.CreateRunner(w);
            for (int i = 0; i < 12; i++)
            {
                r.StepMany(30 * SimClock.TicksPerDay);
                CollectionAssert.IsEmpty(WorldValidator.Validate(w), $"month {i}");
            }
            Assert.IsTrue(w.Player.IsActive, "the player's resident is still there after a year");
            Assert.That(w.History.Select(s => s.TotalMoney), Is.All.EqualTo(0L));
        }
    }
}
