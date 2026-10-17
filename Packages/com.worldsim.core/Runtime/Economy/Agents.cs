using System;
using System.Collections.Generic;
using WorldSim.Core.Content;

namespace WorldSim.Core.Economy
{
    /// <summary>An individually simulated resident. Cash is personal savings.</summary>
    public sealed class Citizen : Account
    {
        public int Id;
        /// <summary>Day number of birth (may be negative for the initial population).</summary>
        public long BirthDay;
        /// <summary>Residential building id, 0 = homeless.</summary>
        public int HomeId;
        /// <summary>Workplace building id, 0 = not employed.</summary>
        public int JobId;
        /// <summary>0 = basic, 1 = secondary, 2 = higher.</summary>
        public int Education;
        public bool Retired;
        public float Happiness = 0.6f;
        /// <summary>0..1 (S2): worn by polluted air, age and hunger; restored by rest and clinics.</summary>
        public float Health = Systems.HealthSystem.Initial;
        public float CommuteHours;
        public int UnemployedDays;
        /// <summary>Net income received yesterday (wage or pension after tax).</summary>
        public long LastNetIncome;
        /// <summary>Rent paid yesterday (S2: housing costs) and days in arrears.</summary>
        public long LastRent;
        public int RentArrearsDays;
        /// <summary>Share of desired food / goods / services bought yesterday (0..1).</summary>
        public float FoodMet = 1f, GoodsMet = 1f, ServicesMet = 1f;
        /// <summary>Days the student has spent at school (for education level at 18).</summary>
        public int SchoolDays;
        /// <summary>Days worked at the current employer; every half year is a promotion step.</summary>
        public int TenureDays;
        /// <summary>Closeness (0..1) to other residents, only for people this one has interacted with.</summary>
        public SortedDictionary<int, float> Relations;

        public const int DaysPerPromotion = 180;
        public const int MaxPromotions = 3;
        /// <summary>Promotion level 0..3 from tenure.</summary>
        public int Level => Math.Min(MaxPromotions, TenureDays / DaysPerPromotion);
        /// <summary>Pay multiplier from promotions (+4% per level).</summary>
        public double WageFactor => 1 + 0.04 * Level;

        public float Affinity(int other) => Relations != null && Relations.TryGetValue(other, out float v) ? v : 0f;

        public void AddAffinity(int other, float delta)
        {
            Relations ??= new SortedDictionary<int, float>();
            Relations[other] = Math.Max(0f, Math.Min(1f, Affinity(other) + delta));
        }

        public int AgeYears(long today) => (int)((today - BirthDay) / Simulation.SimClock.DaysPerYear);
        public bool IsWorkingAge(long today) { int a = AgeYears(today); return a >= 18 && a < 65; }
    }

    /// <summary>
    /// An employer bound 1:1 to a building (keyed by building id). Private companies hold their own
    /// cash; public institutions (IsPublic) are paid for by the city treasury.
    /// </summary>
    public sealed class Company : Account
    {
        public int BuildingId;
        public BuildingDef Def;
        public RecipeDef Recipe;
        public bool IsPublic;

        public long Wage;
        public int TargetWorkers;
        /// <summary>Why the last weekly staffing decision cut jobs (used to label the layoffs).</summary>
        public Cause TargetCause;
        /// <summary>Employee citizen ids in hiring order (last hired is first fired).</summary>
        public readonly List<int> Workers = new List<int>();
        public readonly SortedDictionary<string, double> Inventory = new SortedDictionary<string, double>(StringComparer.Ordinal);

        public bool Closed;
        public int ClosedDays;
        /// <summary>Days of unpaid or partially paid payroll in a row.</summary>
        public int DistressDays;
        public int VacancyDays;
        /// <summary>Remaining days of equipment breakdown (0 = working).</summary>
        public int BrokenDays;
        public readonly SortedSet<string> AdoptedTechs = new SortedSet<string>(StringComparer.Ordinal);

        // Daily accounting, reset by FinanceSystem.
        public long RevenueToday;
        public long CostToday;
        /// <summary>Exponential moving average of daily profit (~10 days).</summary>
        public double ProfitAvg;
        public long LastProfit;
        /// <summary>Yesterday's total costs (wages, inputs, freight, energy...), for unit-cost pricing (S3).</summary>
        public long LastCost;

        // Operating measures from the last day.
        public double LastOutput;
        public double OutputCapacity;
        /// <summary>Production limited by missing inputs yesterday.</summary>
        public bool InputShortage;
        public double EnergyUse;
        /// <summary>Smoothed units sold per day by retail resource.</summary>
        public readonly SortedDictionary<string, double> SalesAvg = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, double> SalesToday = new SortedDictionary<string, double>(StringComparer.Ordinal);

        public double Stock(string res) => Inventory.TryGetValue(res, out double v) ? v : 0;
        public void AddStock(string res, double qty) => Inventory[res] = Stock(res) + qty;
        public bool Sells(string res) => Array.IndexOf(Def.Sells, res) >= 0;
        public bool Produces(string res) => Recipe != null && Recipe.Outputs.ContainsKey(res);
        public bool IsOperating => !Closed;
        public bool IsRetail => Def.Sells.Length > 0;
        public bool IsLogistics => Def.GetParam("logistics") > 0;
        /// <summary>Builds, maintains and repairs for others (S2: the local construction industry).</summary>
        public bool IsContractor => Def.GetParam("workPerWorker") > 0;
        /// <summary>Contracted work not yet done, in yen (contractors only).</summary>
        public long Backlog;
        public double PowerOutput => Def.GetParam("powerOutput");
    }
}
