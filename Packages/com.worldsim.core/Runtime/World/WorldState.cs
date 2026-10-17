using System;
using System.Collections.Generic;
using WorldSim.Core.Content;
using WorldSim.Core.Simulation;

namespace WorldSim.Core.World
{
    /// <summary>
    /// The single authoritative play state. Systems and presentation read it; all player edits go
    /// through the Try* methods (via commands) so validation lives in one place and every change
    /// raises a <see cref="Changed"/> notification.
    /// </summary>
    public sealed class WorldState
    {
        public const int FormatVersion = 1;
        /// <summary>Largest height difference allowed inside a single road cell, in metres.</summary>
        public const float RoadMaxSlope = 3.5f;

        private readonly SortedDictionary<int, Building> _buildings = new SortedDictionary<int, Building>();
        private readonly int[] _occupant; // building id per cell, 0 = none
        private readonly bool[] _road;
        private readonly byte[] _vegetation;
        private int _roadCellCount;

        public ContentDatabase Content { get; }
        public ulong Seed { get; }
        public Terrain Terrain { get; }
        public SimClock Clock { get; } = new SimClock();
        public DeterministicRandom Random { get; }
        public int NextId { get; private set; } = 1;

        public event Action<WorldChange> Changed;

        public WorldState(ContentDatabase content, ulong seed, Terrain terrain, byte[] vegetation = null)
        {
            Content = content ?? throw new ArgumentNullException(nameof(content));
            Seed = seed;
            Terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
            Random = new DeterministicRandom(seed ^ 0x51u);
            int cells = terrain.Width * terrain.Depth;
            _occupant = new int[cells];
            _road = new bool[cells];
            _vegetation = vegetation ?? new byte[cells];
            if (_vegetation.Length != cells) throw new ArgumentException("Vegetation size mismatch", nameof(vegetation));
        }

        public int Width => Terrain.Width;
        public int Depth => Terrain.Depth;
        public IEnumerable<Building> Buildings => _buildings.Values;
        public int BuildingCount => _buildings.Count;
        public int RoadCellCount => _roadCellCount;

        private int Index(GridPos c) => c.Z * Terrain.Width + c.X;

        public bool TryGetBuilding(int id, out Building b) => _buildings.TryGetValue(id, out b);

        public Building GetBuildingAt(GridPos c) =>
            Terrain.InBounds(c) && _occupant[Index(c)] != 0 ? _buildings[_occupant[Index(c)]] : null;

        public bool HasRoad(GridPos c) => Terrain.InBounds(c) && _road[Index(c)];
        public bool IsOccupied(GridPos c) => _occupant[Index(c)] != 0 || _road[Index(c)];
        public byte GetVegetation(GridPos c) => _vegetation[Index(c)];

        /// <summary>True when any cell orthogonally adjacent to the footprint is a road.</summary>
        public bool HasRoadAccess(GridRect r)
        {
            for (int x = r.MinX; x < r.MaxX; x++)
                if (HasRoad(new GridPos(x, r.MinZ - 1)) || HasRoad(new GridPos(x, r.MaxZ))) return true;
            for (int z = r.MinZ; z < r.MaxZ; z++)
                if (HasRoad(new GridPos(r.MinX - 1, z)) || HasRoad(new GridPos(r.MaxX, z))) return true;
            return false;
        }

        // ---------------------------------------------------------------- buildings

        public CommandResult CanPlaceBuilding(BuildingDef def, GridPos origin)
        {
            GridRect r = GridRect.FromSize(origin, def.Width, def.Depth);
            if (r.MinX < 0 || r.MinZ < 0 || r.MaxX > Width || r.MaxZ > Depth) return CommandResult.Fail("範囲外です");
            for (int z = r.MinZ; z < r.MaxZ; z++)
            for (int x = r.MinX; x < r.MaxX; x++)
            {
                var c = new GridPos(x, z);
                if (IsOccupied(c)) return CommandResult.Fail("他の建物または道路と重なっています");
                if (Terrain.IsCellUnderwater(c)) return CommandResult.Fail("水面には建設できません");
            }
            Terrain.HeightRange(r, out float min, out float max);
            if (max - min > def.MaxSlope) return CommandResult.Fail($"傾斜が大きすぎます（高低差 {max - min:0.0}m > {def.MaxSlope:0.0}m）");
            return CommandResult.Ok();
        }

        public CommandResult TryPlaceBuilding(string defId, GridPos origin)
        {
            if (!Content.TryGetBuilding(defId, out BuildingDef def)) return CommandResult.Fail($"未定義の建物 '{defId}'");
            CommandResult check = CanPlaceBuilding(def, origin);
            if (!check.Success) return check;

            GridRect r = GridRect.FromSize(origin, def.Width, def.Depth);
            Terrain.HeightRange(r, out _, out float max);
            var b = new Building(NextId++, def, origin, max);
            _buildings.Add(b.Id, b);
            bool clearedVegetation = false;
            for (int z = r.MinZ; z < r.MaxZ; z++)
            for (int x = r.MinX; x < r.MaxX; x++)
            {
                int i = Index(new GridPos(x, z));
                _occupant[i] = b.Id;
                if (_vegetation[i] != 0) { _vegetation[i] = 0; clearedVegetation = true; }
            }
            Raise(WorldChangeKind.Buildings, r);
            if (clearedVegetation) Raise(WorldChangeKind.Vegetation, r);
            return CommandResult.Ok(def.Name + "を建設しました", b.Id);
        }

        public CommandResult TryRemoveBuilding(int id)
        {
            if (!_buildings.TryGetValue(id, out Building b)) return CommandResult.Fail($"建物 #{id} は存在しません");
            GridRect r = b.Footprint;
            for (int z = r.MinZ; z < r.MaxZ; z++)
            for (int x = r.MinX; x < r.MaxX; x++)
                _occupant[Index(new GridPos(x, z))] = 0;
            _buildings.Remove(id);
            Raise(WorldChangeKind.Buildings, r);
            return CommandResult.Ok(b.Def.Name + "を撤去しました");
        }

        // ---------------------------------------------------------------- roads

        /// <summary>Cells of an L-shaped path: along X first, then along Z. Includes both ends.</summary>
        public static List<GridPos> RoadPath(GridPos from, GridPos to)
        {
            var path = new List<GridPos>();
            int sx = Math.Sign(to.X - from.X), sz = Math.Sign(to.Z - from.Z);
            int x = from.X, z = from.Z;
            path.Add(new GridPos(x, z));
            while (x != to.X) { x += sx; path.Add(new GridPos(x, z)); }
            while (z != to.Z) { z += sz; path.Add(new GridPos(x, z)); }
            return path;
        }

        /// <summary>
        /// Builds road along <see cref="RoadPath"/>. Atomic: if any new cell is invalid nothing is built.
        /// Cells that already have road are kept, so paths may cross or extend existing roads.
        /// </summary>
        public CommandResult TryBuildRoad(GridPos from, GridPos to)
        {
            List<GridPos> path = RoadPath(from, to);
            foreach (GridPos c in path)
            {
                if (!Terrain.InBounds(c)) return CommandResult.Fail("範囲外です");
                if (_road[Index(c)]) continue;
                if (_occupant[Index(c)] != 0) return CommandResult.Fail($"{c} に建物があります");
                if (Terrain.IsCellUnderwater(c)) return CommandResult.Fail($"{c} は水面です（橋は未実装）");
                if (Terrain.CellMaxHeight(c) - Terrain.CellMinHeight(c) > RoadMaxSlope)
                    return CommandResult.Fail($"{c} の傾斜が大きすぎます");
            }

            int added = 0;
            GridRect region = GridRect.Empty;
            foreach (GridPos c in path)
            {
                int i = Index(c);
                if (_road[i]) continue;
                _road[i] = true;
                _vegetation[i] = 0;
                added++;
                region = region.Union(GridRect.FromSize(c, 1, 1));
            }
            _roadCellCount += added;
            if (added > 0)
            {
                Raise(WorldChangeKind.Roads, region);
                Raise(WorldChangeKind.Vegetation, region);
            }
            return CommandResult.Ok($"道路 {added} マスを建設しました");
        }

        public CommandResult TryRemoveRoad(GridPos c)
        {
            if (!HasRoad(c)) return CommandResult.Fail("道路がありません");
            _road[Index(c)] = false;
            _roadCellCount--;
            Raise(WorldChangeKind.Roads, GridRect.FromSize(c, 1, 1));
            return CommandResult.Ok("道路を撤去しました");
        }

        /// <summary>Removes whatever structure is on the cell (building first, then road).</summary>
        public CommandResult TryBulldoze(GridPos c)
        {
            if (!Terrain.InBounds(c)) return CommandResult.Fail("範囲外です");
            Building b = GetBuildingAt(c);
            if (b != null) return TryRemoveBuilding(b.Id);
            if (HasRoad(c)) return TryRemoveRoad(c);
            return CommandResult.Fail("撤去する対象がありません");
        }

        // ---------------------------------------------------------------- terrain editing

        /// <summary>
        /// A vertex is locked when any of the up to four cells sharing it holds a building or road,
        /// so terrain edits never leave structures floating or buried.
        /// </summary>
        public bool IsVertexLocked(int vx, int vz)
        {
            for (int dz = -1; dz <= 0; dz++)
            for (int dx = -1; dx <= 0; dx++)
            {
                var c = new GridPos(vx + dx, vz + dz);
                if (Terrain.InBounds(c) && IsOccupied(c)) return true;
            }
            return false;
        }

        /// <summary>Raises (delta &gt; 0) or lowers terrain around a vertex with a smooth falloff.</summary>
        public CommandResult TryRaiseTerrain(int centerVx, int centerVz, float radiusCells, float delta)
        {
            return EditTerrain(centerVx, centerVz, radiusCells, (h, w) => h + delta * w);
        }

        /// <summary>Pulls terrain around a vertex toward <paramref name="targetHeight"/>.</summary>
        public CommandResult TryFlattenTerrain(int centerVx, int centerVz, float radiusCells, float targetHeight)
        {
            return EditTerrain(centerVx, centerVz, radiusCells, (h, w) => h + (targetHeight - h) * Math.Min(1f, w * 1.5f));
        }

        private CommandResult EditTerrain(int cvx, int cvz, float radius, Func<float, float, float> apply)
        {
            if (!Terrain.VertexInBounds(cvx, cvz)) return CommandResult.Fail("範囲外です");
            if (radius <= 0) return CommandResult.Fail("半径が不正です");
            int r = (int)Math.Ceiling(radius);
            int changed = 0, locked = 0;
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            for (int vz = cvz - r; vz <= cvz + r; vz++)
            for (int vx = cvx - r; vx <= cvx + r; vx++)
            {
                if (!Terrain.VertexInBounds(vx, vz)) continue;
                float dist = (float)Math.Sqrt((vx - cvx) * (vx - cvx) + (vz - cvz) * (vz - cvz));
                if (dist > radius) continue;
                if (IsVertexLocked(vx, vz)) { locked++; continue; }
                float t = dist / radius;
                float weight = 1f - t * t * (3f - 2f * t);
                float before = Terrain.GetVertexHeight(vx, vz);
                Terrain.SetVertexHeight(vx, vz, apply(before, weight));
                if (Terrain.GetVertexHeight(vx, vz) == before) continue;
                changed++;
                minX = Math.Min(minX, vx); minZ = Math.Min(minZ, vz);
                maxX = Math.Max(maxX, vx); maxZ = Math.Max(maxZ, vz);
            }
            if (changed == 0)
                return locked > 0 ? CommandResult.Fail("建物・道路の下は編集できません") : CommandResult.Fail("変化がありません");

            // Cells touching a changed vertex are affected.
            var region = new GridRect(minX - 1, minZ - 1, maxX + 1, maxZ + 1).Clamp(Width, Depth);
            Raise(WorldChangeKind.Terrain, region);
            RemoveVegetationBelowSea(region);
            return CommandResult.Ok(locked > 0 ? $"地形を編集しました（建物・道路の下 {locked} 点は保護）" : "地形を編集しました");
        }

        private void RemoveVegetationBelowSea(GridRect region)
        {
            bool any = false;
            for (int z = region.MinZ; z < region.MaxZ; z++)
            for (int x = region.MinX; x < region.MaxX; x++)
            {
                var c = new GridPos(x, z);
                int i = Index(c);
                if (_vegetation[i] != 0 && Terrain.IsCellUnderwater(c)) { _vegetation[i] = 0; any = true; }
            }
            if (any) Raise(WorldChangeKind.Vegetation, region);
        }

        // ---------------------------------------------------------------- time & hashing

        internal void AdvanceClock() => Clock.Tick++;

        /// <summary>Fingerprint of the full play state, for determinism and save/load tests.</summary>
        public ulong ComputeHash()
        {
            var h = new StateHasher();
            h.Add(FormatVersion);
            h.Add(Seed);
            h.Add(Clock.Tick);
            h.Add(Random.State);
            h.Add(NextId);
            foreach (float v in Terrain.RawHeights) h.Add(v);
            foreach (bool r in _road) h.Add(r);
            foreach (byte v in _vegetation) h.Add((int)v);
            foreach (Building b in _buildings.Values)
            {
                h.Add(b.Id);
                h.Add(b.Def.Id);
                h.Add(b.Origin.X);
                h.Add(b.Origin.Z);
                h.Add(b.BaseHeight);
                h.Add(b.Condition);
            }
            return h.Value;
        }

        private void Raise(WorldChangeKind kind, GridRect region) => Changed?.Invoke(new WorldChange(kind, region));
    }
}
