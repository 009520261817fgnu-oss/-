using System;
using System.Collections.Generic;
using System.Linq;
using WorldSim.Core.Economy;
using WorldSim.Core.World;

namespace WorldSim.Core.Simulation
{
    /// <summary>One cause's share of an indicator's change, plus the companies or buildings most involved.</summary>
    public sealed class Contribution
    {
        public string Label;
        public double Value;
        /// <summary>Building ids most involved (e.g. the companies that laid people off), most first.</summary>
        public List<(int id, int count)> Agents = new List<(int, int)>();
    }

    /// <summary>Why an indicator moved over a period: the change split into causes that add up to it.</summary>
    public sealed class Explanation
    {
        public string Metric, Title, Unit;
        /// <summary>Extra context in words, e.g. the prices behind a % change.</summary>
        public string Note;
        public long FromDay, ToDay;
        public double Start, End;
        public double Change => End - Start;
        public readonly List<Contribution> Parts = new List<Contribution>();
        /// <summary>Sum of the parts divided by the actual change (1 = fully explained).</summary>
        public double Explained => Math.Abs(Change) < 1e-12 ? (Parts.Sum(p => Math.Abs(p.Value)) < 1e-9 ? 1 : 0) : Parts.Sum(p => p.Value) / Change;
    }

    /// <summary>
    /// Decomposes the change of an indicator into causes using the cause log (S1-4). Totals are exact
    /// because the log books every transition; this class only does the arithmetic and labelling.
    /// </summary>
    public static class Explainer
    {
        public static readonly string[] Metrics = { "unemployment", "population", "treasury", "householdCash", "price.food", "price.goods" };

        public static Explanation Explain(WorldState w, string metric, int days)
        {
            List<StatsSnapshot> h = w.History;
            if (h.Count < 2) return null;
            int end = h.Count - 1;
            long endDay = DayOf(h[end]);
            // Only days still in the cause log can be explained.
            long oldestLogged = w.Economy.Causes.Days.Count > 0 ? w.Economy.Causes.Days[0].Day : endDay;
            int start = end;
            while (start > 0 && endDay - DayOf(h[start - 1]) <= days && DayOf(h[start - 1]) >= oldestLogged - 1) start--;
            if (start == end) return null;
            return Explain(w, metric, h[start], h[end]);
        }

        public static Explanation Explain(WorldState w, string metric, StatsSnapshot a, StatsSnapshot b)
        {
            CauseLog log = w.Economy.Causes;
            long from = DayOf(a) + 1, to = DayOf(b);
            var x = new Explanation { Metric = metric, FromDay = from, ToDay = to };
            switch (metric)
            {
                case "unemployment":
                {
                    x.Title = "失業率"; x.Unit = "pt";
                    double l0 = a.LabourForce, l1 = b.LabourForce, u0 = l0 == 0 ? 0 : 1 - a.Employed / l0;
                    x.Start = u0 * 100; x.End = b.UnemploymentRate * 100;
                    var du = log.Sum(CauseMetric.Unemployed, from, to);
                    var dl = log.Sum(CauseMetric.LabourForce, from, to);
                    // u1 - u0 = (ΔU - u0·ΔL) / L1, which splits exactly by cause.
                    foreach (int k in du.Keys.Union(dl.Keys).Distinct())
                    {
                        double v = l1 == 0 ? 0 : ((du.TryGetValue(k, out long u) ? u : 0) - u0 * (dl.TryGetValue(k, out long l) ? l : 0)) / l1 * 100;
                        Add(x, CauseLabel((Cause)k), v, AgentsFor(log, (Cause)k, from, to));
                    }
                    break;
                }
                case "population":
                    x.Title = "人口"; x.Unit = "人";
                    x.Start = a.Population; x.End = b.Population;
                    foreach (var kv in log.Sum(CauseMetric.Population, from, to)) Add(x, CauseLabel((Cause)kv.Key), kv.Value, null);
                    break;
                case "treasury":
                    x.Title = "市の資金"; x.Unit = "円";
                    x.Start = a.Treasury; x.End = b.Treasury;
                    foreach (var kv in log.Sum(CauseMetric.CityMoney, from, to)) Add(x, FlowLabel((Flow)kv.Key), kv.Value, null);
                    break;
                case "householdCash":
                    x.Title = "家計の貯蓄"; x.Unit = "円";
                    x.Start = a.HouseholdCash; x.End = b.HouseholdCash;
                    foreach (var kv in log.Sum(CauseMetric.HouseholdMoney, from, to)) Add(x, FlowLabel((Flow)kv.Key), kv.Value, null);
                    break;
                default:
                    if (!metric.StartsWith("price.")) throw new ArgumentException("unknown metric " + metric);
                    string res = metric.Substring(6);
                    int index = w.Content.Resources.ToList().FindIndex(r => r.Id == res);
                    if (index < 0) throw new ArgumentException("unknown resource " + res);
                    x.Title = w.Content.GetResource(res).Name + "の価格"; x.Unit = "%";
                    double p0 = a.Prices.TryGetValue(res, out double pa) ? pa : 0, p1 = b.Prices.TryGetValue(res, out double pb) ? pb : 0;
                    x.Start = 0; x.End = p0 == 0 ? 0 : (p1 / p0 - 1) * 100;
                    x.Note = $"{p0:N0}円 → {p1:N0}円（基準価格 {w.Content.GetResource(res).BasePrice:N0}円、上限はその1.6倍・下限0.6倍）";
                    // Daily moves are booked in log units; scale them so the parts add up to the % change.
                    var logs = log.Sum(CauseMetric.PriceLog, from, to).Where(kv => kv.Key / 64 == index).ToList();
                    double total = logs.Sum(kv => kv.Value) / 1e6;
                    foreach (var kv in logs)
                        Add(x, CauseLabel((Cause)(kv.Key % 64)), Math.Abs(total) < 1e-12 ? 0 : kv.Value / 1e6 / total * x.End, null);
                    break;
            }
            x.Parts.Sort((p, q) => Math.Abs(q.Value).CompareTo(Math.Abs(p.Value)));
            return x;
        }

        /// <summary>
        /// Why two branches differ: each branch's change since the fork is decomposed, and the parts are
        /// subtracted (branch minus the other). Both start from the same state, so the parts add up to
        /// the difference between the branches today.
        /// </summary>
        public static Explanation CompareBranches(WorldState a, WorldState b, string metric, long forkTick)
        {
            Explanation ea = SinceTick(a, metric, forkTick), eb = SinceTick(b, metric, forkTick);
            if (ea == null || eb == null) return null;
            var x = new Explanation { Metric = metric, Title = ea.Title, Unit = ea.Unit, FromDay = ea.FromDay, ToDay = ea.ToDay, Start = eb.End, End = ea.End };
            if (metric.StartsWith("price.")) { x.Start = 0; x.End = ea.End - eb.End; }
            foreach (string label in ea.Parts.Select(p => p.Label).Union(eb.Parts.Select(p => p.Label)))
            {
                double va = ea.Parts.Where(p => p.Label == label).Sum(p => p.Value), vb = eb.Parts.Where(p => p.Label == label).Sum(p => p.Value);
                Add(x, label, va - vb, null);
            }
            x.Parts.Sort((p, q) => Math.Abs(q.Value).CompareTo(Math.Abs(p.Value)));
            return x;
        }

        /// <summary>From the last snapshot taken at or before <paramref name="tick"/> (shared by both branches) to now.</summary>
        private static Explanation SinceTick(WorldState w, string metric, long tick)
        {
            List<StatsSnapshot> h = w.History;
            StatsSnapshot a = h.LastOrDefault(s => s.Tick <= tick), b = h.Count > 0 ? h[h.Count - 1] : null;
            if (a == null || b == null || a == b) return null;
            if (w.Economy.Causes.Days.Count == 0 || w.Economy.Causes.Days[0].Day > DayOf(a) + 1) return null;
            return Explain(w, metric, a, b);
        }

        private static long DayOf(StatsSnapshot s) => s.Tick / SimClock.TicksPerDay;

        private static void Add(Explanation x, string label, double v, List<(int, int)> agents)
        {
            if (Math.Abs(v) < 1e-9) return;
            Contribution c = x.Parts.FirstOrDefault(p => p.Label == label);
            if (c == null) x.Parts.Add(c = new Contribution { Label = label });
            c.Value += v;
            if (agents != null) c.Agents = agents;
        }

        /// <summary>The companies or buildings named most often in the period's notes for this cause.</summary>
        private static List<(int, int)> AgentsFor(CauseLog log, Cause cause, long from, long to)
        {
            long t0 = (from - 1) * SimClock.TicksPerDay, t1 = (to + 1) * SimClock.TicksPerDay;
            return log.Notes.Where(n => n.Cause == cause && n.Agent != 0 && n.Tick >= t0 && n.Tick < t1)
                .GroupBy(n => n.Agent).Select(g => (g.Key, g.Count())).OrderByDescending(t => t.Item2).ThenBy(t => t.Key).Take(5).ToList();
        }

        public static string CauseLabel(Cause c)
        {
            switch (c)
            {
                case Cause.LossesDownsizing: return "赤字による人員削減";
                case Cause.OverstockDownsizing: return "在庫過多による人員削減";
                case Cause.InputShortageDownsizing: return "原料不足による人員削減";
                case Cause.CashShortDownsizing: return "資金不足による人員削減";
                case Cause.Automation: return "自動化（必要人員の減少）";
                case Cause.Bankruptcy: return "倒産";
                case Cause.Demolished: return "建物の撤去";
                case Cause.CommuteTooLong: return "通勤が長すぎて離職";
                case Cause.Absence: return "無断欠勤で解雇";
                case Cause.QuitByChoice: return "自分の意思で退職";
                case Cause.Retirement: return "定年退職";
                case Cause.Hired: return "就職";
                case Cause.SwitchedForBetterPay: return "より良い職への転職";
                case Cause.Born: return "出生";
                case Cause.Died: return "死亡";
                case Cause.ImmigratedForJobs: return "求人を求めて転入";
                case Cause.ImmigratedForHousing: return "住まいを求めて転入";
                case Cause.EmigratedUnhappy: return "不満による転出";
                case Cause.EmigratedUnemployed: return "長期失業による転出";
                case Cause.EmigratedHomeless: return "住まいがなく転出";
                case Cause.PlayerCreated: return "プレイヤーの人物";
                case Cause.ComingOfAge: return "18歳になり就業年齢へ";
                case Cause.Ageing: return "就業年齢を過ぎた";
                case Cause.Reopened: return "営業再開";
                case Cause.PriceShortage: return "品不足（需要が満たされない）";
                case Cause.PriceImports: return "輸入に頼っている";
                case Cause.PriceSurplus: return "供給過剰（輸出に回る）";
                case Cause.PriceBound: return "価格の上限・下限";
                case Cause.PriceCostPush: return "費用の上昇を価格に転嫁";
                case Cause.InvestedForImports: return "輸入超過を見た民間投資";
                case Cause.InvestedForUnmetDemand: return "品不足を見た民間投資";
                case Cause.PublicStaffing: return "公共・発電の定員変更";
                case Cause.Disaster: return "災害";
                case Cause.MovedCloserToWork: return "職場の近くへ転居";
                case Cause.Evicted: return "家賃滞納で退去";
                case Cause.MovedForCheaperRent: return "安い家賃の住まいへ転居";
                case Cause.DiedOfIllness: return "健康悪化による死亡";
                default: return "理由の記録なし";
            }
        }

        public static string FlowLabel(Flow f)
        {
            switch (f)
            {
                case Flow.Wages: return "賃金";
                case Flow.B2BSales: return "企業間取引";
                case Flow.Retail: return "買い物";
                case Flow.Freight: return "運賃";
                case Flow.Imports: return "輸入";
                case Flow.Exports: return "輸出";
                case Flow.IncomeTax: return "所得税";
                case Flow.CorporateTax: return "法人税";
                case Flow.Subsidy: return "産業補助";
                case Flow.Maintenance: return "維持費";
                case Flow.Construction: return "建設費";
                case Flow.Energy: return "電気代";
                case Flow.Fuel: return "燃料";
                case Flow.Pension: return "年金";
                case Flow.Migration: return "転入・転出に伴う持ち込み・持ち出し";
                case Flow.Inheritance: return "相続（死亡）";
                case Flow.Repair: return "修理・復旧";
                case Flow.TechAdoption: return "技術導入";
                case Flow.Investment: return "外部からの出資";
                case Flow.Divestment: return "撤退・倒産での引き上げ";
                case Flow.Preparedness: return "防災投資";
                case Flow.Dividends: return "配当";
                case Flow.Benefit: return "失業手当";
                case Flow.Tuition: return "職業訓練の授業料";
                case Flow.Moving: return "引っ越し費用";
                case Flow.Rent: return "家賃";
                case Flow.Medical: return "診察代";
                default: return f.ToString();
            }
        }
    }
}
