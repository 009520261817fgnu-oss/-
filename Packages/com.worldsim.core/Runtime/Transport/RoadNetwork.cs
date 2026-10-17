using System;
using System.Collections.Generic;
using WorldSim.Core.World;

namespace WorldSim.Core.Transport
{
    /// <summary>
    /// Road graph derived from the world grid (one node per usable road cell, 4-neighbour edges).
    /// Answers building-to-building travel times and routes with Dijkstra trees cached per root.
    /// Travel time per cell depends on tech (road speed) and yesterday's traffic (congestion).
    /// This is a cache: it is rebuilt from world state and never saved.
    ///
    /// Costs are whole microhours, so a trip costs exactly the same in both directions (S5-1). That
    /// lets one tree rooted at a business answer the trips of every home that commutes or shops
    /// there: about a third as many trees as one per origin. Routes are always read from the
    /// destination's tree, so they do not depend on which trees happen to be cached (save/load).
    /// </summary>
    public sealed class RoadNetwork
    {
        /// <summary>
        /// Effective door-to-door urban speed including signals, stops and parking searches. The
        /// prototype map is only ~1.3 km across, so this keeps commutes in the 5-20 minute range
        /// where routes, detours and congestion matter.
        /// </summary>
        public const float BaseSpeedKmh = 6f;
        /// <summary>Fixed time for parking, loading and walking at each end of a trip.</summary>
        public const float TripOverheadHours = 0.03f;
        /// <summary>Travel time from the map edge to the nearest outside market.</summary>
        public const float OutsideHours = 1.0f;
        /// <summary>Vehicles per day a road cell carries before congestion grows quickly.</summary>
        public const float CellCapacityPerDay = 1000f;
        /// <summary>Cost units per hour (integer costs keep sums exact and symmetric).</summary>
        public const float UnitsPerHour = 1_000_000f;
        private const int Unreachable = int.MaxValue;

        private readonly WorldState _w;
        private bool _dirty = true;
        private int[] _nodeOfCell;
        private int[] _cellOfNode = Array.Empty<int>();
        private int[] _nodeCost = Array.Empty<int>();
        private int[][] _adj = Array.Empty<int[]>();
        private readonly Dictionary<int, int[]> _access = new Dictionary<int, int[]>();
        private readonly Dictionary<int, Tree> _trees = new Dictionary<int, Tree>();
        private Tree _outside;
        private readonly Stack<Tree> _spare = new Stack<Tree>();
        private MinHeap _heap;

        public RoadNetwork(WorldState world) { _w = world; }

        /// <summary>Increments whenever travel times may have changed.</summary>
        public int Version { get; private set; }
        public int NodeCount { get { EnsureBuilt(); return _cellOfNode.Length; } }
        /// <summary>Diagnostics for benchmarks: shortest-path trees computed and full rebuilds so far.</summary>
        public long TreesComputed { get; private set; }
        public long Rebuilds { get; private set; }

        /// <summary>Road topology, speeds or congestion changed.</summary>
        public void Invalidate()
        {
            _dirty = true;
            Version++;
        }

        /// <summary>A building was added or removed; only its access points change.</summary>
        public void InvalidateBuilding(int buildingId)
        {
            _access.Remove(buildingId);
            if (_trees.TryGetValue(buildingId, out Tree old)) { _spare.Push(old); _trees.Remove(buildingId); }
            Version++;
        }

        public bool HasOutsideConnection
        {
            get
            {
                EnsureBuilt();
                foreach (int d in _outside.Dist) if (d != Unreachable) return true;
                return false;
            }
        }

        /// <summary>Door-to-door hours between two buildings, +∞ when unreachable.</summary>
        public float TravelHours(int fromBuilding, int toBuilding)
        {
            if (fromBuilding == toBuilding) return 0f;
            EnsureBuilt();
            int[] a = Access(fromBuilding), b = Access(toBuilding);
            if (a.Length == 0 || b.Length == 0) return float.PositiveInfinity;
            // Symmetric costs: any tree rooted at either end gives the same answer. Prefer one that is
            // already built, then the end that is a business (fewer of those than homes).
            Tree t;
            int[] other;
            if (_trees.TryGetValue(toBuilding, out t)) other = a;
            else if (_trees.TryGetValue(fromBuilding, out t)) other = b;
            else if (IsBusiness(toBuilding) && !IsBusiness(fromBuilding)) { t = TreeFrom(toBuilding); other = a; }
            else { t = TreeFrom(fromBuilding); other = b; }
            int best = Nearest(t, other, out _);
            return best == Unreachable ? float.PositiveInfinity : best / UnitsPerHour + TripOverheadHours;
        }

        /// <summary>
        /// TravelHours(from, targets[i]) for many targets at once, read from the targets' trees (the
        /// values are identical to single calls; this only skips the per-call lookups).
        /// </summary>
        public void TravelHoursMany(int fromBuilding, IReadOnlyList<int> targets, float[] result)
        {
            EnsureBuilt();
            int[] a = Access(fromBuilding);
            for (int i = 0; i < targets.Count; i++)
            {
                int to = targets[i];
                if (to == fromBuilding) { result[i] = 0f; continue; }
                int best = a.Length == 0 ? Unreachable : Nearest(TreeFrom(to), a, out _);
                result[i] = best == Unreachable ? float.PositiveInfinity : best / UnitsPerHour + TripOverheadHours;
            }
        }

        /// <summary>Hours from a building to the outside market via the nearest map-edge road.</summary>
        public float HoursToOutside(int building)
        {
            EnsureBuilt();
            int best = Nearest(_outside, Access(building), out _);
            return best == Unreachable ? float.PositiveInfinity : best / UnitsPerHour + TripOverheadHours + OutsideHours;
        }

        /// <summary>Road cells (as world cell indices) along the fastest route, empty if unreachable.</summary>
        public List<int> RouteCells(int fromBuilding, int toBuilding)
        {
            var cells = new List<int>();
            EnsureBuilt();
            // Walk the destination's tree from the origin's nearest access point towards its root.
            Tree t = TreeFrom(toBuilding);
            int[] src = Access(fromBuilding);
            if (t == null || src.Length == 0 || Nearest(t, src, out int start) == Unreachable) return cells;
            for (int n = start; n >= 0; n = t.Parent[n]) cells.Add(_cellOfNode[n]);
            return cells;
        }

        /// <summary>Adds <paramref name="vehicles"/> to every road cell on the fastest route (to 0 = the outside).</summary>
        public void AddRouteVolume(int fromBuilding, int toBuilding, float vehicles, float[] volume)
        {
            EnsureBuilt();
            Tree t = toBuilding == 0 ? _outside : TreeFrom(toBuilding);
            if (Nearest(t, Access(fromBuilding), out int start) == Unreachable) return;
            for (int n = start; n >= 0; n = t.Parent[n]) volume[_cellOfNode[n]] += vehicles;
        }

        /// <summary>Road cells from the building to the map edge, empty if not connected.</summary>
        public List<int> RouteCellsToOutside(int building)
        {
            var cells = new List<int>();
            EnsureBuilt();
            if (Nearest(_outside, Access(building), out int start) == Unreachable) return cells;
            for (int n = start; n >= 0; n = _outside.Parent[n]) cells.Add(_cellOfNode[n]);
            return cells;
        }

        public bool HasAccess(int building) { EnsureBuilt(); return Access(building).Length > 0; }

        /// <summary>Cost of passing a road cell in cost units (see UnitsPerHour), -1 if it is not a usable road.</summary>
        public int CellCost(int cell)
        {
            EnsureBuilt();
            int n = _nodeOfCell[cell];
            return n < 0 ? -1 : _nodeCost[n];
        }

        private bool IsBusiness(int building) => _w.Economy.Companies.ContainsKey(building);

        /// <summary>Smallest tree distance among the nodes (first such node in order), or Unreachable.</summary>
        private static int Nearest(Tree t, int[] nodes, out int node)
        {
            node = -1;
            int best = Unreachable;
            if (t == null) return best;
            foreach (int n in nodes) if (t.Dist[n] < best) { best = t.Dist[n]; node = n; }
            return best;
        }

        private int[] Access(int building)
        {
            if (_access.TryGetValue(building, out int[] a)) return a;
            var list = new List<int>();
            if (_w.TryGetBuilding(building, out Building b))
            {
                GridRect r = b.Footprint;
                for (int x = r.MinX; x < r.MaxX; x++) { AddNode(list, x, r.MinZ - 1); AddNode(list, x, r.MaxZ); }
                for (int z = r.MinZ; z < r.MaxZ; z++) { AddNode(list, r.MinX - 1, z); AddNode(list, r.MaxX, z); }
            }
            a = list.ToArray();
            _access[building] = a;
            return a;
        }

        private void AddNode(List<int> list, int x, int z)
        {
            if (x < 0 || z < 0 || x >= _w.Width || z >= _w.Depth) return;
            int n = _nodeOfCell[z * _w.Width + x];
            if (n >= 0 && !list.Contains(n)) list.Add(n);
        }

        private Tree TreeFrom(int building)
        {
            if (_trees.TryGetValue(building, out Tree t)) return t;
            int[] src = Access(building);
            if (src.Length == 0) return null;
            t = Dijkstra(src);
            _trees[building] = t;
            return t;
        }

        private void EnsureBuilt()
        {
            if (!_dirty) return;
            _dirty = false;
            Rebuilds++;
            _access.Clear();
            foreach (Tree old in _trees.Values) _spare.Push(old);
            _trees.Clear();

            int w = _w.Width, d = _w.Depth;
            _nodeOfCell = new int[w * d];
            var cells = new List<int>();
            for (int i = 0; i < _nodeOfCell.Length; i++)
            {
                var c = new GridPos(i % w, i / w);
                if (_w.IsRoadUsable(c)) { _nodeOfCell[i] = cells.Count; cells.Add(i); }
                else _nodeOfCell[i] = -1;
            }
            _cellOfNode = cells.ToArray();
            if (_spare.Count > 0 && _spare.Peek().Dist.Length != _cellOfNode.Length) _spare.Clear();

            float speed = BaseSpeedKmh * (float)(1 + _w.Effects.Global("roadSpeed"));
            float baseHours = _w.Terrain.CellSize / 1000f / Math.Max(1f, speed);
            float[] traffic = _w.CongestionBasis;
            _nodeCost = new int[_cellOfNode.Length];
            _adj = new int[_cellOfNode.Length][];
            var nb = new List<int>(4);
            for (int n = 0; n < _cellOfNode.Length; n++)
            {
                int cell = _cellOfNode[n];
                float ratio = traffic[cell] / CellCapacityPerDay;
                _nodeCost[n] = Math.Max(1, (int)Math.Round(baseHours * Math.Min(10f, 1f + 0.15f * ratio * ratio * ratio * ratio) * UnitsPerHour));
                int x = cell % w, z = cell / w;
                nb.Clear();
                if (x > 0 && _nodeOfCell[cell - 1] >= 0) nb.Add(_nodeOfCell[cell - 1]);
                if (x < w - 1 && _nodeOfCell[cell + 1] >= 0) nb.Add(_nodeOfCell[cell + 1]);
                if (z > 0 && _nodeOfCell[cell - w] >= 0) nb.Add(_nodeOfCell[cell - w]);
                if (z < d - 1 && _nodeOfCell[cell + w] >= 0) nb.Add(_nodeOfCell[cell + w]);
                _adj[n] = nb.ToArray();
            }

            var edges = new List<int>();
            for (int n = 0; n < _cellOfNode.Length; n++)
            {
                int cell = _cellOfNode[n], x = cell % w, z = cell / w;
                if (x == 0 || z == 0 || x == w - 1 || z == d - 1) edges.Add(n);
            }
            _outside = Dijkstra(edges.ToArray());
        }

        private Tree Dijkstra(int[] sources)
        {
            TreesComputed++;
            int n = _cellOfNode.Length;
            Tree t = _spare.Count > 0 ? _spare.Pop() : new Tree(n);
            t.Reset();
            MinHeap heap = _heap ??= new MinHeap(Math.Max(16, n));
            heap.Clear();
            foreach (int s in sources)
            {
                if (_nodeCost[s] < t.Dist[s])
                {
                    t.Dist[s] = _nodeCost[s];
                    heap.Push(t.Dist[s], s);
                }
            }
            // Most road cells lie inside a straight or curved run with exactly two neighbours. Instead of
            // queueing each of them, walk the run from a settled node until it reaches a junction or a
            // dead end (queued as usual) or stops improving: along a run, distances from this side rise
            // and those from the other side fall, so once a cell is not improved none further are.
            // The distances are exactly those of plain Dijkstra; only ties between equal routes may
            // pick a different (still deterministic) parent.
            while (heap.Count > 0)
            {
                heap.Pop(out int du, out int u);
                if (du > t.Dist[u]) continue;
                foreach (int first in _adj[u])
                {
                    int prev = u, v = first, d = du;
                    while (true)
                    {
                        int nd = d + _nodeCost[v];
                        if (nd >= t.Dist[v]) break;
                        t.Dist[v] = nd;
                        t.Parent[v] = prev;
                        int[] nv = _adj[v];
                        if (nv.Length != 2) { heap.Push(nd, v); break; }
                        int next = nv[0] == prev ? nv[1] : nv[0];
                        prev = v; v = next; d = nd;
                    }
                }
            }
            return t;
        }

        private sealed class Tree
        {
            public readonly int[] Dist;
            public readonly int[] Parent;

            public Tree(int n)
            {
                Dist = new int[n];
                Parent = new int[n];
            }

            public void Reset()
            {
                Array.Fill(Dist, Unreachable);
                Array.Fill(Parent, -1);
            }
        }
    }

    /// <summary>Binary min-heap of (priority, value); netstandard2.1 has no PriorityQueue.</summary>
    internal sealed class MinHeap
    {
        private int[] _keys;
        private int[] _vals;
        public int Count { get; private set; }
        public void Clear() => Count = 0;

        public MinHeap(int capacity)
        {
            _keys = new int[capacity];
            _vals = new int[capacity];
        }

        public void Push(int key, int val)
        {
            if (Count == _keys.Length)
            {
                Array.Resize(ref _keys, Count * 2);
                Array.Resize(ref _vals, Count * 2);
            }
            int i = Count++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_keys[p] <= key) break;
                _keys[i] = _keys[p];
                _vals[i] = _vals[p];
                i = p;
            }
            _keys[i] = key;
            _vals[i] = val;
        }

        public void Pop(out int key, out int val)
        {
            key = _keys[0];
            val = _vals[0];
            Count--;
            int k = _keys[Count];
            int v = _vals[Count];
            int i = 0;
            while (true)
            {
                int c = 2 * i + 1;
                if (c >= Count) break;
                if (c + 1 < Count && _keys[c + 1] < _keys[c]) c++;
                if (_keys[c] >= k) break;
                _keys[i] = _keys[c];
                _vals[i] = _vals[c];
                i = c;
            }
            _keys[i] = k;
            _vals[i] = v;
        }
    }
}
