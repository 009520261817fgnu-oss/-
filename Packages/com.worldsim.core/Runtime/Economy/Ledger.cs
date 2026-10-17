using System;

namespace WorldSim.Core.Economy
{
    /// <summary>Anything that holds money. Cash is integer yen so totals are exact.</summary>
    public abstract class Account
    {
        public long Cash;
    }

    /// <summary>
    /// The rest of the world: source of migrants' savings, investment and imports, sink for
    /// exports, fuel and materials. It may go negative. Including it, total money is constant.
    /// </summary>
    public sealed class ExternalAccount : Account
    {
    }

    public enum Flow
    {
        Wages,
        B2BSales,
        Retail,
        Freight,
        Imports,
        Exports,
        IncomeTax,
        CorporateTax,
        Subsidy,
        Maintenance,
        Construction,
        Energy,
        Fuel,
        Pension,
        Migration,
        Inheritance,
        Repair,
        TechAdoption,
        Investment,
        Divestment,
        Preparedness,
        Dividends,
        Benefit,
    }

    /// <summary>Single path for every money movement; keeps per-day totals by flow for analysis.</summary>
    public sealed class Ledger
    {
        public static readonly int FlowCount = Enum.GetValues(typeof(Flow)).Length;

        public long[] Today = new long[FlowCount];
        public long[] Yesterday = new long[FlowCount];

        public void Transfer(Account from, Account to, long amount, Flow flow)
        {
            if (amount <= 0 || from == to) return;
            from.Cash -= amount;
            to.Cash += amount;
            Today[(int)flow] += amount;
        }

        /// <summary>Transfers up to the payer's cash (never below zero). Returns the amount moved.</summary>
        public long TransferUpTo(Account from, Account to, long amount, Flow flow)
        {
            long amt = Math.Min(amount, Math.Max(0, from.Cash));
            Transfer(from, to, amt, flow);
            return amt;
        }

        public void EndDay()
        {
            long[] t = Yesterday;
            Yesterday = Today;
            Today = t;
            Array.Clear(Today, 0, Today.Length);
        }

        public long YesterdayOf(Flow f) => Yesterday[(int)f];
    }
}
