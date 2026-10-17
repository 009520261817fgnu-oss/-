using System;
using System.Collections.Generic;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Daily settlement: payroll with income tax, pensions, industry subsidies, corporate tax on
    /// profit, public maintenance, preparedness spending, and company failure / restart.
    /// </summary>
    public sealed class FinanceSystem : ISimulationSystem
    {
        public const int DistressDaysToClose = 14;
        public const int ReopenCheckDays = 30;
        public const long RoadMaintenancePerCell = 100;
        public const int MaxAbsenceDays = 3;
        /// <summary>Share of excess cash paid out per day = 1 / DividendDivisor.</summary>
        public const long DividendDivisor = 20;

        public string Name => "Finance";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EconomyState e = w.Economy;
            Policy p = e.City.Policy;
            Ledger l = e.Ledger;

            foreach (Citizen c in e.Citizens.Values) c.LastNetIncome = 0;
            Citizen player = w.Player.IsActive && e.Citizens.TryGetValue(w.Player.CitizenId, out Citizen pc) ? pc : null;
            bool workday = People.DailySchedule.IsWorkday(w.Clock.TotalDays);

            foreach (Company co in e.Companies.Values)
            {
                if (co.Closed) continue;
                Account payer = co.IsPublic ? (Account)e.City : co;
                bool shortfall = false;
                foreach (int id in co.Workers)
                {
                    if (!e.Citizens.TryGetValue(id, out Citizen c)) continue;
                    // Promotions raise pay for everyone; the player is paid for the hours actually worked.
                    double share = c == player && workday ? Math.Min(1.0, w.Player.WorkedHours / 8.0) : 1.0;
                    long due = (long)Math.Round(co.Wage * c.WageFactor * share);
                    long paid = l.TransferUpTo(payer, c, due, Flow.Wages);
                    if (paid < due) shortfall = true;
                    if (c != player || !workday || w.Player.WorkedHours >= 4) c.TenureDays++;
                    if (!co.IsPublic) co.CostToday += paid;
                    long tax = (long)Math.Round(paid * p.IncomeTax);
                    l.Transfer(c, e.City, Math.Min(tax, c.Cash), Flow.IncomeTax);
                    c.LastNetIncome = paid - tax;
                }
                if (co.IsPublic) continue;

                BuildingCategory cat = co.Def.Category;
                if (p.IndustrySubsidyPerWorker > 0 && (cat == BuildingCategory.Industrial || cat == BuildingCategory.Agriculture))
                {
                    long sub = l.TransferUpTo(e.City, co, p.IndustrySubsidyPerWorker * co.Workers.Count, Flow.Subsidy);
                    co.RevenueToday += sub;
                }
                co.DistressDays = shortfall ? co.DistressDays + 1 : 0;
            }

            if (player != null) SettleAttendance(w, player, workday);

            long today = w.Clock.TotalDays;
            foreach (Citizen c in e.Citizens.Values)
            {
                if (c.Retired) c.LastNetIncome += l.TransferUpTo(e.City, c, p.PensionPerDay, Flow.Pension);
                else if (c.JobId == 0 && c.HomeId != 0 && c.IsWorkingAge(today))
                    c.LastNetIncome += l.TransferUpTo(e.City, c, p.UnemploymentBenefit, Flow.Benefit);
            }
            HousingMarket.CollectRent(w);

            long publicMaintenance = 0;
            foreach (Company co in e.Companies.Values)
            {
                if (co.IsPublic)
                {
                    publicMaintenance += (long)co.Def.GetParam("maintenance");
                    continue;
                }
                long profit = co.RevenueToday - co.CostToday;
                if (profit > 0)
                {
                    long tax = (long)Math.Round(profit * p.CorporateTax);
                    l.TransferUpTo(co, e.City, tax, Flow.CorporateTax);
                }
                co.LastProfit = profit;
                co.LastCost = co.CostToday;
                co.ProfitAvg = co.ProfitAvg * 0.9 + profit * 0.1;
                co.RevenueToday = 0;
                co.CostToday = 0;

                if (!co.Closed && co.DistressDays >= DistressDaysToClose) Close(w, co);
                else if (co.Closed && ++co.ClosedDays % ReopenCheckDays == 0) TryReopen(w, co);
            }

            PayDividends(w);
            // Upkeep and preparedness works are contracted after the books are closed, so the
            // contractors' revenue counts towards tomorrow's profit like any other sale.
            e.PayForWork(e.City, publicMaintenance + w.RoadCellCount * RoadMaintenancePerCell, Flow.Maintenance);
            if (p.PreparednessBudget > 0)
            {
                long spent = Math.Min(p.PreparednessBudget, Math.Max(0, e.City.Cash));
                e.PayForWork(e.City, spent, Flow.Preparedness);
                e.City.PreparednessStock += spent;
            }
            e.City.PreparednessStock *= 0.999; // slow depreciation
        }

        /// <summary>
        /// Companies distribute part of their cash above an operating reserve to local adult residents
        /// (the shareholders), so profits return to household income and spending.
        /// </summary>
        private static void PayDividends(WorldState w)
        {
            EconomyState e = w.Economy;
            long today = w.Clock.TotalDays;
            long pool = 0;
            foreach (Company co in e.Companies.Values)
            {
                if (co.IsPublic || co.Closed) continue;
                long reserve = EconomyState.CompanyStartCapital + 30L * co.Wage * Math.Max(1, co.Workers.Count);
                if (co.Cash <= reserve) continue;
                long payout = (co.Cash - reserve) / DividendDivisor;
                e.Ledger.Transfer(co, e.City, payout, Flow.Dividends); // held briefly for distribution
                pool += payout;
            }
            if (pool <= 0) return;
            var adults = new List<Citizen>();
            foreach (Citizen c in e.Citizens.Values) if (c.HomeId != 0 && c.AgeYears(today) >= 18) adults.Add(c);
            if (adults.Count == 0) { e.Ledger.Transfer(e.City, e.External, pool, Flow.Dividends); return; }
            long each = pool / adults.Count;
            foreach (Citizen c in adults)
            {
                e.Ledger.Transfer(e.City, c, each, Flow.Dividends);
                c.LastNetIncome += each;
            }
            e.Ledger.Transfer(e.City, e.External, pool - each * adults.Count, Flow.Dividends);
        }

        /// <summary>Missing work several workdays in a row gets the player fired, like any employer would.</summary>
        private static void SettleAttendance(WorldState w, Citizen player, bool workday)
        {
            People.PlayerState p = w.Player;
            if (player.JobId != 0 && workday)
            {
                if (p.WorkedHours < 4) p.AbsenceDays++;
                else p.AbsenceDays = 0;
                if (p.AbsenceDays >= MaxAbsenceDays && w.Economy.Companies.TryGetValue(player.JobId, out Company co))
                {
                    w.Economy.Fire(player, Cause.Absence);
                    p.AbsenceDays = 0;
                    w.Log("人生", $"{People.Names.Of(player.Id)}は無断欠勤が続き、{co.Def.Name}を解雇されました");
                }
            }
            p.WorkedHours = 0;
        }

        private static void Close(WorldState w, Company co)
        {
            EconomyState e = w.Economy;
            foreach (int id in co.Workers.ToArray())
                if (e.Citizens.TryGetValue(id, out Citizen c)) e.Fire(c, Cause.Bankruptcy);
            e.Causes.Note(0, Cause.Bankruptcy, co.BuildingId);
            e.Causes.Book(CauseMetric.CompaniesClosed, (int)Cause.Bankruptcy, 1);
            co.Closed = true;
            co.ClosedDays = 0;
            co.TargetWorkers = 0;
            co.Inventory.Clear();
            e.Ledger.Transfer(co, e.External, co.Cash, Flow.Divestment);
            w.Log("倒産", $"{co.Def.Name} #{co.BuildingId} が資金不足で倒産しました");
        }

        /// <summary>A closed business restarts with outside investment when its market looks healthy.</summary>
        private static void TryReopen(WorldState w, Company co)
        {
            EconomyState e = w.Economy;
            bool healthy = true;
            if (co.Recipe != null)
                foreach (var kv in co.Recipe.Outputs)
                {
                    ResourceDef r = w.Content.GetResource(kv.Key);
                    if (e.Market.GetPrice(kv.Key) < r.BasePrice * 0.9) healthy = false;
                }
            if (!healthy || !w.Network.HasAccess(co.BuildingId)) return;
            co.Closed = false;
            co.DistressDays = 0;
            co.ProfitAvg = 0;
            co.Wage = EconomyState.BaseWage;
            co.TargetWorkers = Math.Max(1, co.Def.Jobs / 2);
            e.Ledger.Transfer(e.External, co, EconomyState.CompanyStartCapital, Flow.Investment);
            e.Causes.Note(0, Cause.Reopened, co.BuildingId);
            e.Causes.Book(CauseMetric.CompaniesReopened, (int)Cause.Reopened, 1);
            w.Log("再開", $"{co.Def.Name} #{co.BuildingId} が新たな出資を受けて営業を再開しました");
        }
    }
}
