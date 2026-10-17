using System;
using System.Collections.Generic;
using System.Globalization;

namespace WorldSim.Core.Content
{
    /// <summary>
    /// All static definitions, loaded from external JSON. Each JSON document may contain any of the
    /// top-level arrays "buildings", "resources", "recipes" and "techs"; later documents may add new
    /// ids but not redefine existing ones (so a mod cannot silently replace base content).
    /// Cross-references are checked by <see cref="Validate"/>.
    /// </summary>
    public sealed class ContentDatabase
    {
        private readonly Registry<BuildingDef> _buildings = new Registry<BuildingDef>("building", d => d.Id);
        private readonly Registry<ResourceDef> _resources = new Registry<ResourceDef>("resource", d => d.Id);
        private readonly Registry<RecipeDef> _recipes = new Registry<RecipeDef>("recipe", d => d.Id);
        private readonly Registry<TechDef> _techs = new Registry<TechDef>("tech", d => d.Id);
        private readonly Registry<InteriorDef> _interiors = new Registry<InteriorDef>("interior", d => d.Id);

        public IEnumerable<BuildingDef> Buildings => _buildings.All;
        public IEnumerable<ResourceDef> Resources => _resources.All;
        public IEnumerable<RecipeDef> Recipes => _recipes.All;
        public IEnumerable<TechDef> Techs => _techs.All;
        public IEnumerable<InteriorDef> Interiors => _interiors.All;

        /// <summary>Interior for a building type: a definition naming the building id wins over one naming its category.</summary>
        public InteriorDef InteriorFor(BuildingDef b)
        {
            InteriorDef byCategory = null;
            foreach (InteriorDef i in _interiors.All)
            {
                if (Array.IndexOf(i.For, b.Id) >= 0) return i;
                if (byCategory == null && Array.IndexOf(i.For, b.Category.ToString()) >= 0) byCategory = i;
            }
            return byCategory;
        }

        public bool TryGetBuilding(string id, out BuildingDef def) => _buildings.TryGet(id, out def);
        public BuildingDef GetBuilding(string id) => _buildings.Get(id);
        public bool TryGetResource(string id, out ResourceDef def) => _resources.TryGet(id, out def);
        public ResourceDef GetResource(string id) => _resources.Get(id);
        public bool TryGetRecipe(string id, out RecipeDef def) => _recipes.TryGet(id, out def);
        public RecipeDef GetRecipe(string id) => _recipes.Get(id);
        public bool TryGetTech(string id, out TechDef def) => _techs.TryGet(id, out def);
        public TechDef GetTech(string id) => _techs.Get(id);

        /// <summary>Loads documents in order and validates cross-references.</summary>
        public static ContentDatabase FromJson(params string[] documents)
        {
            var db = new ContentDatabase();
            foreach (string doc in documents) db.LoadJson(doc);
            db.Validate();
            return db;
        }

        public void LoadJson(string text)
        {
            if (!(Json.Parse(text) is IDictionary<string, object> root))
                throw new FormatException("Content document must be a JSON object");
            foreach (var o in Items(root, "resources")) _resources.Add(ParseResource(o));
            foreach (var o in Items(root, "recipes")) _recipes.Add(ParseRecipe(o));
            foreach (var o in Items(root, "buildings")) _buildings.Add(ParseBuilding(o));
            foreach (var o in Items(root, "techs")) _techs.Add(ParseTech(o));
            foreach (var o in Items(root, "interiors")) _interiors.Add(ParseInterior(o));
        }

        public void AddBuilding(BuildingDef def) => _buildings.Add(def);
        public void AddResource(ResourceDef def) => _resources.Add(def);

        /// <summary>Throws FormatException if any definition refers to an undefined id.</summary>
        public void Validate()
        {
            foreach (RecipeDef r in _recipes.All)
            {
                foreach (string res in r.Inputs.Keys) RequireResource(res, $"recipe '{r.Id}'");
                foreach (string res in r.OptionalInputs.Keys) RequireResource(res, $"recipe '{r.Id}'");
                foreach (string res in r.Outputs.Keys) RequireResource(res, $"recipe '{r.Id}'");
            }
            foreach (BuildingDef b in _buildings.All)
            {
                if (b.Recipe != null && !_recipes.TryGet(b.Recipe, out _))
                    throw new FormatException($"building '{b.Id}': unknown recipe '{b.Recipe}'");
                foreach (string res in b.Sells) RequireResource(res, $"building '{b.Id}'");
                if (b.RequiresTech != null && !_techs.TryGet(b.RequiresTech, out _))
                    throw new FormatException($"building '{b.Id}': unknown tech '{b.RequiresTech}'");
            }
            foreach (TechDef t in _techs.All)
            {
                foreach (string p in t.Prereqs)
                    if (!_techs.TryGet(p, out _)) throw new FormatException($"tech '{t.Id}': unknown prereq '{p}'");
                foreach (string u in t.Unlocks)
                    if (!_buildings.TryGet(u, out _)) throw new FormatException($"tech '{t.Id}': unknown building '{u}'");
            }
        }

        private void RequireResource(string id, string ctx)
        {
            if (!_resources.TryGet(id, out _)) throw new FormatException($"{ctx}: unknown resource '{id}'");
        }

        private static IEnumerable<IDictionary<string, object>> Items(IDictionary<string, object> root, string key)
        {
            IList<object> list = root.GetArray(key);
            if (list == null) yield break;
            foreach (object item in list)
                yield return item as IDictionary<string, object> ?? throw new FormatException($"{key}: entries must be objects");
        }

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
                Cost = (long)o.GetDouble("cost", 0),
                Recipe = o.GetString("recipe"),
                Sells = StringArray(o, "sells", ctx),
                RequiresTech = o.GetString("requiresTech"),
            };
            if (def.Width < 1 || def.Depth < 1 || def.Width > 32 || def.Depth > 32)
                throw new FormatException($"{ctx}: footprint must be 1..32 cells");
            if (def.Height <= 0) throw new FormatException($"{ctx}: height must be positive");
            if (def.Cost < 0) throw new FormatException($"{ctx}: cost must not be negative");
            ReadNumbers(o.GetObject("params"), def.Params, ctx);
            return def;
        }

        private static ResourceDef ParseResource(IDictionary<string, object> o)
        {
            string id = o.RequireString("id", "resource");
            var def = new ResourceDef
            {
                Id = id,
                Name = o.GetString("name", id),
                Tier = EnumParse.Parse<ResourceTier>(o.GetString("tier"), $"resource '{id}'"),
                Unit = o.GetString("unit", "t"),
                BasePrice = o.GetDouble("price", 100),
            };
            if (def.BasePrice <= 0) throw new FormatException($"resource '{id}': price must be positive");
            return def;
        }

        private static RecipeDef ParseRecipe(IDictionary<string, object> o)
        {
            string id = o.RequireString("id", "recipe");
            var def = new RecipeDef { Id = id };
            ReadNumbers(o.GetObject("inputs"), def.Inputs, $"recipe '{id}'");
            ReadNumbers(o.GetObject("optionalInputs"), def.OptionalInputs, $"recipe '{id}'");
            ReadNumbers(o.GetObject("outputs"), def.Outputs, $"recipe '{id}'");
            if (def.Outputs.Count == 0) throw new FormatException($"recipe '{id}': needs at least one output");
            return def;
        }

        private static TechDef ParseTech(IDictionary<string, object> o)
        {
            string id = o.RequireString("id", "tech");
            string ctx = $"tech '{id}'";
            var def = new TechDef
            {
                Id = id,
                Name = o.GetString("name", id),
                Description = o.GetString("description", ""),
                Cost = o.GetDouble("cost", 100),
                Prereqs = StringArray(o, "prereqs", ctx),
                Unlocks = StringArray(o, "unlocks", ctx),
                AdoptionCost = (long)o.GetDouble("adoptionCost", 0),
            };
            ReadNumbers(o.GetObject("effects"), def.Effects, ctx);
            if (def.Cost <= 0) throw new FormatException($"{ctx}: cost must be positive");
            return def;
        }

        private static readonly string[] FixtureTypes = { "bed", "fridge", "sofa", "counter", "workstation", "jobboard", "classroom", "treatment" };

        private static InteriorDef ParseInterior(IDictionary<string, object> o)
        {
            string id = o.RequireString("id", "interior");
            string ctx = $"interior '{id}'";
            var def = new InteriorDef
            {
                Id = id,
                For = StringArray(o, "for", ctx),
                Width = (float)o.GetDouble("width", 10),
                Depth = (float)o.GetDouble("depth", 8),
            };
            IList<object> door = o.GetArray("door");
            if (door != null && door.Count == 2) { def.DoorX = (float)(double)door[0]; def.DoorZ = (float)(double)door[1]; }
            else def.DoorX = def.Width / 2;
            if (def.Width < 3 || def.Depth < 3 || def.Width > 60 || def.Depth > 60) throw new FormatException($"{ctx}: room must be 3..60 m");
            IList<object> fixtures = o.GetArray("fixtures");
            if (fixtures != null)
                foreach (object f in fixtures)
                {
                    var fo = f as IDictionary<string, object> ?? throw new FormatException($"{ctx}: fixtures must be objects");
                    var fx = new FixtureDef
                    {
                        Type = fo.RequireString("type", ctx), Label = fo.GetString("label"),
                        X = (float)fo.GetDouble("x"), Z = (float)fo.GetDouble("z"),
                        W = (float)fo.GetDouble("w", 1), D = (float)fo.GetDouble("d", 1),
                    };
                    if (Array.IndexOf(FixtureTypes, fx.Type) < 0) throw new FormatException($"{ctx}: unknown fixture type '{fx.Type}'");
                    if (fx.X < 0 || fx.Z < 0 || fx.X + fx.W > def.Width || fx.Z + fx.D > def.Depth) throw new FormatException($"{ctx}: fixture '{fx.Type}' outside the room");
                    def.Fixtures.Add(fx);
                }
            return def;
        }

        private static void ReadNumbers(IDictionary<string, object> src, Dictionary<string, double> dst, string ctx)
        {
            if (src == null) return;
            foreach (var kv in src)
                if (kv.Value is double d) dst[kv.Key] = d;
                else throw new FormatException($"{ctx}: '{kv.Key}' must be a number");
        }

        private static string[] StringArray(IDictionary<string, object> o, string key, string ctx)
        {
            IList<object> list = o.GetArray(key);
            if (list == null) return Array.Empty<string>();
            var result = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                result[i] = list[i] as string ?? throw new FormatException($"{ctx}: '{key}' must contain strings");
            return result;
        }

        private static int ParseColor(string s, string ctx)
        {
            if (s != null && s.Length == 7 && s[0] == '#' &&
                int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                return rgb;
            throw new FormatException($"{ctx}: color must be '#RRGGBB'");
        }

        private sealed class Registry<T> where T : class
        {
            private readonly string _kind;
            private readonly Func<T, string> _id;
            private readonly Dictionary<string, T> _byId = new Dictionary<string, T>();
            private readonly List<T> _ordered = new List<T>();

            public Registry(string kind, Func<T, string> id) { _kind = kind; _id = id; }
            public IEnumerable<T> All => _ordered;
            public bool TryGet(string id, out T def) => _byId.TryGetValue(id ?? "", out def);
            public T Get(string id) => TryGet(id, out T d) ? d : throw new KeyNotFoundException($"Unknown {_kind} '{id}'");

            public void Add(T def)
            {
                string id = _id(def);
                if (_byId.ContainsKey(id)) throw new FormatException($"Duplicate {_kind} id '{id}'");
                _byId.Add(id, def);
                _ordered.Add(def);
            }
        }
    }
}
