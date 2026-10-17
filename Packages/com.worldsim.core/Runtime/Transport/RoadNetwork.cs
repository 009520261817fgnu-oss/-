using System;
using System.Collections.Generic;
using WorldSim.Core.World;

namespace WorldSim.Core.Transport
{
    /// <summary>
    /// Road graph derived from the world grid (one node per usable road cell, 4-neighbour edges).
    /// Answers building-to-building travel times and routes with Dijkstra trees cached per origin.
    /// Travel time per cell depends on tech (road speed) and yesterday's traffic (congestion).
    /// This is a cache: it is rebuilt from world state and never saved.
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

        private readonly WorldState _w;
        private bool _dirty = true;
        private int[] _nodeOfCell;
        private int[] _cellOfNode = Array.Empty<int>();
        private float[] _nodeHours = Array.Empty<float>();
        private int[][] _adj = Array.Empty<int[]>();
        private readonly Dictionary<int, int[]> _access = new Dictionary<int, int[]>();
        private readonly Dictionary<int, Tree> _trees = new Dictionary<int, Tree>();
        private Tree _outside;

        public RoadNetwork(WorldState world) { _w = world; }

        /// <summary>Increments whenever travel times may have changed.</summary>
        public int Version { get; private set; }
        public int NodeCount { get { EnsureBuilt(); return _cellOfNode.Length; } }

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
            _trees.Remove(buildingId);
            Version++;
        }

        public bool HasOutsideConnection
        {
            get
            {
                EnsureBuilt();
                foreach (float d in _outside.Dist) if (d < float.PositiveInfinity) return true;
                return false;
            }
        }

        /// <summary>Door-to-door hours between two buildings, +∞ when unreachable.</summary>
        public float TravelHours(int fromBuilding, int toBuilding)
        {
            if (fromBuilding == toBuilding) return 0f;
            EnsureBuilt();
            int[] dst = Access(toBuilding);
            if (dst.Length == 0) return float.PositiveInfinity;
            Tree t = TreeFrom(fromBuilding);
            if (t == null) return float.PositiveInfinity;
            float best = float.PositiveInfinity;
            foreach (int n in dst) if (t.Dist[n] < best) best = t.Dist[n];
            return best + TripOverheadHours;
        }

        /// <summary>Hours from a building to the outside market via the nearest map-edge road.</summary>
        public float HoursToOutside(int building)
        {
            EnsureBuilt();
            float best = float.PositiveInfinity;
            foreach (int n in Access(building)) if (_outside.Dist[n] < best) best = _outside.Dist[n];
            return best + TripOverheadHours + OutsideHours;
        }

        /// <summary>Road cells (as world cell indices) along the fastest route, empty if unreachable.</summary>
        public List<int> RouteCells(int fromBuilding, int toBuilding)
        {
            var cells = new List<int>();
            EnsureBuilt();
            Tree t = TreeFrom(fromBuilding);
            int[] dst = Access(toBuilding);
            if (t == null || dst.Length == 0) return cells;
            int end = -1;
            float best = float.PositiveInfinity;
            foreach (int n in dst) if (t.Dist[n] < best) { best = t.Dist[n]; end = n; }
            for (int n = end; n >= 0; n = t.Parent[n]) cells.Add(_cellOfNode[n]);
            cells.Reverse();
            return cells;
        }

        /// <summary>Road cells from the building to the map edge, empty if not connected.</summary>
        public List<int> RouteCellsToOutside(int building)
        {
            var cells = new List<int>();
            EnsureBuilt();
            int start = -1;
            float best = float.PositiveInfinity;
            foreach (int n in Access(building)) if (_outside.Dist[n] < best) { best = _outside.Dist[n]; start = n; }
            for (int n = start; n >= 0; n = _outside.Parent[n]) cells.Add(_cellOfNode[n]);
            return cells;
        }

        public bool HasAccess(int building) { EnsureBuilt(); return Access(building).Length > 0; }

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
            _access.Clear();
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

            float speed = BaseSpeedKmh * (float)(1 + _w.Effects.Global("roadSpeed"));
            float baseHours = _w.Terrain.CellSize / 1000f / Math.Max(1f, speed);
            float[] traffic = _w.CongestionBasis;
            _nodeHours = new float[_cellOfNode.Length];
            _adj = new int[_cellOfNode.Length][];
            var nb = new List<int>(4);
            for (int n = 0; n < _cellOfNode.Length; n++)
            {
                int cell = _cellOfNode[n];
                float ratio = traffic[cell] / CellCapacityPerDay;
                _nodeHours[n] = baseHours * Math.Min(10f, 1f + 0.15f * ratio * ratio * ratio * ratio);
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
            int n = _cellOfNode.Length;
            var t = new Tree(n);
            var heap = new MinHeap(Math.Max(16, n));
            foreach (int s in sources)
            {
                if (_nodeHours[s] < t.Dist[s])
                {
                    t.Dist[s] = _nodeHours[s];
                    heap.Push(t.Dist[s], s);
                }
            }
            while (heap.Count > 0)
            {
                heap.Pop(out float du, out int u);
                if (du > t.Dist[u]) continue;
                foreach (int v in _adj[u])
                {
                    float nd = du + _nodeHours[v];
                    if (nd < t.Dist[v])
                    {
                        t.Dist[v] = nd;
                        t.Parent[v] = u;
                        heap.Push(nd, v);
                    }
                }
            }
            return t;
        }

        private sealed class Tree
        {
            public readonly float[] Dist;
            public readonly int[] Parent;

            public Tree(int n)
            {
                Dist = new float[n];
                Parent = new int[n];
                for (int i = 0; i < n; i++) { Dist[i] = float.PositiveInfinity; Parent[i] = -1; }
            }
        }
    }

    /// <summary>Binary min-heap of (priority, value); netstandard2.1 has no PriorityQueue.</summary>
    internal sealed class MinHeap
    {
        private float[] _keys;
        private int[] _vals;
        public int Count { get; private set; }

        public MinHeap(int capacity)
        {
            _keys = new float[capacity];
            _vals = new int[capacity];
        }

        public void Push(float key, int val)
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

        public void Pop(out float key, out int val)
        {
            key = _keys[0];
            val = _vals[0];
            Count--;
            float k = _keys[Count];
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
