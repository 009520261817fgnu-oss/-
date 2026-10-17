using System;
using System.Collections.Generic;
using System.Linq;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Outside investors (S3, an autonomous actor). Once a week they look at the last month: a product
    /// the town keeps importing, or retail demand that goes unmet, while people are looking for work
    /// and prices are healthy. Then they open a business that would serve it, paying for the building
    /// themselves, at most one every <see cref="MinDaysBetween"/> days. Every decision is logged with
    /// its reason, so the player can see why a new company appeared.
    /// </summary>
    public sealed class InvestmentSystem : ISimulationSystem
    {
        public const int LookBackDays = 30;
        public const int MinDaysBetween = 60;
        /// <summary>Imports must exceed this share of what one new plant would make at full staff.</summary>
        public const double ImportTrigger = 0.5;
        public const double MinUnemployment = 0.04;

        public string Name => "Investment";
        public int IntervalTicks => SimClock.TicksPerDay;

        public void Tick(WorldState w)
        {
            EconomyState e = w.Economy;
            long today = w.Clock.TotalDays;
            if (e.City.Policy.PrivateInvestment == 0 || today % 7 != 3) return;
            if (e.LastInvestmentDay >= 0 && today - e.LastInvestmentDay < MinDaysBetween) return;
            if (w.History.Count < LookBackDays || !w.Network.HasOutsideConnection) return;
            if (PopulationSystem.UnemploymentRate(w, today) < MinUnemployment) return; // nobody to hire

            var (def, cause, why) = Choose(w);
            if (def == null) return;
            if (!FindSpot(w, def, out GridPos spot)) return;
            CommandResult r = w.TryPlaceBuilding(def.Id, spot, e.External);
            if (!r.Success) return;
            e.LastInvestmentDay = today;
            e.Causes.Note(0, cause, r.CreatedId);
            e.Causes.Book(CauseMetric.CompaniesOpened, (int)cause, 1);
            w.Log("投資", $"市外の投資家が{why}を見て、{def.Name} #{r.CreatedId} を開業しました");
        }

        /// <summary>The most needed business, or null. Closed companies of the same kind are reopened first by finance.</summary>
        public static (BuildingDef def, Cause cause, string why) Choose(WorldState w)
        {
            List<StatsSnapshot> h = w.History;
            var recent = h.Skip(h.Count - LookBackDays).ToList();
            double Avg(Func<StatsSnapshot, SortedDictionary<string, double>> map, string res) =>
                recent.Average(s => map(s).TryGetValue(res, out double v) ? v : 0);

            BuildingDef best = null;
            double bestScore = 0;
            Cause bestCause = Cause.Unspecified;
            string bestWhy = null;
            foreach (BuildingDef def in w.Content.Buildings.OrderBy(d => d.Id, StringComparer.Ordinal))
            {
                if (!def.IsCompany || def.RequiresTech != null && !w.Effects.IsResearched(def.RequiresTech)) continue;
                if (w.Economy.Companies.Values.Any(c => c.Def == def && c.Closed)) continue; // a closed one can reopen instead
                // Producers: replace imports of what they make, if the price covers costs.
                if (def.Recipe != null && w.Content.TryGetRecipe(def.Recipe, out RecipeDef recipe))
                {
                    foreach (var kv in recipe.Outputs)
                    {
                        ResourceDef res = w.Content.GetResource(kv.Key);
                        double plantOutput = kv.Value * def.Jobs;
                        double imports = Avg(s => s.Imported, kv.Key);
                        if (imports < plantOutput * ImportTrigger || w.Economy.Market.GetPrice(kv.Key) < res.BasePrice * 0.9) continue;
                        if (!IncumbentsDoingWell(w, def)) continue;
                        double score = imports / plantOutput;
                        if (score > bestScore) { bestScore = score; best = def; bestCause = Cause.InvestedForImports; bestWhy = $"{res.Name}の輸入超過（1日 約{imports:0}{res.Unit}）"; }
                    }
                }
                // Shops: households could not buy what they wanted.
                if (def.Recipe == null && def.Sells.Length > 0)
                {
                    foreach (string sold in def.Sells)
                    {
                        ResourceDef res = w.Content.GetResource(sold);
                        double unmet = Avg(s => s.Unmet, sold);
                        double perShop = Math.Max(1, def.Jobs * 30);
                        if (unmet < perShop) continue;
                        double score = unmet / perShop;
                        if (score > bestScore) { bestScore = score; best = def; bestCause = Cause.InvestedForUnmetDemand; bestWhy = $"{res.Name}の品不足（1日 約{unmet:0}{res.Unit}）"; }
                    }
                }
            }
            return (best, bestCause, bestWhy);
        }

        /// <summary>
        /// A sensible investor only adds capacity when the existing local producers are profitable and
        /// nearly fully staffed: then imports mean "not enough plants", not "plants that cannot compete"
        /// or "plants that cannot find workers".
        /// </summary>
        public static bool IncumbentsDoingWell(WorldState w, BuildingDef def)
        {
            var same = w.Economy.Companies.Values.Where(c => c.Def == def && !c.Closed).ToList();
            if (same.Count == 0) return true;
            double profit = same.Average(c => c.ProfitAvg);
            double staffing = same.Sum(c => c.Workers.Count) / (double)Math.Max(1, same.Sum(c => w.Effects.JobCapacity(c)));
            return profit > 0 && staffing >= 0.7;
        }

        /// <summary>A spot with road access near existing businesses of the same category, else near the town centre.</summary>
        private static bool FindSpot(WorldState w, BuildingDef def, out GridPos spot)
        {
            Building anchor = w.Buildings.Where(b => b.Def.Category == def.Category).OrderBy(b => b.Id).FirstOrDefault();
            int x = anchor != null ? anchor.Origin.X : w.Width / 2, z = anchor != null ? anchor.Origin.Z : w.Depth / 2;
            return w.FindBuildableSpot(def, x, z, 40, out spot, out _, requireRoad: true);
        }
    }
}
