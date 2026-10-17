using System;
using System.Collections.Generic;

namespace WorldSim.Core.Economy
{
    /// <summary>
    /// Why something changed. Every hire, layoff, arrival, departure, closure and price move carries
    /// one of these, so indicators can be decomposed into their causes (S1, explainability).
    /// Values are saved: append only, never renumber.
    /// </summary>
    public enum Cause : byte
    {
        Unspecified = 0,
        // losing a job
        LossesDownsizing = 1,
        OverstockDownsizing = 2,
        InputShortageDownsizing = 3,
        CashShortDownsizing = 4,
        Automation = 5,
        Bankruptcy = 6,
        Demolished = 7,
        CommuteTooLong = 8,
        Absence = 9,
        QuitByChoice = 10,
        Retirement = 11,
        // finding a job
        Hired = 12,
        SwitchedForBetterPay = 13,
        // arriving and leaving
        Born = 14,
        Died = 15,
        ImmigratedForJobs = 16,
        ImmigratedForHousing = 17,
        EmigratedUnhappy = 18,
        EmigratedUnemployed = 19,
        EmigratedHomeless = 20,
        PlayerCreated = 21,
        // implicit transitions found by comparing days
        ComingOfAge = 22,
        Ageing = 23,
        // companies
        Reopened = 24,
        // prices
        PriceShortage = 25,
        PriceImports = 26,
        PriceSurplus = 27,
        PriceBound = 28,
        // other
        PublicStaffing = 29,
        Disaster = 30,
        MovedCloserToWork = 31,
        Evicted = 32,
        MovedForCheaperRent = 33,
        DiedOfIllness = 34,
    }

    /// <summary>What a daily total measures. Saved: append only.</summary>
    public enum CauseMetric : byte
    {
        Population = 0,
        LabourForce = 1,
        Unemployed = 2,
        JobsLost = 3,
        JobsGained = 4,
        CompaniesClosed = 5,
        CompaniesReopened = 6,
        /// <summary>Net yen per flow (sub key = Flow) for the city treasury.</summary>
        CityMoney = 7,
        HouseholdMoney = 8,
        CompanyMoney = 9,
        ExternalMoney = 10,
        /// <summary>Price change in log units ×1e6 (sub key = resource index × 64 + cause).</summary>
        PriceLog = 11,
    }

    /// <summary>One noted event: who, why, and the other party (company or building id).</summary>
    public struct CauseNote
    {
        public long Tick;
        public int Citizen;
        public Cause Cause;
        public int Agent;
    }

    /// <summary>Daily totals by metric and cause, for the days since the previous statistics snapshot.</summary>
    public sealed class CauseDay
    {
        public long Day;
        public readonly SortedDictionary<int, long> Totals = new SortedDictionary<int, long>();

        public static int Key(CauseMetric m, int sub) => ((int)m << 24) | (sub & 0xFFFFFF);
        public static CauseMetric MetricOf(int key) => (CauseMetric)(key >> 24);
        public static int SubOf(int key) => key & 0xFFFFFF;

        public void Add(CauseMetric m, int sub, long v)
        {
            if (v == 0) return;
            int k = Key(m, sub);
            Totals[k] = (Totals.TryGetValue(k, out long old) ? old : 0) + v;
        }

        public long Get(CauseMetric m, int sub) => Totals.TryGetValue(Key(m, sub), out long v) ? v : 0;
    }

    /// <summary>
    /// The cause record. Systems call <see cref="Note"/> when they change a person or company; at the
    /// end of each day <see cref="CloseDay"/> compares every resident with the previous day and books
    /// each real transition (job lost, arrived, left the labour force...) under the latest noted cause.
    /// Comparing states makes the totals exact even for changes nobody notes (ageing).
    /// </summary>
    public sealed class CauseLog
    {
        public const int KeepDays = 400;
        public const int KeepNoteDays = 90;

        private const byte Exists = 1, InLabour = 2, Employed = 4;

        public long Now;
        public readonly List<CauseNote> Notes = new List<CauseNote>();
        public readonly List<CauseDay> Days = new List<CauseDay>();
        /// <summary>Totals booked since the last snapshot (money and prices are booked as they happen).</summary>
        public CauseDay Open = new CauseDay();
        /// <summary>Each resident's status at the previous day's close (Exists | InLabour | Employed).</summary>
        public readonly Dictionary<int, byte> Last = new Dictionary<int, byte>();
        /// <summary>Residents removed since the last close (so the close need not scan for them).</summary>
        public readonly List<int> Gone = new List<int>();
        /// <summary>Latest cause noted today per resident (cleared at close).</summary>
        public readonly SortedDictionary<int, CauseNote> Today = new SortedDictionary<int, CauseNote>();
        /// <summary>False until the first close; the first comparison has no previous day.</summary>
        public bool Primed;

        public void Note(int citizen, Cause cause, int agent)
        {
            var n = new CauseNote { Tick = Now, Citizen = citizen, Cause = cause, Agent = agent };
            Notes.Add(n);
            if (citizen != 0) Today[citizen] = n;
        }

        public void Book(CauseMetric m, int sub, long v) => Open.Add(m, sub, v);

        /// <summary>
        /// Net money per sector (City, Household, Company, External) and flow since the last close.
        /// Money moves thousands of times a day, so it is summed in an array and folded into the day
        /// record at close (see <see cref="OpenWithMoney"/>).
        /// </summary>
        public readonly long[,] Money = new long[4, Ledger.FlowCount];

        public static int SectorIndex(CauseMetric m) => (int)m - (int)CauseMetric.CityMoney;

        /// <summary>The open day including the money not yet folded in (used by saving; does not modify).</summary>
        public CauseDay OpenWithMoney()
        {
            var d = new CauseDay { Day = Open.Day };
            foreach (var kv in Open.Totals) d.Totals[kv.Key] = kv.Value;
            FoldMoney(d);
            return d;
        }

        private void FoldMoney(CauseDay d)
        {
            for (int s = 0; s < 4; s++)
            for (int f = 0; f < Ledger.FlowCount; f++)
                d.Add((CauseMetric)((int)CauseMetric.CityMoney + s), f, Money[s, f]);
        }

        private static byte StatusOf(Citizen c, long today)
        {
            byte s = Exists;
            if (!c.Retired && c.IsWorkingAge(today))
            {
                s |= InLabour;
                if (c.JobId != 0) s |= Employed;
            }
            return s;
        }

        public void CloseDay(EconomyState e, long today)
        {
            CauseDay d = Open;
            d.Day = today;
            FoldMoney(d);
            Array.Clear(Money, 0, Money.Length);
            foreach (Citizen c in e.Citizens.Values)
            {
                byte now = StatusOf(c, today);
                Last.TryGetValue(c.Id, out byte was);
                if (was == now) continue;
                if (Primed) Transition(d, c.Id, was, now, c.Retired);
                Last[c.Id] = now;
            }
            foreach (int id in Gone)
            {
                if (!Last.TryGetValue(id, out byte was) || e.Citizens.ContainsKey(id)) continue;
                if (Primed) Transition(d, id, was, 0, false);
                Last.Remove(id);
            }
            Gone.Clear();
            Primed = true;
            Today.Clear();
            Days.Add(d);
            if (Days.Count > KeepDays) Days.RemoveRange(0, Days.Count - KeepDays);
            long cutoff = (today - KeepNoteDays) * 24;
            int drop = 0;
            while (drop < Notes.Count && Notes[drop].Tick < cutoff) drop++;
            if (drop > 0) Notes.RemoveRange(0, drop);
            Open = new CauseDay();
        }

        private void Transition(CauseDay d, int id, byte was, byte now, bool retired)
        {
            if (was == now) return;
            bool wasHere = (was & Exists) != 0, here = (now & Exists) != 0;
            bool wasL = (was & InLabour) != 0, isL = (now & InLabour) != 0;
            bool wasE = (was & Employed) != 0, isE = (now & Employed) != 0;
            bool wasU = wasL && !wasE, isU = isL && !isE;
            Cause cause = Today.TryGetValue(id, out CauseNote n) ? n.Cause : Cause.Unspecified;
            if (cause == Cause.Unspecified)
            {
                // Nobody noted a reason: the change came from age alone.
                if (!wasL && isL) cause = Cause.ComingOfAge;
                else if (wasL && !isL) cause = retired ? Cause.Retirement : Cause.Ageing;
            }
            int sub = (int)cause;
            if (wasHere != here) d.Add(CauseMetric.Population, sub, here ? 1 : -1);
            d.Add(CauseMetric.LabourForce, sub, (isL ? 1 : 0) - (wasL ? 1 : 0));
            d.Add(CauseMetric.Unemployed, sub, (isU ? 1 : 0) - (wasU ? 1 : 0));
            if (wasE && !isE) d.Add(CauseMetric.JobsLost, sub, 1);
            if (!wasE && isE) d.Add(CauseMetric.JobsGained, sub, 1);
        }

        /// <summary>Sum of a metric's sub keys over days in [fromDay, toDay].</summary>
        public SortedDictionary<int, long> Sum(CauseMetric m, long fromDay, long toDay)
        {
            var r = new SortedDictionary<int, long>();
            foreach (CauseDay d in Days)
            {
                if (d.Day < fromDay || d.Day > toDay) continue;
                foreach (var kv in d.Totals)
                {
                    if (CauseDay.MetricOf(kv.Key) != m) continue;
                    int sub = CauseDay.SubOf(kv.Key);
                    r[sub] = (r.TryGetValue(sub, out long v) ? v : 0) + kv.Value;
                }
            }
            return r;
        }
    }
}
