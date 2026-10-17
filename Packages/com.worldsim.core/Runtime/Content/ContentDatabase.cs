using System;
using System.Collections.Generic;
using System.Globalization;

namespace WorldSim.Core.Content
{
    /// <summary>
    /// All static definitions, loaded from external JSON. Each JSON document may contain any of the
    /// top-level arrays "buildings" and "resources"; later documents may add new ids but not redefine
    /// existing ones (so a mod cannot silently replace base content).
    /// </summary>
    public sealed class ContentDatabase
    {
        private readonly Dictionary<string, BuildingDef> _buildings = new Dictionary<string, BuildingDef>();
        private readonly Dictionary<string, ResourceDef> _resources = new Dictionary<string, ResourceDef>();
        private readonly List<string> _buildingOrder = new List<string>();
        private readonly List<string> _resourceOrder = new List<string>();

        public IEnumerable<BuildingDef> Buildings
        {
            get { foreach (string id in _buildingOrder) yield return _buildings[id]; }
        }

        public IEnumerable<ResourceDef> Resources
        {
            get { foreach (string id in _resourceOrder) yield return _resources[id]; }
        }

        public bool TryGetBuilding(string id, out BuildingDef def) => _buildings.TryGetValue(id, out def);

        public BuildingDef GetBuilding(string id) =>
            _buildings.TryGetValue(id, out BuildingDef def) ? def : throw new KeyNotFoundException($"Unknown building '{id}'");

        public bool TryGetResource(string id, out ResourceDef def) => _resources.TryGetValue(id, out def);

        public static ContentDatabase FromJson(params string[] documents)
        {
            var db = new ContentDatabase();
            foreach (string doc in documents) db.LoadJson(doc);
            return db;
        }

        public void LoadJson(string text)
        {
            if (!(Json.Parse(text) is IDictionary<string, object> root))
                throw new FormatException("Content document must be a JSON object");

            IList<object> buildings = root.GetArray("buildings");
            if (buildings != null)
                foreach (object item in buildings) AddBuilding(ParseBuilding(AsObject(item, "buildings")));

            IList<object> resources = root.GetArray("resources");
            if (resources != null)
                foreach (object item in resources) AddResource(ParseResource(AsObject(item, "resources")));
        }

        public void AddBuilding(BuildingDef def)
        {
            if (_buildings.ContainsKey(def.Id)) throw new FormatException($"Duplicate building id '{def.Id}'");
            _buildings.Add(def.Id, def);
            _buildingOrder.Add(def.Id);
        }

        public void AddResource(ResourceDef def)
        {
            if (_resources.ContainsKey(def.Id)) throw new FormatException($"Duplicate resource id '{def.Id}'");
            _resources.Add(def.Id, def);
            _resourceOrder.Add(def.Id);
        }

        private static IDictionary<string, object> AsObject(object item, string context) =>
            item as IDictionary<string, object> ?? throw new FormatException($"{context}: entries must be objects");

        private static BuildingDef ParseBuilding(IDictionary<string, object> o)
        {
            string id = o.RequireString("id", "building");
            string ctx = $"building '{id}'";
            var def = new BuildingDef
            {
                Id = id,
                Name = o.GetString("name", id),
                Category = EnumParse.Parse<BuildingCategory>(o.GetString("category"), ctx),
                Width = o.GetInt("width", 1),
                Depth = o.GetInt("depth", 1),
                Height = (float)o.GetDouble("height", 4),
                MaxSlope = (float)o.GetDouble("maxSlope", 2.5),
                Color = ParseColor(o.GetString("color", "#CCCCCC"), ctx),
            };
            if (def.Width < 1 || def.Depth < 1 || def.Width > 32 || def.Depth > 32)
                throw new FormatException($"{ctx}: footprint must be 1..32 cells");
            if (def.Height <= 0) throw new FormatException($"{ctx}: height must be positive");

            IDictionary<string, object> ps = o.GetObject("params");
            if (ps != null)
                foreach (var kv in ps)
                    if (kv.Value is double d) def.Params[kv.Key] = d;
                    else throw new FormatException($"{ctx}: param '{kv.Key}' must be a number");
            return def;
        }

        private static ResourceDef ParseResource(IDictionary<string, object> o)
        {
            string id = o.RequireString("id", "resource");
            return new ResourceDef
            {
                Id = id,
                Name = o.GetString("name", id),
                Tier = EnumParse.Parse<ResourceTier>(o.GetString("tier"), $"resource '{id}'"),
                Unit = o.GetString("unit", "t"),
            };
        }

        private static int ParseColor(string s, string ctx)
        {
            if (s != null && s.Length == 7 && s[0] == '#' &&
                int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                return rgb;
            throw new FormatException($"{ctx}: color must be '#RRGGBB'");
        }
    }
}
