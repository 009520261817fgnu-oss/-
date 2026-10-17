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
        /// <summary>
        /// Free-form numeric parameters (housing, jobs, ...). Read by systems added in later phases;
        /// unknown keys are kept so content can be authored ahead of the code.
        /// </summary>
        public readonly Dictionary<string, double> Params = new Dictionary<string, double>();

        public double GetParam(string key, double fallback = 0) => Params.TryGetValue(key, out double v) ? v : fallback;
    }

    public sealed class ResourceDef
    {
        public string Id;
        public string Name;
        public ResourceTier Tier;
        public string Unit = "t";
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
