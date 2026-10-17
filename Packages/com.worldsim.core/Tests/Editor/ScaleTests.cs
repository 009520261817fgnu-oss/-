using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Economy;
using WorldSim.Core.Persistence;
using WorldSim.Core.Simulation;
using WorldSim.Core.Transport;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>
    /// S5-1: the faster routing must give the same answers as a plain shortest-path search, the same
    /// answer in both directions, and answers that do not depend on which trees happen to be cached.
    /// </summary>
    public class ScaleTests
    {
        private static WorldState Town(int days = 20)
        {
            WorldState w = WorldFactory.Create(TestContent.Load(), TestContent.SmallOptions(5));
            WorldFactory.CreateRunner(w).StepMany(days * SimClock.TicksPerDay); // some traffic, so costs differ
            return w;
        }

        private static List<int> Reachable(WorldState w) =>
            w.Buildings.Where(b => w.Network.HasAccess(b.Id)).Select(b => b.Id).ToList();

        private static List<int> AccessCells(WorldState w, int building)
        {
            var list = new List<int>();
            Building b = w.Buildings.First(x => x.Id == building);
            GridRect r = b.Footprint;
            void Add(int x, int z)
            {
                if (x < 0 || z < 0 || x >= w.Width || z >= w.Depth) return;
                int cell = z * w.Width + x;
                if (w.Network.CellCost(cell) >= 0 && !list.Contains(cell)) list.Add(cell);
            }
            for (int x = r.MinX; x < r.MaxX; x++) { Add(x, r.MinZ - 1); Add(x, r.MaxZ); }
            for (int z = r.MinZ; z < r.MaxZ; z++) { Add(r.MinX - 1, z); Add(r.MaxX, z); }
            return list;
        }

        /// <summary>Textbook Dijkstra over road cells with the network's own cell costs.</summary>
        private static float ReferenceHours(WorldState w, int from, int to)
        {
            var dist = new Dictionary<int, long>();
            var queue = new SortedSet<(long d, int cell)>();
            foreach (int s in AccessCells(w, from)) { dist[s] = w.Network.CellCost(s); queue.Add((dist[s], s)); }
            while (queue.Count > 0)
            {
                var (d, u) = queue.Min;
                queue.Remove(queue.Min);
                int x = u % w.Width, z = u / w.Width;
                foreach (int v in new[] { x > 0 ? u - 1 : -1, x < w.Width - 1 ? u + 1 : -1, z > 0 ? u - w.Width : -1, z < w.Depth - 1 ? u + w.Width : -1 })
                {
                    if (v < 0 || w.Network.CellCost(v) < 0) continue;
                    long nd = d + w.Network.CellCost(v);
                    if (dist.TryGetValue(v, out long old) && old <= nd) continue;
                    if (dist.ContainsKey(v)) queue.Remove((old, v));
                    dist[v] = nd;
                    queue.Add((nd, v));
                }
            }
            long best = long.MaxValue;
            foreach (int c in AccessCells(w, to)) if (dist.TryGetValue(c, out long d) && d < best) best = d;
            return best == long.MaxValue ? float.PositiveInfinity : (int)best / RoadNetwork.UnitsPerHour + RoadNetwork.TripOverheadHours;
        }

        [Test]
        public void TravelTimesMatchPlainDijkstraAndAreSymmetric()
        {
            WorldState w = Town();
            List<int> ids = Reachable(w);
            Assert.Greater(ids.Count, 20);
            var rng = new Random(3);
            for (int k = 0; k < 40; k++)
            {
                int a = ids[rng.Next(ids.Count)], b = ids[rng.Next(ids.Count)];
                if (a == b) continue;
                float h = w.Network.TravelHours(a, b);
                Assert.AreEqual(ReferenceHours(w, a, b), h, $"{a}->{b}");
                Assert.AreEqual(h, w.Network.TravelHours(b, a), $"{b}->{a}");
            }
        }

        [Test]
        public void AnswersDoNotDependOnWhichTreesAreCached()
        {
            WorldState w = Town();
            WorldState copy = SaveSystem.Load(TestContent.Load(), SaveSystem.Save(w)); // empty caches
            List<int> ids = Reachable(w).Take(30).ToList();
            var forward = new List<float>();
            var routes = new List<string>();
            foreach (int a in ids) foreach (int b in ids) { forward.Add(w.Network.TravelHours(a, b)); routes.Add(string.Join(",", w.Network.RouteCells(a, b))); }
            // Query the copy in the opposite order, and from the other end, so different trees get built first.
            var backward = new float[forward.Count];
            var routesBack = new string[forward.Count];
            for (int i = ids.Count - 1; i >= 0; i--)
                for (int j = ids.Count - 1; j >= 0; j--)
                {
                    backward[i * ids.Count + j] = copy.Network.TravelHours(ids[j], ids[i]);
                    routesBack[i * ids.Count + j] = string.Join(",", copy.Network.RouteCells(ids[i], ids[j]));
                }
            CollectionAssert.AreEqual(forward, backward);
            CollectionAssert.AreEqual(routes, routesBack);

            var many = new float[ids.Count];
            copy.Network.TravelHoursMany(ids[0], ids, many);
            CollectionAssert.AreEqual(forward.Take(ids.Count).ToArray(), many);
        }

        [Test]
        public void RoutesAreConnectedAndCostWhatTheTravelTimeSays()
        {
            WorldState w = Town();
            List<int> ids = Reachable(w);
            for (int k = 1; k < ids.Count; k += 7)
            {
                int a = ids[0], b = ids[k];
                List<int> cells = w.Network.RouteCells(a, b);
                Assert.IsNotEmpty(cells);
                CollectionAssert.Contains(AccessCells(w, a), cells[0], "starts at the origin");
                CollectionAssert.Contains(AccessCells(w, b), cells[cells.Count - 1], "ends at the destination");
                for (int i = 1; i < cells.Count; i++)
                {
                    int dx = Math.Abs(cells[i] % w.Width - cells[i - 1] % w.Width), dz = Math.Abs(cells[i] / w.Width - cells[i - 1] / w.Width);
                    Assert.AreEqual(1, dx + dz, "neighbouring cells");
                }
                int cost = cells.Sum(c => w.Network.CellCost(c));
                Assert.AreEqual(w.Network.TravelHours(a, b), cost / RoadNetwork.UnitsPerHour + RoadNetwork.TripOverheadHours);
            }
        }

        [Test]
        public void IdMapKeepsAscendingOrderLikeSortedDictionary()
        {
            var m = new IdMap<string>();
            foreach (int id in new[] { 5, 9, 2, 7, 12 }) m.Add(id, "v" + id);
            CollectionAssert.AreEqual(new[] { 2, 5, 7, 9, 12 }, m.Keys.ToArray());
            CollectionAssert.AreEqual(new[] { "v2", "v5", "v7", "v9", "v12" }, m.Values.ToArray());
            Assert.IsTrue(m.Remove(7));
            Assert.IsFalse(m.Remove(7));
            m[3] = "v3";
            CollectionAssert.AreEqual(new[] { 2, 3, 5, 9, 12 }, m.Select(kv => kv.Key).ToArray());
            Assert.AreEqual(5, m.Count);
            Assert.IsTrue(m.TryGetValue(9, out string v) && v == "v9");
            Assert.Throws<KeyNotFoundException>(() => { string _ = m[100]; });
            Assert.Throws<ArgumentException>(() => m.Add(5, "again"));
            Assert.Throws<InvalidOperationException>(() => { foreach (string s in m.Values) m.Remove(12); });
        }
    }
}
