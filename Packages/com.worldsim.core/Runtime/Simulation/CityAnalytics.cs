using System;
using System.Collections.Generic;
using WorldSim.Core.Content;
using WorldSim.Core.Economy;
using WorldSim.Core.World;

namespace WorldSim.Core.Simulation
{
    public enum Severity
    {
        Info,
        Warning,
        Critical,
    }

    public sealed class Finding
    {
        public Severity Severity;
        public string Title;
        /// <summary>Likely cause and a suggested action, in plain language.</summary>
        public string Detail;
    }

    /// <summary>
    /// Basic city analysis (spec 2.10 / phase 3): rule-based diagnosis of problems with their likely
    /// causes, simple trend forecasts from the stats history, and a land value map for overlays.
    /// Deliberately simple and transparent; an AI advisor can build on these findings later.
    /// </summary>
    public static class CityAnalytics
    {
        public static List<Finding> Diagnose(WorldState w)
        {
            var list = new List<Finding>();
            List<StatsSnapshot> h = w.History;
            StatsSnapshot s = h.Count > 0 ? h[h.Count - 1] : StatsHistorySystem.Capture(w);
            EconomyState e = w.Economy;

            if (!w.Network.HasOutsideConnection)
                Add(list, Severity.Critical, "外部への道路がありません",
                    "マップ端まで道路が通じていないため輸出入ができません。端まで道路を延ばしてください。");

            if (s.UnemploymentRate > 0.08)
            {
                string cause = s.Vacancies == 0
                    ? $"求人が不足しています（労働力 {s.LabourForce} 人に対し就業 {s.Employed} 人）。工場や商業施設を増やすと改善します。"
                    : $"求人 {s.Vacancies} 件があるのに埋まっていません。通勤できない場所にある可能性があります。";
                Add(list, s.UnemploymentRate > 0.15 ? Severity.Critical : Severity.Warning, $"失業率 {s.UnemploymentRate:P0}", cause);
            }

            if (s.Vacancies > Math.Max(10, s.LabourForce * 0.05))
                Add(list, Severity.Warning, $"人手不足（欠員 {s.Vacancies} 人）",
                    "住宅を増やすと移住者が増えます。賃金は欠員の多い企業から上がっていきます。");

            if (w.Environment.EnergyRatio < 0.999)
                Add(list, Severity.Critical, $"電力不足（供給 {w.Environment.EnergyRatio:P0}）",
                    $"需要 {w.Environment.EnergyDemand:N0} kWh に対し供給 {w.Environment.EnergySupply:N0} kWh。発電所を建設するか、省エネ技術を研究してください。");

            int homeless = 0;
            foreach (Citizen c in e.Citizens.Values) if (c.HomeId == 0) homeless++;
            if (homeless > 0)
                Add(list, Severity.Warning, $"住宅を失った住民 {homeless} 人", "住宅を建設するか、被災した住宅の復旧を待ってください。");

            foreach (ResourceDef r in w.Content.Resources)
            {
                double produced = Get(s.Production, r.Id), imported = Get(s.Imported, r.Id);
                if (imported > 0 && imported > produced)
                    Add(list, Severity.Info, $"{r.Name}の輸入依存", $"地域内生産 {produced:N0} に対し輸入 {imported:N0}。生産施設を増やすと費用が下がります。");
                double unmet = Get(s.Unmet, r.Id);
                if (unmet > Math.Max(10, produced * 0.2))
                    Add(list, Severity.Warning, $"{r.Name}が不足", $"満たされなかった需要 {unmet:N0}。");
            }

            if (s.CongestedCells > 0)
                Add(list, Severity.Warning, $"渋滞 {s.CongestedCells:N0} マス",
                    "交通容量を超えた道路があります。迂回路を作るか、交通信号制御を研究してください。");

            if (h.Count >= 30)
            {
                double slope = Trend(h, "treasury", 30);
                if (slope < 0 && w.Economy.City.Cash + slope * 180 < 0)
                    Add(list, Severity.Critical, "財政破綻の恐れ", $"市の資金が1日あたり {-slope:N0} 円減少しています。半年以内に枯渇します。税率か支出を見直してください。");
                else if (slope < 0)
                    Add(list, Severity.Info, "財政赤字", $"市の資金が1日あたり {-slope:N0} 円減少しています。");
            }

            if (s.CompaniesClosed > 0)
                Add(list, Severity.Info, $"休業中の企業 {s.CompaniesClosed} 社", "需要不足や資金不足で倒産した企業です。市場が回復すると再開します。");
            if (s.AvgPollution > 20)
                Add(list, Severity.Warning, $"大気汚染（平均 {s.AvgPollution:0}）", "工場・発電所の近くの住民の満足度が下がります。排煙浄化装置の研究が有効です。");
            if (s.DamagedBuildings + s.DamagedRoads > 0)
                Add(list, Severity.Warning, $"被災中（建物 {s.DamagedBuildings}・道路 {s.DamagedRoads}）",
                    "復旧予算と消防・復旧センターの数で復旧速度が決まります。");

            if (list.Count == 0) Add(list, Severity.Info, "大きな問題はありません", "");
            list.Sort((a, b) => b.Severity.CompareTo(a.Severity));
            return list;
        }

        /// <summary>Least-squares slope per day of a series over the last <paramref name="days"/> snapshots.</summary>
        public static double Trend(List<StatsSnapshot> history, string series, int days)
        {
            int n = Math.Min(days, history.Count);
            if (n < 2) return 0;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                double y = history[history.Count - n + i].Get(series);
                sx += i; sy += y; sxx += i * (double)i; sxy += i * y;
            }
            double denom = n * sxx - sx * sx;
            return denom == 0 ? 0 : (n * sxy - sx * sy) / denom;
        }

        /// <summary>Linear forecast of a series <paramref name="daysAhead"/> days from now.</summary>
        public static double Forecast(List<StatsSnapshot> history, string series, int daysAhead, int window = 60)
        {
            if (history.Count == 0) return 0;
            double v = history[history.Count - 1].Get(series) + Trend(history, series, window) * daysAhead;
            // Shares and counts cannot leave their natural range, however steep the recent trend.
            if (series == "unemployment" || series == "happiness") return Math.Max(0, Math.Min(100, v));
            if (series == "population") return Math.Max(0, v);
            return v;
        }

        /// <summary>
        /// Land value index per building (0..200): shops, jobs and parks within reach raise it, pollution and
        /// damage lower it. Used for overlays and to explain where people want to live.
        /// </summary>
        public static Dictionary<int, double> LandValues(WorldState w)
        {
            var result = new Dictionary<int, double>();
            var retail = new List<int>();
            var employers = new List<Company>();
            var parks = new List<int>();
            foreach (Company co in w.Economy.Companies.Values)
            {
                if (co.Closed) continue;
                if (co.IsRetail) retail.Add(co.BuildingId);
                if (co.Def.GetParam("leisure") > 0) parks.Add(co.BuildingId);
                employers.Add(co);
            }
            var employerIds = new List<int>(employers.Count);
            foreach (Company co in employers) employerIds.Add(co.BuildingId);
            float[] hp = new float[parks.Count], hr = new float[retail.Count], he = new float[employers.Count];
            foreach (Building b in w.Buildings)
            {
                if (!w.Network.HasAccess(b.Id)) { result[b.Id] = 0; continue; }
                double shops = 0, jobs = 0, green = 0;
                w.Network.TravelHoursMany(b.Id, parks, hp);
                w.Network.TravelHoursMany(b.Id, retail, hr);
                w.Network.TravelHoursMany(b.Id, employerIds, he);
                foreach (float h in hp) if (h < 0.15f) green++;
                foreach (float h in hr) if (h < 0.15f) shops++;
                for (int i = 0; i < employers.Count; i++) if (he[i] < 0.25f) jobs += employers[i].Workers.Count;
                double v = 60 + Math.Min(60, shops * 6) + Math.Min(80, jobs / 10.0) + Math.Min(30, green * 15)
                           - w.Environment.PollutionAt(b.Origin) * 1.5 - (1 - b.Condition) * 60;
                result[b.Id] = Math.Max(0, Math.Min(200, v));
            }
            return result;
        }

        private static void Add(List<Finding> list, Severity sev, string title, string detail) =>
            list.Add(new Finding { Severity = sev, Title = title, Detail = detail });

        private static double Get(SortedDictionary<string, double> d, string k) => d.TryGetValue(k, out double v) ? v : 0;
    }
}
