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

    /// <summary>Averages of the probed series over the last days of a run.</summary>
    public sealed class ProbeResult
    {
        public string Name;
        public readonly SortedDictionary<string, double> Values = new SortedDictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Relative change against a baseline (0.1 = +10%). Zero baselines give ±1 or 0.</summary>
        public double Change(ProbeResult baseline, string key)
        {
            double b = baseline.Values[key], v = Values[key];
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

        public static ProbeResult Run(ContentDatabase content, string baseSave, Intervention intervention, int days, int window = 14)
        {
            WorldState w = SaveSystem.Load(content, baseSave);
            intervention?.Apply(w);
            Bootstrap.WorldFactory.CreateRunner(w).StepMany(days * SimClock.TicksPerDay);
            var tail = w.History.Skip(Math.Max(0, w.History.Count - window)).ToList();
            var r = new ProbeResult { Name = intervention?.Name ?? "baseline" };
            foreach (string k in Series) r.Values[k] = tail.Count == 0 ? 0 : tail.Average(s => s.Get(k));
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
            new Intervention("電気料金 100円", w => new SetPolicyCommand("electricityPrice", 100).Execute(w)),
            new Intervention("発電所をすべて撤去", w => Remove(w, b => b.Def.Category == BuildingCategory.Power)),
            new Intervention("商店をすべて撤去", w => Remove(w, b => b.Def.Id == "shop")),
            new Intervention("食品工場を撤去", w => Remove(w, b => b.Def.Id == "food_plant")),
            new Intervention("農場を撤去", w => Remove(w, b => b.Def.Id == "farm")),
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
            sb.Append("介入".PadRight(18)).AppendLine(string.Join(" ", keys.Select(k => k.PadLeft(12))));
            sb.Append("基準（値）".PadRight(18)).AppendLine(string.Join(" ", keys.Select(k => baseline.Values[k].ToString("G4").PadLeft(12))));
            foreach (ProbeResult r in results)
            {
                sb.Append(r.Name.PadRight(18));
                sb.AppendLine(string.Join(" ", keys.Select(k => { double c = r.Change(baseline, k) * 100; return ((c >= 0 ? "+" : "") + c.ToString("0.0") + "%").PadLeft(12); })));
            }
            return sb.ToString();
        }
    }
}
