using System;
using System.Collections.Generic;
using WorldSim.Core.Content;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Persistence
{
    /// <summary>One timeline: its own world copy and its own clock.</summary>
    public sealed class Branch
    {
        public int Id;
        public string Name;
        /// <summary>Branch this one was forked from, 0 for the original timeline.</summary>
        public int ParentId;
        /// <summary>Simulation tick at which the fork happened.</summary>
        public long ForkTick;
        public SimulationRunner Runner;
        public WorldState World => Runner.World;
    }

    /// <summary>
    /// History branching (spec 2.12 / phase 5): forks are independent deep copies made through the
    /// save format, each with its own time progression. The shared past stays in every branch's
    /// history, so branches can be compared day by day from the fork point onwards.
    /// </summary>
    public sealed class BranchManager
    {
        public const string Format = "WorldSim.Branches";
        public const int MaxBranches = 8;

        private readonly List<Branch> _branches = new List<Branch>();
        private readonly Func<WorldState, SimulationRunner> _runnerFactory;
        private int _nextId = 1;

        public BranchManager(WorldState root, Func<WorldState, SimulationRunner> runnerFactory, string name = "本線")
        {
            _runnerFactory = runnerFactory;
            Active = Add(name, 0, 0, root);
        }

        public Branch Active { get; private set; }
        public IReadOnlyList<Branch> Branches => _branches;

        public Branch Get(int id)
        {
            foreach (Branch b in _branches) if (b.Id == id) return b;
            throw new KeyNotFoundException($"分岐 #{id} はありません");
        }

        /// <summary>Copies the active branch at its current moment into a new branch (not activated).</summary>
        public Branch Fork(string name)
        {
            if (_branches.Count >= MaxBranches) throw new InvalidOperationException($"分岐は最大 {MaxBranches} 本です");
            WorldState copy = SaveSystem.Clone(Active.World);
            Branch b = Add(string.IsNullOrWhiteSpace(name) ? $"分岐{_nextId}" : name, Active.Id, Active.World.Clock.Tick, copy);
            b.Runner.Speed = SimSpeed.Paused;
            copy.Log("分岐", $"「{Active.Name}」から分岐しました");
            return b;
        }

        public void Switch(int id)
        {
            Branch target = Get(id);
            Active.Runner.Speed = SimSpeed.Paused;
            Active = target;
        }

        /// <summary>Runs a branch forward headlessly (other branches keep their own time).</summary>
        public void Advance(int id, int days) => Get(id).Runner.StepMany(days * SimClock.TicksPerDay);

        public void Remove(int id)
        {
            Branch b = Get(id);
            if (b == Active) throw new InvalidOperationException("表示中の分岐は削除できません");
            _branches.Remove(b);
        }

        /// <summary>Daily values of one series for every branch, indexed by day number.</summary>
        public SortedDictionary<int, double[]> Compare(string series)
        {
            var result = new SortedDictionary<int, double[]>();
            foreach (Branch b in _branches)
            {
                List<StatsSnapshot> h = b.World.History;
                var values = new double[h.Count];
                for (int i = 0; i < h.Count; i++) values[i] = h[i].Get(series);
                result[b.Id] = values;
            }
            return result;
        }

        public string SaveAll()
        {
            var list = new List<object>();
            foreach (Branch b in _branches)
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = b.Id, ["name"] = b.Name, ["parent"] = b.ParentId, ["forkTick"] = (double)b.ForkTick,
                    ["save"] = Json.Parse(SaveSystem.Save(b.World)),
                });
            return Json.Serialize(new Dictionary<string, object>
            {
                ["format"] = Format, ["active"] = Active.Id, ["nextId"] = _nextId, ["branches"] = list,
            });
        }

        public static BranchManager LoadAll(ContentDatabase content, string json, Func<WorldState, SimulationRunner> runnerFactory)
        {
            if (!(Json.Parse(json) is IDictionary<string, object> root) || root.GetString("format") != Format)
                throw new FormatException("分岐データではありません");
            BranchManager mgr = null;
            foreach (object o in root.GetArray("branches"))
            {
                var d = (IDictionary<string, object>)o;
                WorldState w = SaveSystem.Load(content, Json.Serialize(d["save"]));
                if (mgr == null)
                {
                    mgr = new BranchManager(w, runnerFactory, d.GetString("name"));
                    mgr._branches[0].Id = d.GetInt("id");
                    mgr._branches[0].ParentId = d.GetInt("parent");
                    mgr._branches[0].ForkTick = (long)d.GetDouble("forkTick");
                }
                else
                {
                    Branch b = mgr.Add(d.GetString("name"), d.GetInt("parent"), (long)d.GetDouble("forkTick"), w);
                    b.Id = d.GetInt("id");
                }
            }
            if (mgr == null) throw new FormatException("分岐が1本もありません");
            mgr._nextId = root.GetInt("nextId");
            mgr.Active = mgr.Get(root.GetInt("active"));
            return mgr;
        }

        private Branch Add(string name, int parent, long forkTick, WorldState world)
        {
            var b = new Branch { Id = _nextId++, Name = name, ParentId = parent, ForkTick = forkTick, Runner = _runnerFactory(world) };
            _branches.Add(b);
            return b;
        }
    }
}
