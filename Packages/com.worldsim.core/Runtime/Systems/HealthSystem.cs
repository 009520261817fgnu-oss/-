using System;
using System.Collections.Generic;
using WorldSim.Core.Economy;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Residents' health (S2). Polluted air at home and at work, old age and hunger wear it down;
    /// rest restores it slowly and clinics (within reach, up to their staffed capacity) restore it
    /// quickly. Health raises or lowers mortality, workers' productivity and happiness, which closes
    /// the loop industry → pollution → health → labour and population.
    /// </summary>
    public static class HealthSystem
    {
        public const float Initial = 0.9f;
        public const double NaturalRecovery = 0.0015, ClinicRecovery = 0.01;
        public const double PollutionDamage = 0.006; // per day at pollution 100
        public const float CareThreshold = 0.8f;
        public const float ClinicReachHours = 0.5f;

        public static void Update(WorldState w, long today)
        {
            EconomyState e = w.Economy;
            var clinics = new List<(Company co, double left)>();
            foreach (Company co in e.Companies.Values)
            {
                double patients = co.Def.GetParam("patients");
                if (patients <= 0 || co.Closed || co.TargetWorkers <= 0 || !w.TryGetBuilding(co.BuildingId, out Building cb) || cb.Condition < 0.25f) continue;
                clinics.Add((co, patients * co.Workers.Count / Math.Max(1.0, co.Def.Jobs)));
            }
            foreach (Citizen c in e.Citizens.Values)
            {
                double h = c.Health;
                double air = 0;
                if (c.HomeId != 0 && w.TryGetBuilding(c.HomeId, out Building home)) air += w.Environment.PollutionAt(home.Origin) * 0.6;
                if (c.JobId != 0 && w.TryGetBuilding(c.JobId, out Building job)) air += w.Environment.PollutionAt(job.Origin) * 0.4;
                h -= air / 100.0 * PollutionDamage;
                int age = c.AgeYears(today);
                if (age > 55) h -= (age - 55) * 0.00004;
                if (age >= 18 && c.FoodMet < 0.5f) h -= 0.002 * (1 - c.FoodMet);
                h += NaturalRecovery;
                if (h < CareThreshold && c.HomeId != 0)
                {
                    for (int i = 0; i < clinics.Count; i++)
                    {
                        if (clinics[i].left < 1 || w.Network.TravelHours(c.HomeId, clinics[i].co.BuildingId) > ClinicReachHours) continue;
                        clinics[i] = (clinics[i].co, clinics[i].left - 1);
                        h += ClinicRecovery;
                        break;
                    }
                }
                c.Health = (float)Math.Max(0, Math.Min(1, h));
            }
        }

        /// <summary>Multiplier on the age-based death rate: 1 when healthy, up to 4 when very ill.</summary>
        public static double MortalityFactor(Citizen c) => 1 + 3 * (1 - c.Health);

        /// <summary>Workers' average health as a productivity factor (≈1 at normal health, 0.5 when all are very ill).</summary>
        public static double ProductivityFactor(WorldState w, Company co)
        {
            if (co.Workers.Count == 0) return 1;
            double sum = 0;
            foreach (int id in co.Workers)
                if (w.Economy.Citizens.TryGetValue(id, out Citizen c)) sum += c.Health;
            return Math.Min(1.0, 0.5 + 0.55 * sum / co.Workers.Count);
        }
    }
}
