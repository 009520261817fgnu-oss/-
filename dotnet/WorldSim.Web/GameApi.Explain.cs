using System.Collections.Generic;
using System.Linq;
using Microsoft.JSInterop;
using WorldSim.Core;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Web
{
    /// <summary>"Why?" for the browser: indicator changes split into causes (S1-4).</summary>
    public static partial class GameApi
    {
        [JSInvokable]
        public static string Explain(string metric, int days) =>
            ExplanationJson(World, Explainer.Explain(World, metric, System.Math.Max(1, System.Math.Min(CauseLogDays, days))));

        /// <summary>Why the active branch differs from another one, from the moment they diverged.</summary>
        [JSInvokable]
        public static string ExplainBranches(string metric, int otherId)
        {
            var other = _branches.Get(otherId);
            long tick = _branches.DivergenceTick(_branches.Active.Id, otherId);
            return ExplanationJson(World, Explainer.CompareBranches(World, other.World, metric, tick));
        }

        private const int CauseLogDays = Core.Economy.CauseLog.KeepDays - 10;

        private static string ExplanationJson(WorldState w, Explanation x)
        {
            if (x == null) return Json.Serialize(new Dictionary<string, object> { ["ok"] = false });
            return Json.Serialize(new Dictionary<string, object>
            {
                ["ok"] = true, ["metric"] = x.Metric, ["title"] = x.Title, ["unit"] = x.Unit, ["note"] = x.Note,
                ["fromDay"] = (double)x.FromDay, ["toDay"] = (double)x.ToDay,
                ["start"] = x.Start, ["end"] = x.End, ["change"] = x.Change, ["explained"] = x.Explained,
                ["parts"] = x.Parts.Select(p => (object)new Dictionary<string, object>
                {
                    ["label"] = p.Label, ["value"] = p.Value,
                    ["agents"] = p.Agents.Select(a =>
                    {
                        // The building may be gone (demolished); keep the count, drop the map link.
                        bool here = w.TryGetBuilding(a.id, out Building b);
                        return (object)new Dictionary<string, object>
                        {
                            ["id"] = a.id, ["count"] = a.count, ["name"] = here ? b.Def.Name : "撤去された建物",
                            ["x"] = here ? b.Origin.X + b.Def.Width / 2.0 : -1, ["z"] = here ? b.Origin.Z + b.Def.Depth / 2.0 : -1,
                        };
                    }).ToList(),
                }).ToList(),
            });
        }
    }
}
