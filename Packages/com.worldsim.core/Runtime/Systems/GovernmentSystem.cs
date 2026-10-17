using System;
using System.Collections.Generic;
using System.Linq;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Optional city government (S3, an autonomous actor; off by default so the player governs). Once
    /// a month it reads the city's situation — financial reserve in days of spending, unemployment,
    /// rent burden, unrepaired damage — and adjusts at most two policies by small steps through the
    /// same policy rules the player uses. Each decision is logged with the observation behind it.
    /// </summary>
    public sealed class GovernmentSystem : ISimulationSystem
    {
        public const int PeriodDays = 30;
        public const int MaxChangesPerPeriod = 2;

        public string Name => "Government";
        public int IntervalTicks => SimClock.TicksPerDay;

        public static bool Active(WorldState w)
        {
            long mode = w.Economy.City.Policy.AutoGovernment;
            return mode == 1 || (mode == 2 && w.Player.IsActive);
        }

        public void Tick(WorldState w)
        {
            long today = w.Clock.TotalDays;
            if (!Active(w) || today % PeriodDays != 5 || w.History.Count < PeriodDays) return;
            foreach (var (key, value, reason) in Decide(w).Take(MaxChangesPerPeriod))
            {
                double before = w.Economy.City.Policy.Get(key);
                if (w.Economy.City.Policy.Set(key, value) != null || before == w.Economy.City.Policy.Get(key)) continue;
                w.Economy.Causes.Note(0, Cause.GovernmentDecision, 0);
                w.Log("市政", $"市が{Label(key)}を{Format(key, before)}→{Format(key, value)}に変更：{reason}");
            }
        }

        /// <summary>Days the treasury would last at the last month's average spending if all income stopped (∞ when nothing is spent).</summary>
        public static double ReserveDays(WorldState w)
        {
            long today = w.Clock.TotalDays;
            var flows = w.Economy.Causes.Sum(CauseMetric.CityMoney, today - PeriodDays + 1, today);
            double spending = -flows.Values.Where(v => v < 0).Sum() / (double)PeriodDays;
            return spending <= 0 ? double.PositiveInfinity : w.Economy.City.Cash / spending;
        }

        /// <summary>The government's candidate decisions, most pressing first (key, new value, reason).</summary>
        public static List<(string key, double value, string reason)> Decide(WorldState w)
        {
            Policy p = w.Economy.City.Policy;
            long today = w.Clock.TotalDays;
            var list = new List<(string, double, string)>();
            double reserve = ReserveDays(w);
            double unemployment = PopulationSystem.UnemploymentRate(w, today);
            var adults = w.Economy.Citizens.Values.Where(c => c.LastRent > 0).ToList();
            double burden = adults.Count == 0 ? 0 : adults.Average(HousingMarket.RentBurden);
            int damaged = w.Buildings.Count(b => b.Condition < 1f);
            string R(double d) => double.IsPositiveInfinity(d) ? "黒字" : $"{d:0}日分";

            // Deficit: restore finances first (cut subsidies before raising taxes).
            if (reserve < 120)
            {
                if (p.IndustrySubsidyPerWorker > 0) list.Add(("industrySubsidy", Math.Max(0, p.IndustrySubsidyPerWorker - 2000), $"財政の余力が{R(reserve)}しかない"));
                else list.Add(("incomeTax", Math.Min(0.4, p.IncomeTax + 0.01), $"財政の余力が{R(reserve)}しかない"));
            }
            if (damaged > 0 && p.RepairBudget < 20_000_000 && reserve > 180)
                list.Add(("repairBudget", Math.Min(20_000_000, p.RepairBudget + 5_000_000), $"被災した建物が{damaged}棟ある"));
            if (unemployment > 0.08 && reserve > 365)
                list.Add(("industrySubsidy", Math.Min(10_000, p.IndustrySubsidyPerWorker + 1000), $"失業率が{unemployment * 100:0}%で、財政に余裕がある"));
            if (unemployment < 0.05 && p.IndustrySubsidyPerWorker > 0)
                list.Add(("industrySubsidy", Math.Max(0, p.IndustrySubsidyPerWorker - 1000), $"失業率が{unemployment * 100:0}%まで下がった"));
            if (burden > 0.35)
                list.Add(("rent", Math.Round(p.RentPerDay * 0.9), $"家賃が所得の{burden * 100:0}%を占めている"));
            if (reserve > 720 && p.IncomeTax > 0.05)
                list.Add(("incomeTax", Math.Max(0.05, p.IncomeTax - 0.01), $"財政の余力が{R(reserve)}あり、黒字が続いている"));
            return list;
        }

        private static string Label(string key)
        {
            switch (key)
            {
                case "incomeTax": return "所得税率";
                case "industrySubsidy": return "産業補助";
                case "repairBudget": return "復旧予算";
                case "rent": return "家賃";
                default: return key;
            }
        }

        private static string Format(string key, double v) => key == "incomeTax" ? $"{v * 100:0}%" : $"{v:N0}円";
    }
}
