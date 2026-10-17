using System;
using System.Collections.Generic;
using WorldSim.Core.Content;

namespace WorldSim.Core.Economy
{
    /// <summary>City-wide policy levers (spec 3.3 政治). Changed through SetPolicyCommand.</summary>
    public sealed class Policy
    {
        public double IncomeTax = 0.20;
        public double CorporateTax = 0.30;
        /// <summary>Yen per industrial/agricultural worker per day paid to companies.</summary>
        public long IndustrySubsidyPerWorker;
        /// <summary>Yen per day invested in disaster preparedness (levees, drills, retrofits).</summary>
        public long PreparednessBudget;
        /// <summary>Maximum yen per day spent on repairing damage.</summary>
        public long RepairBudget = 5_000_000;
        public long PensionPerDay = 1500;
        /// <summary>Yen per day paid to unemployed working-age residents.</summary>
        public long UnemploymentBenefit = 2500;
        /// <summary>Yen per kWh paid by consumers to power companies.</summary>
        public double ElectricityPrice = 25;
        /// <summary>
        /// Rent per adult resident per day at an average location (S2). The city owns the housing
        /// stock (municipal housing, an assumption of this stage); rent scales with land value.
        /// </summary>
        public long RentPerDay = 300;
        /// <summary>1: outside investors may open businesses where demand is unmet (S3). 0: only the player builds.</summary>
        public long PrivateInvestment = 1;
        /// <summary>City government AI (S3): 0 off (the player governs), 1 always, 2 only while a resident is being lived as.</summary>
        public long AutoGovernment;

        public static readonly string[] Keys =
            { "incomeTax", "corporateTax", "industrySubsidy", "preparedness", "repairBudget", "pension", "unemploymentBenefit", "electricityPrice", "rent", "privateInvestment", "autoGovernment" };

        public double Get(string key)
        {
            switch (key)
            {
                case "incomeTax": return IncomeTax;
                case "corporateTax": return CorporateTax;
                case "industrySubsidy": return IndustrySubsidyPerWorker;
                case "preparedness": return PreparednessBudget;
                case "repairBudget": return RepairBudget;
                case "pension": return PensionPerDay;
                case "unemploymentBenefit": return UnemploymentBenefit;
                case "electricityPrice": return ElectricityPrice;
                case "rent": return RentPerDay;
                case "privateInvestment": return PrivateInvestment;
                case "autoGovernment": return AutoGovernment;
                default: throw new ArgumentException($"Unknown policy '{key}'");
            }
        }

        /// <summary>Returns null on success, or an error message when out of range.</summary>
        public string Set(string key, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "値が不正です";
            switch (key)
            {
                case "incomeTax": if (v < 0 || v > 0.5) return "所得税率は0〜50%"; IncomeTax = v; return null;
                case "corporateTax": if (v < 0 || v > 0.6) return "法人税率は0〜60%"; CorporateTax = v; return null;
                case "industrySubsidy": if (v < 0 || v > 20000) return "産業補助は0〜20,000円/人日"; IndustrySubsidyPerWorker = (long)v; return null;
                case "preparedness": if (v < 0 || v > 20_000_000) return "防災投資は0〜2,000万円/日"; PreparednessBudget = (long)v; return null;
                case "repairBudget": if (v < 0 || v > 50_000_000) return "復旧予算は0〜5,000万円/日"; RepairBudget = (long)v; return null;
                case "pension": if (v < 0 || v > 20000) return "年金は0〜20,000円/日"; PensionPerDay = (long)v; return null;
                case "unemploymentBenefit": if (v < 0 || v > 20000) return "失業手当は0〜20,000円/日"; UnemploymentBenefit = (long)v; return null;
                case "electricityPrice": if (v < 5 || v > 100) return "電気料金は5〜100円/kWh"; ElectricityPrice = v; return null;
                case "rent": if (v < 0 || v > 5000) return "家賃は0〜5,000円/人日"; RentPerDay = (long)v; return null;
                case "privateInvestment": if (v != 0 && v != 1) return "民間投資は 0（禁止）か 1（許可）"; PrivateInvestment = (long)v; return null;
                case "autoGovernment": if (v != 0 && v != 1 && v != 2) return "自治体の自動運営は 0・1・2"; AutoGovernment = (long)v; return null;
                default: return $"未知の政策 '{key}'";
            }
        }
    }

    public sealed class ResearchState
    {
        public readonly SortedSet<string> Completed = new SortedSet<string>(StringComparer.Ordinal);
        /// <summary>Tech being researched, or null (the lab then picks the cheapest available).</summary>
        public string Current;
        public double Progress;
        public double PointsYesterday;
    }

    public sealed class City : Account
    {
        public readonly Policy Policy = new Policy();
        public readonly ResearchState Research = new ResearchState();
        /// <summary>Cumulative preparedness investment (depreciates slowly).</summary>
        public double PreparednessStock;
    }

    /// <summary>Local wholesale prices and yesterday's trade volumes per resource.</summary>
    public sealed class Market
    {
        public readonly SortedDictionary<string, double> Price = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, double> Produced = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, double> LocalTraded = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, double> Imported = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, double> Exported = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, double> Unmet = new SortedDictionary<string, double>(StringComparer.Ordinal);

        public void Init(ContentDatabase content)
        {
            foreach (ResourceDef r in content.Resources)
                if (!Price.ContainsKey(r.Id)) Price[r.Id] = r.BasePrice;
        }

        public double GetPrice(string res) => Price.TryGetValue(res, out double p) ? p : 0;

        public static void Add(SortedDictionary<string, double> d, string key, double v) =>
            d[key] = (d.TryGetValue(key, out double cur) ? cur : 0) + v;

        public void ClearDaily()
        {
            Produced.Clear();
            LocalTraded.Clear();
            Imported.Clear();
            Exported.Clear();
            Unmet.Clear();
        }
    }

    public sealed class WorldEvent
    {
        public long Tick;
        public string Kind;
        public string Message;
    }

    /// <summary>Demographic event counts for the current day (transient, read by statistics).</summary>
    public sealed class DailyCounters
    {
        public int Births, Deaths, Immigrants, Emigrants, Moves;
        public void Clear() { Births = Deaths = Immigrants = Emigrants = Moves = 0; }
    }

    /// <summary>All social and economic play state. Systems are stateless and operate on this.</summary>
    public sealed class EconomyState
    {
        public const long BaseWage = 8000;
        public const long CompanyStartCapital = 5_000_000;
        public const long CityStartFunds = 300_000_000;
        public const long RoadCostPerCell = 50_000;

        public readonly SortedDictionary<int, Citizen> Citizens = new SortedDictionary<int, Citizen>();
        /// <summary>Employers keyed by building id.</summary>
        public readonly SortedDictionary<int, Company> Companies = new SortedDictionary<int, Company>();
        /// <summary>Residents per residential building id.</summary>
        public readonly SortedDictionary<int, int> Occupancy = new SortedDictionary<int, int>();
        public readonly City City = new City();
        public readonly ExternalAccount External = new ExternalAccount();
        public readonly Market Market = new Market();
        public readonly Ledger Ledger = new Ledger();
        public readonly DailyCounters Daily = new DailyCounters();
        /// <summary>Why things changed (S1): noted by the systems, totalled per day.</summary>
        public readonly CauseLog Causes = new CauseLog();
        /// <summary>Day of the last private investment (S3 investor agent), -1 if none.</summary>
        public long LastInvestmentDay = -1;

        public EconomyState() { Ledger.Causes = Causes; }

        public int Population => Citizens.Count;

        public bool TryGetCompany(int buildingId, out Company c) => Companies.TryGetValue(buildingId, out c);

        /// <summary>Mean wage offered by open employers (the going rate a new business must match).</summary>
        public long AverageWage()
        {
            long sum = 0;
            int n = 0;
            foreach (Company c in Companies.Values)
                if (!c.Closed) { sum += c.Wage; n++; }
            return n == 0 ? BaseWage : sum / n;
        }

        public int Residents(int buildingId) => Occupancy.TryGetValue(buildingId, out int n) ? n : 0;

        /// <summary>Sum of all money including the external world. Constant unless there is a bug.</summary>
        public long TotalMoney()
        {
            long sum = City.Cash + External.Cash;
            foreach (Citizen c in Citizens.Values) sum += c.Cash;
            foreach (Company c in Companies.Values) sum += c.Cash;
            return sum;
        }

        public void MoveIn(Citizen c, int homeId)
        {
            if (c.HomeId != 0) Occupancy[c.HomeId] = Residents(c.HomeId) - 1;
            c.HomeId = homeId;
            if (homeId != 0) Occupancy[homeId] = Residents(homeId) + 1;
        }

        /// <summary>Days of work a contractor may have on its books before new jobs go to outside firms.</summary>
        public const int ContractorBacklogDays = 30;

        /// <summary>Yen of work a contractor's current staff can do per day.</summary>
        public static long WorkCapacity(Company co) => (long)(co.Workers.Count * co.Def.GetParam("workPerWorker"));

        /// <summary>
        /// Pays for construction, maintenance or repair work (S2). Local contractors take the work while
        /// they have room in their order books (shared by spare capacity); the rest goes to firms outside
        /// the town. Returns the yen that stayed local.
        /// </summary>
        public long PayForWork(Account payer, long amount, Flow flow)
        {
            if (amount <= 0) return 0;
            long totalFree = 0;
            foreach (Company co in Companies.Values)
                if (co.IsContractor && !co.Closed) totalFree += ContractorFree(co);
            long local = Math.Min(amount, totalFree), left = local;
            if (local > 0)
            {
                Company last = null;
                foreach (Company co in Companies.Values)
                {
                    if (!co.IsContractor || co.Closed) continue;
                    long free = ContractorFree(co);
                    if (free <= 0) continue;
                    long part = Math.Min(left, (long)((double)local * free / totalFree));
                    Award(co, payer, part, flow);
                    left -= part;
                    last = co;
                }
                if (left > 0 && last != null) { Award(last, payer, left, flow); left = 0; }
            }
            Ledger.Transfer(payer, External, amount - local + left, flow);
            return local - left;
        }

        private static long ContractorFree(Company co) => Math.Max(0, WorkCapacity(co) * ContractorBacklogDays - co.Backlog);

        private void Award(Company co, Account payer, long part, Flow flow)
        {
            if (part <= 0) return;
            Ledger.Transfer(payer, co, part, flow);
            co.RevenueToday += part;
            co.Backlog += part;
        }

        public void Hire(Company co, Citizen c, Cause cause = Cause.Hired)
        {
            if (c.JobId != 0) Fire(c);
            Causes.Note(c.Id, cause, co.BuildingId);
            co.Workers.Add(c.Id);
            c.JobId = co.BuildingId;
            c.UnemployedDays = 0;
            c.TenureDays = 0;
        }

        public void Fire(Citizen c, Cause cause = Cause.Unspecified)
        {
            if (cause != Cause.Unspecified) Causes.Note(c.Id, cause, c.JobId);
            if (c.JobId != 0 && Companies.TryGetValue(c.JobId, out Company co)) co.Workers.Remove(c.Id);
            c.JobId = 0;
        }

        /// <summary>Removes a citizen from the world; savings go to <paramref name="heir"/>.</summary>
        public void RemoveCitizen(Citizen c, Account heir, Flow flow, Cause cause = Cause.Unspecified)
        {
            Fire(c, cause);
            // Whoever stays behind is no longer in a couple (widowed, or the partner moved away).
            if (c.PartnerId != 0 && Citizens.TryGetValue(c.PartnerId, out Citizen partner) && partner.PartnerId == c.Id) partner.PartnerId = 0;
            c.PartnerId = 0;
            MoveIn(c, 0);
            Ledger.Transfer(c, heir, c.Cash, flow);
            Citizens.Remove(c.Id);
            Causes.Gone.Add(c.Id);
        }
    }
}
