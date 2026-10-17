using System;
using System.Collections.Generic;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Demography and residence: schooling, finding and changing homes (moving closer to work when
    /// commutes are long), births, deaths, immigration driven by vacancies and housing, emigration
    /// driven by unhappiness and unemployment, and each resident's happiness.
    /// </summary>
    public sealed class PopulationSystem : ISimulationSystem
    {
        public const int SchoolYears = 12;
        /// <summary>Share of newcomers who arrive with a partner (S4).</summary>
        public const double CoupleArrivalShare = 0.4;
        public const double MoveCheckShare = 0.02;
        /// <summary>Residents commuting longer than this occasionally look for a closer home.</summary>
        public const float MoveCommuteHours = 0.2f;

        public string Name => "Population";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EconomyState e = w.Economy;
            long today = w.Clock.TotalDays;
            DeterministicRandom rng = w.Random;
            var stats = w.Economy.Daily;
            stats.Clear();

            List<Building> homes = ResidentialBuildings(w);
            Education(w, today);
            HealthSystem.Update(w, today);
            UpdateHappiness(w, today);
            Households.Update(w, today, rng, w.Player.CitizenId);

            var leaving = new List<(Citizen c, Cause why)>();
            var dying = new List<(Citizen c, Cause why)>();
            var newborns = new List<Citizen>();
            int player = w.Player.CitizenId;
            foreach (Citizen c in e.Citizens.Values)
            {
                int age = c.AgeYears(today);
                if (c.Id == player)
                {
                    // The player's life choices (moving, leaving town) are theirs; ageing still applies.
                    if (c.JobId == 0 && !c.Retired && c.IsWorkingAge(today)) c.UnemployedDays++;
                    continue;
                }
                if (age >= 60 && rng.Chance(0.01 * Math.Exp((age - 60) / 8.0) / SimClock.DaysPerYear * HealthSystem.MortalityFactor(c))) { dying.Add((c, Cause.Died)); continue; }
                if (c.Health < 0.25f && rng.Chance(0.003)) { dying.Add((c, Cause.DiedOfIllness)); continue; }
                if (c.HomeId == 0)
                {
                    c.UnemployedDays++;
                    if (!FindHome(w, c, homes) && c.UnemployedDays > 14) { leaving.Add((c, Cause.EmigratedHomeless)); continue; }
                }
                if (c.JobId == 0 && !c.Retired && c.IsWorkingAge(today)) c.UnemployedDays++;
                if (c.Happiness < 0.3f && rng.Chance(0.01)) { leaving.Add((c, Cause.EmigratedUnhappy)); continue; }
                if (c.JobId == 0 && c.UnemployedDays > 30 && c.IsWorkingAge(today) && rng.Chance(0.03)) { leaving.Add((c, Cause.EmigratedUnemployed)); continue; }
                // Children are born to couples living together (counted once, by the partner with the lower id).
                if (c.PartnerId > c.Id && age >= 20 && age <= 42 && c.HomeId != 0
                    && e.Citizens.TryGetValue(c.PartnerId, out Citizen partner) && partner.HomeId == c.HomeId
                    && partner.AgeYears(today) >= 20 && partner.AgeYears(today) <= 42
                    && rng.Chance(Households.CoupleBirthRatePerYear / SimClock.DaysPerYear)
                    && w.TryGetBuilding(c.HomeId, out Building h) && e.Residents(h.Id) < h.Def.Housing)
                {
                    newborns.Add(new Citizen { BirthDay = today, Education = 0, SchoolDays = 0, Happiness = c.Happiness, HomeId = c.HomeId, Health = 1f, ParentId = c.Id });
                }
                if (c.JobId != 0 && c.CommuteHours > MoveCommuteHours && rng.Chance(MoveCheckShare)) MoveCloserToWork(w, c, homes);
                else if (c.HomeId != 0 && HousingMarket.RentBurden(c) > 0.5 && rng.Chance(MoveCheckShare)) MoveForCheaperRent(w, c, homes);
            }
            foreach (var (c, why) in dying)
            {
                Households.Separate(e, c); // the partner is widowed
                e.RemoveCitizen(c, e.City, Flow.Inheritance, why);
                stats.Deaths++;
            }
            foreach (var (c, why) in leaving)
            {
                if (!e.Citizens.ContainsKey(c.Id)) continue; // already left with their family
                // A household leaves town together; a household that includes the player stays.
                List<Citizen> family = Households.Members(e, c, today);
                if (family.Exists(m => m.Id == player)) continue;
                foreach (Citizen m in family)
                {
                    e.RemoveCitizen(m, e.External, Flow.Migration, m == c ? why : Cause.FamilyFollowed);
                    stats.Emigrants++;
                }
            }
            foreach (Citizen b in newborns)
            {
                if (!w.TryGetBuilding(b.HomeId, out Building hb) || e.Residents(b.HomeId) >= hb.Def.Housing) continue;
                b.Id = w.AllocateId();
                int home = b.HomeId;
                b.HomeId = 0;
                e.Citizens.Add(b.Id, b);
                e.MoveIn(b, home);
                e.Causes.Note(b.Id, Cause.Born, home);
                stats.Births++;
            }
            Immigrate(w, today, homes);
            Households.LeaveParents(w, today, rng, homes, (c, except) => FindHome(w, c, homes, except, alone: true));
        }

        public static List<Building> ResidentialBuildings(WorldState w)
        {
            var list = new List<Building>();
            foreach (Building b in w.Buildings)
                if (b.Def.Housing > 0) list.Add(b);
            return list;
        }

        private static bool Habitable(WorldState w, Building b) =>
            b.Condition >= 0.5f && w.Network.HasAccess(b.Id) && w.Economy.Residents(b.Id) < b.Def.Housing;

        /// <summary>
        /// Finds a home for a homeless resident and the homeless members of their household (partner,
        /// children), or for the resident alone (a young adult leaving their parents).
        /// </summary>
        private static bool FindHome(WorldState w, Citizen c, List<Building> homes, int except = 0, bool alone = false)
        {
            EconomyState e = w.Economy;
            List<Citizen> members = alone ? new List<Citizen> { c } : Households.Members(e, c, w.Clock.TotalDays);
            Building best = null;
            float bestScore = float.NegativeInfinity;
            foreach (Building b in homes)
            {
                if (b.Id == except || !Habitable(w, b) || e.Residents(b.Id) + members.Count > b.Def.Housing || !HousingMarket.CanAfford(w, c, b.Id)) continue;
                float score = HomeScore(w, b, c);
                if (score > bestScore) { bestScore = score; best = b; }
            }
            if (best == null) return false;
            foreach (Citizen m in members) e.MoveIn(m, best.Id);
            c.UnemployedDays = 0;
            return true;
        }

        /// <summary>
        /// Higher is better: short commute, clean air, a good location (land value), affordable rent.
        /// Low incomes weigh rent heavily, so they settle where land (and rent) is cheap.
        /// </summary>
        public static float HomeScore(WorldState w, Building home, Citizen c)
        {
            float score = -w.Environment.PollutionAt(home.Origin) * 0.05f;
            if (c.JobId != 0)
            {
                float h = w.Network.TravelHours(home.Id, c.JobId);
                score -= float.IsPositiveInfinity(h) ? 100f : h * 4f;
            }
            score += (float)(HousingMarket.LandValue(w, home.Id) / 100.0 * HousingMarket.LocationWeight(c));
            score -= HousingMarket.RentFor(w, home.Id) / 1000f * HousingMarket.RentWeight(c);
            return score;
        }

        /// <summary>A household paying more than half its income in rent looks for a cheaper home it can reach.</summary>
        private static void MoveForCheaperRent(WorldState w, Citizen c, List<Building> homes)
        {
            long current = HousingMarket.RentFor(w, c.HomeId);
            Building best = null;
            long bestRent = (long)(current * 0.85);
            foreach (Building b in homes)
            {
                if (b.Id == c.HomeId || !Habitable(w, b)) continue;
                if (c.JobId != 0 && w.Network.TravelHours(b.Id, c.JobId) > LaborSystem.MaxCommuteHours) continue;
                long rent = HousingMarket.RentFor(w, b.Id);
                if (rent < bestRent) { bestRent = rent; best = b; }
            }
            if (best == null || !Households.MoveTogether(w, c, best.Id, w.Clock.TotalDays)) return;
            w.Economy.Causes.Note(c.Id, Cause.MovedForCheaperRent, best.Id);
            if (c.JobId != 0) c.CommuteHours = w.Network.TravelHours(best.Id, c.JobId);
            w.Economy.Daily.Moves++;
        }

        private static void MoveCloserToWork(WorldState w, Citizen c, List<Building> homes)
        {
            float current = w.Network.TravelHours(c.HomeId, c.JobId);
            Building best = null;
            float bestH = current - 0.05f;
            foreach (Building b in homes)
            {
                if (b.Id == c.HomeId || !Habitable(w, b)) continue;
                float h = w.Network.TravelHours(b.Id, c.JobId);
                if (h < bestH) { bestH = h; best = b; }
            }
            if (best == null || !Households.MoveTogether(w, c, best.Id, w.Clock.TotalDays)) return;
            w.Economy.Causes.Note(c.Id, Cause.MovedCloserToWork, best.Id);
            c.CommuteHours = bestH;
            w.Economy.Daily.Moves++;
        }

        private static void Immigrate(WorldState w, long today, List<Building> homes)
        {
            EconomyState e = w.Economy;
            int vacantHomes = 0;
            foreach (Building b in homes) if (Habitable(w, b)) vacantHomes += b.Def.Housing - e.Residents(b.Id);
            if (vacantHomes == 0) return;
            int vacancies = 0;
            foreach (Company co in e.Companies.Values)
                if (!co.Closed && w.Network.HasAccess(co.BuildingId)) vacancies += Math.Max(0, co.TargetWorkers - co.Workers.Count);
            double avgHappiness = AverageHappiness(e);
            double unemployment = UnemploymentRate(w, today);
            // Newcomers follow job openings; spare housing attracts a trickle only while jobs are easy to find.
            double forJobs = vacancies * 0.15;
            double expected = forJobs
                + vacantHomes * 0.005 * Math.Max(0, (avgHappiness - 0.45) * 4) * Math.Max(0, 1 - unemployment * 8);
            int n = (int)Math.Floor(expected);
            if (w.Random.Chance(expected - n)) n++;
            n = Math.Min(n, Math.Min(vacantHomes, 40));
            for (int i = 0; i < n; i++)
            {
                var c = new Citizen
                {
                    Id = w.AllocateId(),
                    BirthDay = today - w.Random.NextInt(20, 46) * SimClock.DaysPerYear - w.Random.NextInt(0, 360),
                    Education = w.Random.NextInt(0, 3),
                    SchoolDays = -1,
                    Happiness = 0.6f,
                };
                e.Citizens.Add(c.Id, c);
                e.Ledger.Transfer(e.External, c, w.Random.NextInt(100_000, 400_000), Flow.Migration);
                if (!FindHome(w, c, homes)) { e.RemoveCitizen(c, e.External, Flow.Migration); break; }
                // Label arrivals by which pull brought them: job openings first, then spare housing.
                Cause pull = (i + 0.5) / n <= forJobs / expected ? Cause.ImmigratedForJobs : Cause.ImmigratedForHousing;
                e.Causes.Note(c.Id, pull, c.HomeId);
                e.Daily.Immigrants++;
                // Some newcomers arrive as a couple (S4), if their new home has room for two.
                if (w.Random.Chance(CoupleArrivalShare) && w.TryGetBuilding(c.HomeId, out Building home) && e.Residents(home.Id) < home.Def.Housing)
                {
                    var p = new Citizen
                    {
                        Id = w.AllocateId(),
                        BirthDay = c.BirthDay - w.Random.NextInt(-4, 5) * SimClock.DaysPerYear,
                        Education = w.Random.NextInt(0, 3),
                        SchoolDays = -1,
                        Happiness = 0.6f,
                    };
                    e.Citizens.Add(p.Id, p);
                    e.Ledger.Transfer(e.External, p, w.Random.NextInt(100_000, 400_000), Flow.Migration);
                    e.MoveIn(p, home.Id);
                    Households.Marry(e, c, p);
                    // Their arrival, not the marriage, is why the population changed today.
                    e.Causes.Note(c.Id, pull, home.Id);
                    e.Causes.Note(p.Id, pull, home.Id);
                    e.Daily.Immigrants++;
                }
            }
        }

        public static double UnemploymentRate(WorldState w, long today)
        {
            int labour = 0, unemployed = 0;
            foreach (Citizen c in w.Economy.Citizens.Values)
            {
                if (c.Retired || !c.IsWorkingAge(today)) continue;
                labour++;
                if (c.JobId == 0) unemployed++;
            }
            return labour == 0 ? 0 : (double)unemployed / labour;
        }

        public static double AverageHappiness(EconomyState e)
        {
            if (e.Citizens.Count == 0) return 0.5;
            double s = 0;
            foreach (Citizen c in e.Citizens.Values) s += c.Happiness;
            return s / e.Citizens.Count;
        }

        private static void Education(WorldState w, long today)
        {
            EconomyState e = w.Economy;
            double capacity = 0;
            foreach (Company co in e.Companies.Values)
            {
                double students = co.Def.GetParam("students");
                if (students > 0 && !co.Closed && co.TargetWorkers > 0)
                    capacity += students * co.Workers.Count / (double)co.TargetWorkers;
            }
            int children = 0;
            foreach (Citizen c in e.Citizens.Values) { int a = c.AgeYears(today); if (a >= 6 && a < 18) children++; }
            double coverage = children == 0 ? 1 : Math.Min(1, capacity / children);
            foreach (Citizen c in e.Citizens.Values)
            {
                if (c.SchoolDays < 0) continue;
                int age = c.AgeYears(today);
                if (age >= 6 && age < 18)
                {
                    // Deterministic per-child draw so the same children attend while seats are short.
                    double draw = (uint)(c.Id * 2654435761u) / 4294967296.0;
                    if (draw < coverage) c.SchoolDays++;
                }
                else if (age >= 18)
                {
                    int full = SchoolYears * SimClock.DaysPerYear;
                    c.Education = c.SchoolDays >= full * 0.8 ? 2 : c.SchoolDays >= full * 0.3 ? 1 : 0;
                    c.SchoolDays = -1;
                }
            }
        }

        private static void UpdateHappiness(WorldState w, long today)
        {
            EconomyState e = w.Economy;
            double energy = w.Environment.EnergyRatio;
            foreach (Citizen c in e.Citizens.Values)
            {
                double t = 0.45;
                int age = c.AgeYears(today);
                bool occupied = c.JobId != 0 || c.Retired || age < 18;
                t += occupied ? 0.2 : -0.05;
                if (age >= 18) t += 0.15 * c.FoodMet + 0.05 * c.GoodsMet + 0.05 * c.ServicesMet - 0.15;
                t -= Math.Min(0.3, c.CommuteHours * 0.1);
                if (c.HomeId == 0) t -= 0.3;
                else if (w.TryGetBuilding(c.HomeId, out Building home))
                {
                    t -= Math.Min(0.3, w.Environment.PollutionAt(home.Origin) / 100.0 * 0.3);
                    if (home.Condition < 0.7f) t -= 0.2;
                    // Rent above 30% of income strains the household.
                    t -= Math.Min(0.15, Math.Max(0, HousingMarket.RentBurden(c) - 0.3) * 0.5);
                }
                t -= (1 - energy) * 0.2;
                t -= (1 - c.Health) * 0.2;
                t = Math.Max(0, Math.Min(1, t));
                c.Happiness += (float)((t - c.Happiness) * 0.1);
            }
        }
    }
}
