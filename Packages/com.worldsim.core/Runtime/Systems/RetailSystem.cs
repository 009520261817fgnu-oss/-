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

        // Per home: for each resource, the reachable shops selling it, nearest first. Filtering by resource
        // here (not per purchase) keeps the order and the outcome identical while scanning far fewer shops.
        private readonly Dictionary<int, Dictionary<string, int[]>> _nearbyRetail = new Dictionary<int, Dictionary<string, int[]>>();
        private int _cacheVersion = -1;
        private readonly List<int> _retailIds = new List<int>();
        private float[] _hours;
        private long[] _keys;

        public string Name => "Retail";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EconomyState e = w.Economy;
            if (_cacheVersion != w.Network.Version)
            {
                _nearbyRetail.Clear();
                _retailIds.Clear();
                _cacheVersion = w.Network.Version;
            }
            long today = w.Clock.TotalDays;
            var shopVisits = new SortedDictionary<long, int>();

            foreach (Citizen c in e.Citizens.Values)
            {
                if (c.HomeId == 0 || c.AgeYears(today) < 18) continue;
                if (c.Id == w.Player.CitizenId) continue; // the player shops in person
                Dictionary<string, int[]> shops = NearbyRetail(w, c.HomeId);
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
        private static double Purchase(WorldState w, Citizen c, Dictionary<string, int[]> shopsByRes, string res, double wanted, double cap,
            ref double spent, SortedDictionary<long, int> visits)
        {
            EconomyState e = w.Economy;
            double basePrice = e.Market.GetPrice(res);
            double bought = 0;
            bool first = true;
            int[] shops = shopsByRes.TryGetValue(res, out int[] l) ? l : Array.Empty<int>();
            foreach (int id in shops)
            {
                if (bought >= wanted - 1e-9) break;
                if (!e.Companies.TryGetValue(id, out Company shop) || shop.Closed || shop.Workers.Count == 0) continue;
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
        private Dictionary<string, int[]> NearbyRetail(WorldState w, int homeId)
        {
            if (_nearbyRetail.TryGetValue(homeId, out Dictionary<string, int[]> byRes)) return byRes;
            if (_retailIds.Count == 0)
            {
                foreach (Company co in w.Economy.Companies.Values) if (co.IsRetail) _retailIds.Add(co.BuildingId);
                _hours = new float[_retailIds.Count];
                _keys = new long[_retailIds.Count];
            }
            w.Network.TravelHoursMany(homeId, _retailIds, _hours);
            // Nearest first, ties by id: non-negative floats order like their bit patterns.
            int found = 0;
            for (int i = 0; i < _retailIds.Count; i++)
                if (_hours[i] <= MaxShoppingHours)
                    _keys[found++] = ((long)BitConverter.SingleToInt32Bits(_hours[i]) << 32) | (uint)_retailIds[i];
            Array.Sort(_keys, 0, found);
            var lists = new Dictionary<string, List<int>>();
            for (int i = 0; i < found; i++)
            {
                int id = (int)(_keys[i] & 0xFFFFFFFF);
                foreach (string res in w.Economy.Companies[id].Def.Sells)
                {
                    if (!lists.TryGetValue(res, out List<int> l)) lists[res] = l = new List<int>();
                    l.Add(id);
                }
            }
            byRes = new Dictionary<string, int[]>();
            foreach (var kv in lists) byRes[kv.Key] = kv.Value.ToArray();
            _nearbyRetail[homeId] = byRes;
            return byRes;
        }
    }
}
