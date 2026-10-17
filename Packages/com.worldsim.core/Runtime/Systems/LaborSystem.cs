using System;
using System.Collections.Generic;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Daily labour market: retirement, staffing targets, layoffs, job search (wage minus commute
    /// cost, so road changes shift who works where) and weekly wage adjustment.
    /// </summary>
    public sealed class LaborSystem : ISimulationSystem
    {
        public const float MaxCommuteHours = 1.5f;
        /// <summary>Yen a worker is willing to give up per hour of daily commute.</summary>
        public const double CommuteCostPerHour = 4000;
        public const int MaxApplicantsPerDay = 400;
        /// <summary>Each employed resident reconsiders their job once per this many days.</summary>
        public const int JobSearchPeriodDays = 30;
        /// <summary>A new job must be this much better (net of commute) to switch.</summary>
        public const double SwitchThreshold = 1.1;

        public string Name => "Labor";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w) => Run(w, MaxApplicantsPerDay);

        public static void Run(WorldState w, int maxApplicants)
        {
            EconomyState e = w.Economy;
            long today = w.Clock.TotalDays;

            foreach (Citizen c in e.Citizens.Values)
            {
                if (!c.Retired && c.AgeYears(today) >= 65)
                {
                    c.Retired = true;
                    e.Fire(c);
                }
            }

            bool weekly = today % 7 == 0;
            foreach (Company co in e.Companies.Values)
            {
                int cap = w.Effects.JobCapacity(co);
                if (co.Closed) co.TargetWorkers = 0;
                else if (co.IsPublic || co.PowerOutput > 0) co.TargetWorkers = cap;
                else if (weekly) co.TargetWorkers = DecideTarget(co, cap);
                co.TargetWorkers = Math.Min(co.TargetWorkers, cap);
                while (co.Workers.Count > co.TargetWorkers)
                {
                    int last = co.Workers[co.Workers.Count - 1];
                    if (e.Citizens.TryGetValue(last, out Citizen c)) e.Fire(c);
                    else co.Workers.RemoveAt(co.Workers.Count - 1);
                }
            }

            Match(w, maxApplicants);

            if (weekly) AdjustWages(w);
        }

        private static int DecideTarget(Company co, int cap)
        {
            int t = Math.Max(1, co.TargetWorkers);
            int step = Math.Max(1, t / 10);
            bool losing = co.ProfitAvg < 0;
            bool overstocked = false, understocked = false;
            if (co.Recipe != null && co.OutputCapacity > 0)
            {
                foreach (string res in co.Recipe.Outputs.Keys)
                {
                    double perDay = co.Recipe.Outputs[res] * Math.Max(1, co.Workers.Count);
                    double stock = co.Stock(res);
                    if (stock > perDay * 7) overstocked = true;
                    if (stock < perDay * 2) understocked = true;
                }
            }
            else if (co.IsRetail)
            {
                // Retailers staff to sales: grow while selling out, shrink while losing money.
                understocked = !losing;
            }
            if (co.InputShortage && losing) return Math.Max(1, t - step);
            if (overstocked && losing) return Math.Max(1, t - step);
            if (understocked && co.ProfitAvg >= 0) return Math.Min(cap, t + step);
            if (losing && co.Cash < co.Wage * co.Workers.Count * 7) return Math.Max(1, t - step);
            return t;
        }

        /// <summary>Unemployed working-age residents take the vacancy with the best wage net of commute.</summary>
        public static void Match(WorldState w, int maxApplicants)
        {
            EconomyState e = w.Economy;
            long today = w.Clock.TotalDays;
            var vacancies = new List<Company>();
            foreach (Company co in e.Companies.Values)
                if (!co.Closed && co.Workers.Count < co.TargetWorkers && w.Network.HasAccess(co.BuildingId)) vacancies.Add(co);

            int applicants = 0;
            foreach (Citizen c in e.Citizens.Values)
            {
                if (vacancies.Count == 0 || applicants >= maxApplicants) break;
                if (c.Retired || c.HomeId == 0 || !c.IsWorkingAge(today)) continue;
                double current = 0;
                if (c.JobId != 0)
                {
                    // A few employed people look around each day and switch for clearly better net pay.
                    if ((c.Id + today) % JobSearchPeriodDays != 0 || !e.Companies.TryGetValue(c.JobId, out Company job)) continue;
                    current = Utility(job.Wage, c, w.Network.TravelHours(c.HomeId, c.JobId)) * SwitchThreshold;
                }
                applicants++;
                Company best = null;
                double bestUtility = Math.Max(0, current);
                float bestHours = 0;
                foreach (Company co in vacancies)
                {
                    float h = w.Network.TravelHours(c.HomeId, co.BuildingId);
                    if (h > MaxCommuteHours) continue;
                    if (co.BuildingId == c.JobId) continue;
                    double u = Utility(co.Wage, c, h);
                    if (u > bestUtility) { bestUtility = u; best = co; bestHours = h; }
                }
                if (best == null) continue;
                e.Hire(best, c);
                c.CommuteHours = bestHours;
                if (best.Workers.Count >= best.TargetWorkers) vacancies.Remove(best);
            }
        }

        private static double Utility(long wage, Citizen c, float commuteHours) =>
            wage * (1 + 0.05 * c.Education) - commuteHours * 2 * CommuteCostPerHour;

        private static void AdjustWages(WorldState w)
        {
            EconomyState e = w.Economy;
            long today = w.Clock.TotalDays;
            double unemploymentRate = PopulationSystem.UnemploymentRate(w, today);
            foreach (Company co in e.Companies.Values)
            {
                if (co.Closed) continue;
                bool vacant = co.Workers.Count < co.TargetWorkers;
                co.VacancyDays = vacant ? co.VacancyDays + 7 : 0;
                if (co.VacancyDays >= 14 && (co.IsPublic || co.ProfitAvg >= 0))
                    co.Wage = Math.Min(EconomyState.BaseWage * 3, (long)(co.Wage * 1.03));
                else if (!vacant && (unemploymentRate > 0.15 || co.ProfitAvg < 0))
                    co.Wage = Math.Max(EconomyState.BaseWage * 7 / 10, (long)(co.Wage * 0.99));
            }
        }
    }
}
