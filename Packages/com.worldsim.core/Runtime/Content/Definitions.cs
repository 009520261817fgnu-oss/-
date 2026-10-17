using System;
using System.Collections.Generic;

namespace WorldSim.Core.Content
{
    public enum BuildingCategory
    {
        Residential,
        Commercial,
        Industrial,
        Agriculture,
        Power,
        Public,
    }

    public enum ResourceTier
    {
        Raw,
        Intermediate,
        Product,
    }

    /// <summary>Static definition of a building type. Play-state instances reference it by Id.</summary>
    public sealed class BuildingDef
    {
        public string Id;
        public string Name;
        public BuildingCategory Category;
        public int Width = 1;
        public int Depth = 1;
        public float Height = 4f;
        /// <summary>Largest allowed terrain height difference inside the footprint, in metres.</summary>
        public float MaxSlope = 2.5f;
        /// <summary>sRGB colour as 0xRRGGBB, used by presentation only.</summary>
        public int Color = 0xCCCCCC;
        /// <summary>Construction cost in yen, paid from the city treasury.</summary>
        public long Cost;
        /// <summary>Recipe run by the company in this building, or null.</summary>
        public string Recipe;
        /// <summary>Resources this building sells to residents (retail), empty if none.</summary>
        public string[] Sells = Array.Empty<string>();
        /// <summary>Technology that must be researched before this can be built, or null.</summary>
        public string RequiresTech;
        /// <summary>
        /// Numeric parameters: housing, jobs, energy, pollution, powerOutput, research, students,
        /// repairCrews, logistics, markup, maintenance. Unknown keys are kept for future systems.
        /// </summary>
        public readonly Dictionary<string, double> Params = new Dictionary<string, double>();

        public double GetParam(string key, double fallback = 0) => Params.TryGetValue(key, out double v) ? v : fallback;

        /// <summary>
        /// Parameters named "prefix.x" as x → value, in a fixed order (e.g. "material.steel": tonnes of
        /// steel per million yen of construction work).
        /// </summary>
        public SortedDictionary<string, double> ParamsWithPrefix(string prefix)
        {
            var r = new SortedDictionary<string, double>(StringComparer.Ordinal);
            foreach (var kv in Params)
                if (kv.Key.StartsWith(prefix + ".", StringComparison.Ordinal)) r[kv.Key.Substring(prefix.Length + 1)] = kv.Value;
            return r;
        }

        public int Housing => (int)GetParam("housing");
        public int Jobs => (int)GetParam("jobs");
        public bool IsPublic => Category == BuildingCategory.Public;
        /// <summary>Privately run businesses: every non-public building that employs people.</summary>
        public bool IsCompany => !IsPublic && Jobs > 0;
    }

    public sealed class ResourceDef
    {
        public string Id;
        public string Name;
        public ResourceTier Tier;
        public string Unit = "t";
        /// <summary>Reference wholesale price in yen per unit; market prices float around it.</summary>
        public double BasePrice = 100;
        /// <summary>Freight cost in yen per unit per hour of travel.</summary>
        public double TransportRate => BasePrice * 0.05;
    }

    /// <summary>Production per worker-day: consumes Inputs, yields Outputs (quantities per worker per day).</summary>
    public sealed class RecipeDef
    {
        public string Id;
        public readonly Dictionary<string, double> Inputs = new Dictionary<string, double>();
        /// <summary>
        /// Inputs that help but are not essential (e.g. business services): bought when available;
        /// without them a batch is <see cref="OptionalShortfallEfficiency"/> as productive.
        /// </summary>
        public readonly Dictionary<string, double> OptionalInputs = new Dictionary<string, double>();
        public readonly Dictionary<string, double> Outputs = new Dictionary<string, double>();
        public const double OptionalShortfallEfficiency = 0.9;
    }

    public sealed class TechDef
    {
        public string Id;
        public string Name;
        public string Description = "";
        /// <summary>Research points required.</summary>
        public double Cost;
        public string[] Prereqs = Array.Empty<string>();
        public string[] Unlocks = Array.Empty<string>();
        /// <summary>
        /// Effects as "kind:Category" or "kind" keys, e.g. productivity:Industrial = 0.4 (+40%),
        /// jobs:Industrial = -0.25, energy:*, pollution:*, roadSpeed, transportCost, disasterResilience.
        /// </summary>
        public readonly Dictionary<string, double> Effects = new Dictionary<string, double>();
        /// <summary>
        /// Yen each affected company pays to adopt. 0 = applies city-wide immediately when researched.
        /// </summary>
        public long AdoptionCost;

        public bool AffectsCategory(BuildingCategory c)
        {
            string suffix = ":" + c;
            foreach (string key in Effects.Keys)
                if (key.EndsWith(suffix, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>A usable object inside a room (rectangle in room metres).</summary>
    public sealed class FixtureDef
    {
        /// <summary>bed, fridge, sofa, counter, workstation, jobboard, classroom.</summary>
        public string Type;
        public string Label;
        public float X, Z, W = 1, D = 1;
    }

    /// <summary>
    /// The inside of a building type: one room with a door and fixtures. Matched by building id
    /// first, then by category. Stage 1 of building interiors (later: modular rooms).
    /// </summary>
    public sealed class InteriorDef
    {
        public string Id;
        public string[] For = Array.Empty<string>();
        public float Width = 10, Depth = 8;
        public float DoorX = 5, DoorZ;
        public readonly List<FixtureDef> Fixtures = new List<FixtureDef>();
    }

    internal static class EnumParse
    {
        public static T Parse<T>(string value, string context) where T : struct
        {
            if (value != null && Enum.TryParse(value, true, out T result) && Enum.IsDefined(typeof(T), result)) return result;
            throw new FormatException($"{context}: invalid {typeof(T).Name} '{value}'");
        }
    }
}
