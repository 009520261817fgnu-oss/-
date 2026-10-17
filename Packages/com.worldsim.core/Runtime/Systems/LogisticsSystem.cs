using System;
using System.Collections.Generic;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.Transport;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Business-to-business trade. Each buyer (a factory needing inputs, a shop needing stock) buys
    /// from the source with the lowest delivered cost: a local seller (market price + freight over
    /// the road network) or an import through the map-edge connection (premium price + longer haul).
    /// Surplus beyond a local reserve is exported. Freight fees go to local logistics companies
    /// (warehouses) when there are any, otherwise to outside carriers.
    /// </summary>
    public sealed class LogisticsSystem : ISimulationSystem
    {
        public const double ImportPremium = 1.25;
        public const double ExportDiscount = 0.85;
        public const double InputStockDays = 5;
        public const double RetailStockDays = 3;
        public const double SellerReserveDays = 2;
        /// <summary>Yen of cargo one truck carries; converts trade value into vehicle trips.</summary>
        public const double TruckValue = 300_000;

        public string Name => "Logistics";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EconomyState e = w.Economy;
            var sellers = new Dictionary<string, List<Company>>();
            foreach (Company co in e.Companies.Values)
            {
                if (co.Closed || co.Recipe == null) continue;
                foreach (string res in co.Recipe.Outputs.Keys)
                {
                    // Producers that also sell to residents (offices) keep a retail reserve, then sell the rest.
                    if (Available(co, res) <= 0) continue;
                    if (!sellers.TryGetValue(res, out List<Company> list)) sellers[res] = list = new List<Company>();
                    list.Add(co);
                }
            }

            double freightMult = Math.Max(0.1, 1 + w.Effects.Global("transportCost"));
            foreach (Company buyer in e.Companies.Values)
            {
                if (buyer.Closed) continue;
                foreach (var need in Needs(buyer))
                    Buy(w, buyer, need.Key, need.Value, sellers, freightMult);
            }

            Export(w, sellers, freightMult);
        }

        private static IEnumerable<KeyValuePair<string, double>> Needs(Company co)
        {
            if (co.Recipe != null)
            {
                int workers = Math.Max(1, co.Workers.Count);
                foreach (var kv in co.Recipe.Inputs)
                {
                    double need = kv.Value * workers * InputStockDays - co.Stock(kv.Key);
                    if (need > 0.5) yield return new KeyValuePair<string, double>(kv.Key, need);
                }
                foreach (var kv in co.Recipe.OptionalInputs)
                {
                    double need = kv.Value * workers * InputStockDays - co.Stock(kv.Key);
                    if (need > 0.5) yield return new KeyValuePair<string, double>(kv.Key, need);
                }
            }
            if (co.IsContractor && co.Backlog > 0)
            {
                // Materials for the work on the books, up to a few days ahead.
                double work = Math.Min(co.Backlog, EconomyState.WorkCapacity(co) * InputStockDays);
                foreach (var m in co.Def.ParamsWithPrefix("material"))
                {
                    double need = work / 1e6 * m.Value - co.Stock(m.Key);
                    if (need > 0.5) yield return new KeyValuePair<string, double>(m.Key, need);
                }
            }
            foreach (string res in co.Def.Sells)
            {
                if (co.Produces(res)) continue;
                double avg = co.SalesAvg.TryGetValue(res, out double a) ? a : 0;
                double need = Math.Max(30, avg * RetailStockDays) - co.Stock(res);
                if (need > 0.5) yield return new KeyValuePair<string, double>(res, need);
            }
        }

        private static void Buy(WorldState w, Company buyer, string res, double need,
            Dictionary<string, List<Company>> sellers, double freightMult)
        {
            EconomyState e = w.Economy;
            ResourceDef def = w.Content.GetResource(res);
            double price = e.Market.GetPrice(res);
            double rate = def.TransportRate * freightMult;

            var options = new List<(double delivered, Company seller, float hours)>();
            if (sellers.TryGetValue(res, out List<Company> list))
            {
                foreach (Company s in list)
                {
                    if (s == buyer || Available(s, res) <= 0) continue;
                    float h = w.Network.TravelHours(s.BuildingId, buyer.BuildingId);
                    if (float.IsPositiveInfinity(h)) continue;
                    options.Add((price + rate * h, s, h));
                }
                options.Sort((a, b) => a.delivered != b.delivered ? a.delivered.CompareTo(b.delivered) : a.seller.BuildingId.CompareTo(b.seller.BuildingId));
            }
            float outsideHours = w.Network.HoursToOutside(buyer.BuildingId);
            double importDelivered = float.IsPositiveInfinity(outsideHours)
                ? double.PositiveInfinity
                : def.BasePrice * ImportPremium + rate * outsideHours;

            foreach (var opt in options)
            {
                if (need <= 0.5) break;
                if (opt.delivered > importDelivered) break;
                double qty = Math.Min(need, Available(opt.seller, res));
                qty = Math.Min(qty, Math.Floor(buyer.Cash / opt.delivered));
                if (qty <= 0) break;
                long goodsCost = (long)Math.Round(qty * price);
                long freight = (long)Math.Round(qty * rate * opt.hours);
                e.Ledger.Transfer(buyer, opt.seller, goodsCost, Flow.B2BSales);
                PayFreight(w, buyer, freight);
                opt.seller.RevenueToday += goodsCost;
                buyer.CostToday += goodsCost + freight;
                opt.seller.Inventory[res] = opt.seller.Stock(res) - qty;
                buyer.AddStock(res, qty);
                need -= qty;
                Market.Add(e.Market.LocalTraded, res, qty);
                w.Trips.Add(new Trip(opt.seller.BuildingId, buyer.BuildingId, Trucks(goodsCost), true));
            }

            if (need > 0.5 && !double.IsPositiveInfinity(importDelivered))
            {
                double qty = Math.Min(need, Math.Floor(buyer.Cash / importDelivered));
                if (qty > 0)
                {
                    long goodsCost = (long)Math.Round(qty * def.BasePrice * ImportPremium);
                    long freight = (long)Math.Round(qty * rate * outsideHours);
                    e.Ledger.Transfer(buyer, e.External, goodsCost, Flow.Imports);
                    PayFreight(w, buyer, freight);
                    buyer.CostToday += goodsCost + freight;
                    buyer.AddStock(res, qty);
                    need -= qty;
                    Market.Add(e.Market.Imported, res, qty);
                    w.Trips.Add(new Trip(buyer.BuildingId, 0, Trucks(goodsCost), true));
                }
            }
            if (need > 0.5) Market.Add(e.Market.Unmet, res, need);
        }

        private static void Export(WorldState w, Dictionary<string, List<Company>> sellers, double freightMult)
        {
            EconomyState e = w.Economy;
            foreach (var kv in sellers)
            {
                ResourceDef def = w.Content.GetResource(kv.Key);
                foreach (Company s in kv.Value)
                {
                    double perDay = s.Recipe.Outputs[kv.Key] * Math.Max(1, s.Workers.Count);
                    double qty = Available(s, kv.Key) - perDay * SellerReserveDays;
                    if (qty <= 0) continue;
                    float hours = w.Network.HoursToOutside(s.BuildingId);
                    if (float.IsPositiveInfinity(hours)) continue;
                    double net = def.BasePrice * ExportDiscount - def.TransportRate * freightMult * hours;
                    if (net <= 0) continue;
                    long revenue = (long)Math.Round(qty * net);
                    e.Ledger.Transfer(e.External, s, revenue, Flow.Exports);
                    s.RevenueToday += revenue;
                    s.Inventory[kv.Key] = s.Stock(kv.Key) - qty;
                    Market.Add(e.Market.Exported, kv.Key, qty);
                    w.Trips.Add(new Trip(s.BuildingId, 0, Trucks(revenue), true));
                }
            }
        }

        /// <summary>Stock a company can sell to other companies: all of it, minus a few days of retail sales if it also sells to residents.</summary>
        private static double Available(Company co, string res)
        {
            double stock = co.Stock(res);
            if (!co.Sells(res)) return stock;
            double daily = co.SalesAvg.TryGetValue(res, out double a) ? a : 0;
            return stock - Math.Max(30, daily * RetailStockDays);
        }

        private static void PayFreight(WorldState w, Company payer, long amount)
        {
            if (amount <= 0) return;
            EconomyState e = w.Economy;
            int totalWorkers = 0;
            foreach (Company c in e.Companies.Values)
                if (c.IsLogistics && !c.Closed) totalWorkers += c.Workers.Count;
            if (totalWorkers == 0)
            {
                e.Ledger.Transfer(payer, e.External, amount, Flow.Freight);
                return;
            }
            long remaining = amount;
            Company lastCarrier = null;
            foreach (Company c in e.Companies.Values)
            {
                if (!c.IsLogistics || c.Closed || c.Workers.Count == 0) continue;
                long share = amount * c.Workers.Count / totalWorkers;
                e.Ledger.Transfer(payer, c, share, Flow.Freight);
                c.RevenueToday += share;
                remaining -= share;
                lastCarrier = c;
            }
            if (remaining > 0 && lastCarrier != null)
            {
                e.Ledger.Transfer(payer, lastCarrier, remaining, Flow.Freight);
                lastCarrier.RevenueToday += remaining;
            }
        }

        private static float Trucks(long value) => (float)Math.Max(1, value / TruckValue);
    }
}
