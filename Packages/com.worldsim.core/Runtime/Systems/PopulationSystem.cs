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
        public const double BirthRatePerYear = 0.05;
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
            UpdateHappiness(w, today);

            var leaving = new List<Citizen>();
            var dying = new List<Citizen>();
            var newborns = new List<Citizen>();
            foreach (Citizen c in e.Citizens.Values)
            {
                int age = c.AgeYears(today);
                if (age >= 60 && rng.Chance(0.01 * Math.Exp((age - 60) / 8.0) / SimClock.DaysPerYear)) { dying.Add(c); continue; }
                if (c.HomeId == 0)
                {
                    c.UnemployedDays++;
                    if (!FindHome(w, c, homes) && c.UnemployedDays > 14) { leaving.Add(c); continue; }
                }
                if (c.JobId == 0 && !c.Retired && c.IsWorkingAge(today)) c.UnemployedDays++;
                if (c.Happiness < 0.3f && rng.Chance(0.01)) { leaving.Add(c); continue; }
                if (c.JobId == 0 && c.UnemployedDays > 30 && c.IsWorkingAge(today) && rng.Chance(0.03)) { leaving.Add(c); continue; }
                if (age >= 20 && age <= 40 && c.HomeId != 0 && rng.Chance(BirthRatePerYear / SimClock.DaysPerYear)
                    && w.TryGetBuilding(c.HomeId, out Building h) && e.Residents(h.Id) < h.Def.Housing)
                {
                    newborns.Add(new Citizen { BirthDay = today, Education = 0, SchoolDays = 0, Happiness = c.Happiness, HomeId = c.HomeId });
                }
                if (c.JobId != 0 && c.CommuteHours > MoveCommuteHours && rng.Chance(MoveCheckShare)) MoveCloserToWork(w, c, homes);
            }
            foreach (Citizen c in dying) { e.RemoveCitizen(c, e.City, Flow.Inheritance); stats.Deaths++; }
            foreach (Citizen c in leaving) { e.RemoveCitizen(c, e.External, Flow.Migration); stats.Emigrants++; }
            foreach (Citizen b in newborns)
            {
                if (!w.TryGetBuilding(b.HomeId, out Building hb) || e.Residents(b.HomeId) >= hb.Def.Housing) continue;
                b.Id = w.AllocateId();
                int home = b.HomeId;
                b.HomeId = 0;
                e.Citizens.Add(b.Id, b);
                e.MoveIn(b, home);
                stats.Births++;
            }
            Immigrate(w, today, homes);
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

        private static bool FindHome(WorldState w, Citizen c, List<Building> homes)
        {
            Building best = null;
            float bestScore = float.NegativeInfinity;
            foreach (Building b in homes)
            {
                if (!Habitable(w, b)) continue;
                float score = HomeScore(w, b, c.JobId);
                if (score > bestScore) { bestScore = score; best = b; }
            }
            if (best == null) return false;
            w.Economy.MoveIn(c, best.Id);
            c.UnemployedDays = 0;
            return true;
        }

        /// <summary>Higher is better: short commute, clean air.</summary>
        public static float HomeScore(WorldState w, Building home, int jobId)
        {
            float score = -w.Environment.PollutionAt(home.Origin) * 0.05f;
            if (jobId != 0)
            {
                float h = w.Network.TravelHours(home.Id, jobId);
                score -= float.IsPositiveInfinity(h) ? 100f : h * 4f;
            }
            return score;
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
            if (best == null) return;
            w.Economy.MoveIn(c, best.Id);
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
            double expected = vacancies * 0.15
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
                e.Daily.Immigrants++;
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
                }
                t -= (1 - energy) * 0.2;
                t = Math.Max(0, Math.Min(1, t));
                c.Happiness += (float)((t - c.Happiness) * 0.1);
            }
        }
    }
}
