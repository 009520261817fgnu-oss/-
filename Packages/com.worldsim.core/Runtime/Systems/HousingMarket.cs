using System;
using System.Collections.Generic;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Rent and land value (S2). Land value (shops and jobs within reach, minus pollution and damage)
    /// sets each home's rent; residents weigh rent against commute and air when choosing a home, so
    /// poorer households end up where housing is cheap. Land values are recomputed once a week
    /// (they change slowly, and the computation visits every building).
    /// </summary>
    public static class HousingMarket
    {
        /// <summary>Days of unpaid rent before a resident is evicted.</summary>
        public const int EvictionDays = 30;

        /// <summary>Land value index of a building (0..200, about 100 in an ordinary street).</summary>
        public static double LandValue(WorldState w, int buildingId)
        {
            long week = w.Clock.TotalDays / 7;
            if (w.LandValueWeek != week)
            {
                w.LandValueIndex.Clear();
                foreach (var kv in CityAnalytics.LandValues(w)) w.LandValueIndex[kv.Key] = kv.Value;
                w.LandValueWeek = week;
            }
            return w.LandValueIndex.TryGetValue(buildingId, out double v) ? v : 0;
        }

        /// <summary>Rent per adult per day: the policy level scaled by land value (×0.5 at 0, ×1.5 at 200).</summary>
        public static long RentFor(WorldState w, int homeId) =>
            (long)Math.Round(w.Economy.City.Policy.RentPerDay * (0.5 + LandValue(w, homeId) / 200.0));

        /// <summary>Collects rent after wages and benefits; long arrears lead to eviction.</summary>
        public static void CollectRent(WorldState w)
        {
            EconomyState e = w.Economy;
            long today = w.Clock.TotalDays;
            foreach (Citizen c in e.Citizens.Values)
            {
                c.LastRent = 0;
                if (c.HomeId == 0 || c.AgeYears(today) < 18) { c.RentArrearsDays = 0; continue; }
                long due = RentFor(w, c.HomeId);
                long paid = e.Ledger.TransferUpTo(c, e.City, due, Flow.Rent);
                c.LastRent = paid;
                c.LastNetIncome -= paid;
                c.RentArrearsDays = paid < due ? c.RentArrearsDays + 1 : 0;
                if (c.RentArrearsDays >= EvictionDays)
                {
                    e.Causes.Note(c.Id, Cause.Evicted, c.HomeId);
                    if (c.Id == w.Player.CitizenId) w.Log("人生", $"{People.Names.Of(c.Id)}は家賃を払えず、住まいを失いました");
                    e.MoveIn(c, 0);
                    c.RentArrearsDays = 0;
                }
            }
        }

        /// <summary>Share of income going to rent, 0 when there is no income.</summary>
        public static double RentBurden(Citizen c)
        {
            long gross = c.LastNetIncome + c.LastRent;
            return gross <= 0 ? (c.LastRent > 0 ? 1 : 0) : (double)c.LastRent / gross;
        }

        /// <summary>
        /// How much a resident minds rent when choosing a home: low incomes weigh it heavily, high
        /// incomes care more about location (land value).
        /// </summary>
        public static float RentWeight(Citizen c)
        {
            long income = c.LastNetIncome + c.LastRent;
            return income < 5000 ? 2.0f : income < 10000 ? 1.0f : 0.4f;
        }

        /// <summary>How much a resident values a good location (per 100 land value): more with higher income.</summary>
        public static float LocationWeight(Citizen c)
        {
            long income = c.LastNetIncome + c.LastRent;
            return income < 5000 ? 0.5f : income < 10000 ? 1.0f : 1.5f;
        }

        /// <summary>
        /// Whether a resident can take on this rent: covered by a month of savings or by income. Those
        /// who cannot pay anywhere stay homeless and eventually leave town.
        /// </summary>
        public static bool CanAfford(WorldState w, Citizen c, int homeId)
        {
            long rent = RentFor(w, homeId);
            return rent == 0 || c.Cash >= rent * 30 || c.LastNetIncome + c.LastRent >= rent;
        }
    }
}
