using System;
using System.Collections.Generic;
using System.Linq;
using WorldSim.Core.Commands;
using WorldSim.Core.Content;
using WorldSim.Core.Persistence;
using WorldSim.Core.Systems;
using WorldSim.Core.World;

namespace WorldSim.Core.Simulation
{
    /// <summary>One change applied to a copy of the world, e.g. a tax rise or removing all power plants.</summary>
    public sealed class Intervention
    {
        public readonly string Name;
        public readonly Action<WorldState> Apply;
        public Intervention(string name, Action<WorldState> apply) { Name = name; Apply = apply; }
    }

    /// <summary>
    /// Averages of the probed series over the last days of a run. With several noise seeds,
    /// <see cref="Values"/> is the mean and <see cref="Runs"/> keeps each seed's values.
    /// </summary>
    public sealed class ProbeResult
    {
        public string Name;
        public readonly SortedDictionary<string, double> Values = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public readonly List<SortedDictionary<string, double>> Runs = new List<SortedDictionary<string, double>>();

        /// <summary>Relative change of the means against a baseline (0.1 = +10%). Zero baselines give ±1 or 0.</summary>
        public double Change(ProbeResult baseline, string key) => Relative(baseline.Values[key], Values[key]);

        /// <summary>
        /// True when the effect is negligible (under 1% on average) or every noise seed moves the value
        /// the same way as the mean. False means chance events (a breakdown, who emigrates) decide the
        /// outcome, e.g. a blackout that happened under one seed only.
        /// </summary>
        public bool Consistent(ProbeResult baseline, string key)
        {
            double mean = Change(baseline, key);
            if (Math.Abs(mean) < 0.01) return true;
            int n = Math.Min(Runs.Count, baseline.Runs.Count);
            for (int i = 0; i < n; i++)
            {
                double c = Relative(baseline.Runs[i][key], Runs[i][key]);
                if (Math.Sign(c) != Math.Sign(mean) || Math.Abs(c) < 0.002) return false;
            }
            return true;
        }

        internal static double Relative(double b, double v)
        {
            if (Math.Abs(b) < 1e-9) return Math.Abs(v) < 1e-9 ? 0 : Math.Sign(v);
            return (v - b) / Math.Abs(b);
        }
    }

    /// <summary>
    /// Causal probe: start every run from the same saved state, apply exactly one intervention, run
    /// the same number of days and compare with the untouched baseline. Because the simulation is
    /// deterministic, every difference is caused by the intervention. Used by tests (expected
    /// directions of effects) and by the CLI (`--probe`) to audit how changes spread.
    /// </summary>
    public static class CausalProbe
    {
        public static readonly string[] Series =
        {
            "population", "unemployment", "avgWage", "happiness", "retailSales", "companyProfit", "treasury", "companiesOpen",
            "prod.food", "prod.goods", "pollution", "commute", "energySupply", "traffic", "imports", "exports", "research",
        };

        /// <summary>Noise seeds used by default: 0 keeps the saved random state, others re-seed it.</summary>
        public static readonly ulong[] DefaultNoise = { 0, 1, 2 };

        /// <summary>
        /// Runs the intervention once per noise seed. Baseline and intervention get the same seed, so
        /// each pair differs only by the intervention; averaging over seeds separates the effect from
        /// chance (equipment breakdowns, which resident emigrates...).
        /// </summary>
        public static ProbeResult Run(ContentDatabase content, string baseSave, Intervention intervention, int days, ulong[] noise = null, int window = 14)
        {
            noise = noise ?? DefaultNoise;
            var r = new ProbeResult { Name = intervention?.Name ?? "baseline" };
            foreach (ulong seed in noise)
            {
                WorldState w = SaveSystem.Load(content, baseSave);
                if (seed != 0) w.Random.State = DeterministicRandom.SplitMix(seed) | 1;
                intervention?.Apply(w);
                Bootstrap.WorldFactory.CreateRunner(w).StepMany(days * SimClock.TicksPerDay);
                var tail = w.History.Skip(Math.Max(0, w.History.Count - window)).ToList();
                var values = new SortedDictionary<string, double>(StringComparer.Ordinal);
                foreach (string k in Series) values[k] = tail.Count == 0 ? 0 : tail.Average(s => s.Get(k));
                r.Runs.Add(values);
            }
            foreach (string k in Series) r.Values[k] = r.Runs.Average(v => v[k]);
            return r;
        }

        /// <summary>The standard set of interventions (policy, removal, construction, disaster, isolation).</summary>
        public static List<Intervention> Standard() => new List<Intervention>
        {
            new Intervention("所得税 40%", w => new SetPolicyCommand("incomeTax", 0.40).Execute(w)),
            new Intervention("法人税 60%", w => new SetPolicyCommand("corporateTax", 0.60).Execute(w)),
            new Intervention("産業補助 2万円", w => new SetPolicyCommand("industrySubsidy", 20000).Execute(w)),
            new Intervention("失業手当 0", w => new SetPolicyCommand("unemploymentBenefit", 0).Execute(w)),
            new Intervention("年金 0", w => new SetPolicyCommand("pension", 0).Execute(w)),
            new Intervention("家賃 3倍", w => new SetPolicyCommand("rent", w.Economy.City.Policy.RentPerDay * 3).Execute(w)),
            new Intervention("電気料金 100円", w => new SetPolicyCommand("electricityPrice", 100).Execute(w)),
            new Intervention("発電所をすべて撤去", w => Remove(w, b => b.Def.Category == BuildingCategory.Power)),
            new Intervention("商店をすべて撤去", w => Remove(w, b => b.Def.Id == "shop")),
            new Intervention("食品工場を撤去", w => Remove(w, b => b.Def.Id == "food_plant")),
            new Intervention("農場を撤去", w => Remove(w, b => b.Def.Id == "farm")),
            new Intervention("建設会社を撤去", w => Remove(w, b => b.Def.Id == "construction")),
            new Intervention("研究所と学校を撤去", w => Remove(w, b => b.Def.Id == "research_lab" || b.Def.Id == "school")),
            new Intervention("町の近くに集合住宅6棟", w => BuildNearTown(w, "apartment", 6)),
            new Intervention("市外への道路を遮断", CutOutsideRoads),
            new Intervention("地震 M7.5", w => new TriggerDisasterCommand(DisasterKind.Earthquake, new GridPos(w.Width / 2, w.Depth / 2), 7.5).Execute(w)),
        };

        public static void Remove(WorldState w, Func<Building, bool> which)
        {
            foreach (Building b in w.Buildings.Where(which).OrderBy(b => b.Id).ToList())
                new BulldozeCommand(b.Origin).Execute(w);
        }

        /// <summary>Builds near the town centre on spots with road access (skips if none).</summary>
        public static int BuildNearTown(WorldState w, string defId, int count)
        {
            BuildingDef def = w.Content.GetBuilding(defId);
            int built = 0;
            for (int attempt = 0; attempt < count * 3 && built < count; attempt++)
            {
                if (!w.FindBuildableSpot(def, w.Width / 2, w.Depth / 2, 40, out GridPos o, out bool road) || !road) break;
                if (new PlaceBuildingCommand(defId, o).Execute(w).Success) built++;
            }
            return built;
        }

        /// <summary>Removes road cells near the map edge, which is where the town meets the outside world.</summary>
        public static void CutOutsideRoads(WorldState w)
        {
            for (int z = 0; z < w.Depth; z++)
            for (int x = 0; x < w.Width; x++)
            {
                bool edge = x < 2 || z < 2 || x >= w.Width - 2 || z >= w.Depth - 2;
                var c = new GridPos(x, z);
                if (edge && w.HasRoad(c)) new BulldozeCommand(c).Execute(w);
            }
        }

        /// <summary>A fixed-width text table of relative changes against the baseline, for the CLI and docs.</summary>
        public static string Table(ProbeResult baseline, IEnumerable<ProbeResult> results, string[] keys = null)
        {
            keys = keys ?? Series;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"（{baseline.Runs.Count}通りの乱数で平均。? は乱数によって向きが変わる＝偶然に左右される結果）");
            sb.Append("介入".PadRight(18)).AppendLine(string.Join(" ", keys.Select(k => k.PadLeft(12))));
            sb.Append("基準（値）".PadRight(18)).AppendLine(string.Join(" ", keys.Select(k => baseline.Values[k].ToString("G4").PadLeft(12))));
            foreach (ProbeResult r in results)
            {
                sb.Append(r.Name.PadRight(18));
                sb.AppendLine(string.Join(" ", keys.Select(k =>
                {
                    double c = r.Change(baseline, k) * 100;
                    string mark = r.Consistent(baseline, k) ? "" : "?"; // the seeds disagree: chance, not a dependable effect
                    return ((c >= 0 ? "+" : "") + c.ToString("0.0") + "%" + mark).PadLeft(12);
                })));
            }
            return sb.ToString();
        }
    }
}
