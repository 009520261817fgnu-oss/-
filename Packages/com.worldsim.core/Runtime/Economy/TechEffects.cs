using WorldSim.Core.Content;
using WorldSim.Core.World;

namespace WorldSim.Core.Economy
{
    /// <summary>
    /// Resolves technology effects. City-wide techs (AdoptionCost = 0) apply once researched;
    /// others apply per company only after that company has adopted them.
    /// </summary>
    public sealed class TechEffects
    {
        private readonly WorldState _w;
        public TechEffects(WorldState w) { _w = w; }

        public bool IsResearched(string techId) => _w.Economy.City.Research.Completed.Contains(techId);

        /// <summary>Sum of a city-wide effect key over researched techs.</summary>
        public double Global(string key)
        {
            double sum = 0;
            foreach (string id in _w.Economy.City.Research.Completed)
            {
                TechDef t = _w.Content.GetTech(id);
                if (t.AdoptionCost == 0 && t.Effects.TryGetValue(key, out double v)) sum += v;
            }
            return sum;
        }

        /// <summary>Sum of "kind:Category" effects from techs this company has adopted.</summary>
        public double ForCompany(Company c, string kind)
        {
            string key = kind + ":" + c.Def.Category;
            double sum = 0;
            foreach (string id in c.AdoptedTechs)
                if (_w.Content.GetTech(id).Effects.TryGetValue(key, out double v)) sum += v;
            return sum;
        }

        /// <summary>Job slots after automation-type effects; at least 1.</summary>
        public int JobCapacity(Company c)
        {
            double jobs = c.Def.Jobs * (1 + ForCompany(c, "jobs"));
            return System.Math.Max(1, (int)System.Math.Round(jobs));
        }
    }
}
