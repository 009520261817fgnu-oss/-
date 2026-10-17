using System;
using System.Collections.Generic;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.Transport;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Household consumption. Each adult spends from yesterday's income (plus a little savings):
    /// food first, then services and goods, at the nearest reachable retailer with stock.
    /// Spending therefore follows income, which follows employment and wages (scenario A).
    /// </summary>
    public sealed class RetailSystem : ISimulationSystem
    {
        public const double FoodPerDay = 2.5;
        public const double MaxGoodsPerDay = 1.5;
        public const double MaxServicesPerDay = 6;
        public const float MaxShoppingHours = 1.0f;
        public const double SpendShareOfIncome = 0.85;
        public const double SpendShareOfSavings = 0.01;

        private readonly Dictionary<int, List<int>> _nearbyRetail = new Dictionary<int, List<int>>();
        private int _cacheVersion = -1;

        public string Name => "Retail";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EconomyState e = w.Economy;
            if (_cacheVersion != w.Network.Version)
            {
                _nearbyRetail.Clear();
                _cacheVersion = w.Network.Version;
            }
            long today = w.Clock.TotalDays;
            var shopVisits = new SortedDictionary<long, int>();

            foreach (Citizen c in e.Citizens.Values)
            {
                if (c.HomeId == 0 || c.AgeYears(today) < 18) continue;
                if (c.Id == w.Player.CitizenId) continue; // the player shops in person
                List<int> shops = NearbyRetail(w, c.HomeId);
                double budget = Math.Min(c.Cash, SpendShareOfIncome * c.LastNetIncome + SpendShareOfSavings * c.Cash);
                double spent = 0;

                double food = Purchase(w, c, shops, "food", FoodPerDay, budget, ref spent, shopVisits);
                c.FoodMet = (float)(food / FoodPerDay);
                double left = budget - spent;
                double services = Purchase(w, c, shops, "services", MaxServicesPerDay, spent + left * 0.3, ref spent, shopVisits);
                c.ServicesMet = (float)(services / MaxServicesPerDay);
                double goods = Purchase(w, c, shops, "goods", MaxGoodsPerDay, budget, ref spent, shopVisits);
                c.GoodsMet = (float)(goods / MaxGoodsPerDay);
            }

            foreach (var kv in shopVisits)
                w.Trips.Add(new Trip((int)(kv.Key >> 32), (int)(kv.Key & 0xFFFFFFFF), kv.Value * 0.6f, false));
        }

        /// <summary>Buys up to <paramref name="wanted"/> units while total spending stays within <paramref name="cap"/>.</summary>
        private static double Purchase(WorldState w, Citizen c, List<int> shops, string res, double wanted, double cap,
            ref double spent, SortedDictionary<long, int> visits)
        {
            EconomyState e = w.Economy;
            double basePrice = e.Market.GetPrice(res);
            double bought = 0;
            bool first = true;
            foreach (int id in shops)
            {
                if (bought >= wanted - 1e-9) break;
                if (!e.Companies.TryGetValue(id, out Company shop) || shop.Closed || !shop.Sells(res) || shop.Workers.Count == 0) continue;
                if (first)
                {
                    // Demand is recorded at the preferred shop so it restocks even after selling out.
                    Market.Add(shop.SalesToday, res, wanted - bought);
                    first = false;
                }
                double stock = shop.Stock(res);
                if (stock <= 0) continue;
                double price = basePrice * shop.Def.GetParam("markup", 1.0);
                double affordable = Math.Floor((cap - spent) / price * 100) / 100;
                double qty = Math.Min(Math.Min(wanted - bought, stock), affordable);
                if (qty <= 0) break;
                long cost = (long)Math.Round(qty * price);
                if (cost > c.Cash) break;
                e.Ledger.Transfer(c, shop, cost, Flow.Retail);
                shop.RevenueToday += cost;
                shop.Inventory[res] = stock - qty;
                bought += qty;
                spent += cost;
                long key = ((long)c.HomeId << 32) | (uint)id;
                visits[key] = visits.TryGetValue(key, out int v) ? v + 1 : 1;
            }
            if (bought < wanted) Market.Add(e.Market.Unmet, res, wanted - bought);
            return bought;
        }

        /// <summary>Retailers reachable from a home within the shopping radius, nearest first.</summary>
        private List<int> NearbyRetail(WorldState w, int homeId)
        {
            if (_nearbyRetail.TryGetValue(homeId, out List<int> list)) return list;
            var found = new List<(float h, int id)>();
            foreach (Company co in w.Economy.Companies.Values)
            {
                if (!co.IsRetail) continue;
                float h = w.Network.TravelHours(homeId, co.BuildingId);
                if (h <= MaxShoppingHours) found.Add((h, co.BuildingId));
            }
            found.Sort((a, b) => a.h != b.h ? a.h.CompareTo(b.h) : a.id.CompareTo(b.id));
            list = new List<int>(found.Count);
            foreach (var f in found) list.Add(f.id);
            _nearbyRetail[homeId] = list;
            return list;
        }
    }
}
