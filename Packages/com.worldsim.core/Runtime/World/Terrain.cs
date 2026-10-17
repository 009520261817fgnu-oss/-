using System;

namespace WorldSim.Core.World
{
    /// <summary>
    /// Heightfield sampled at cell corners: a Width x Depth cell grid has (Width+1) x (Depth+1) vertices.
    /// Cell (x, z) spans world X [x*CellSize, (x+1)*CellSize] and likewise for Z. Heights are metres.
    /// </summary>
    public sealed class Terrain
    {
        public const float MinHeight = 0f;
        public const float MaxHeight = 200f;

        private readonly float[] _heights;

        public int Width { get; }
        public int Depth { get; }
        public float CellSize { get; }
        public float SeaLevel { get; }
        public int VertexCountX => Width + 1;
        public int VertexCountZ => Depth + 1;

        public Terrain(int width, int depth, float cellSize, float seaLevel)
        {
            if (width < 1 || depth < 1) throw new ArgumentOutOfRangeException(nameof(width));
            Width = width;
            Depth = depth;
            CellSize = cellSize;
            SeaLevel = seaLevel;
            _heights = new float[(width + 1) * (depth + 1)];
        }

        /// <summary>Raw vertex heights, row-major by Z (index = vz * VertexCountX + vx). Read-only use.</summary>
        public float[] RawHeights => _heights;

        public bool InBounds(GridPos cell) => cell.X >= 0 && cell.Z >= 0 && cell.X < Width && cell.Z < Depth;
        public bool VertexInBounds(int vx, int vz) => vx >= 0 && vz >= 0 && vx <= Width && vz <= Depth;

        public float GetVertexHeight(int vx, int vz) => _heights[vz * VertexCountX + vx];

        public void SetVertexHeight(int vx, int vz, float h) =>
            _heights[vz * VertexCountX + vx] = Math.Max(MinHeight, Math.Min(MaxHeight, h));

        public float CellMinHeight(GridPos c) =>
            Math.Min(Math.Min(GetVertexHeight(c.X, c.Z), GetVertexHeight(c.X + 1, c.Z)),
                Math.Min(GetVertexHeight(c.X, c.Z + 1), GetVertexHeight(c.X + 1, c.Z + 1)));

        public float CellMaxHeight(GridPos c) =>
            Math.Max(Math.Max(GetVertexHeight(c.X, c.Z), GetVertexHeight(c.X + 1, c.Z)),
                Math.Max(GetVertexHeight(c.X, c.Z + 1), GetVertexHeight(c.X + 1, c.Z + 1)));

        /// <summary>Min/max vertex height over a rectangle of cells (rect must be in bounds).</summary>
        public void HeightRange(GridRect rect, out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            for (int vz = rect.MinZ; vz <= rect.MaxZ; vz++)
            for (int vx = rect.MinX; vx <= rect.MaxX; vx++)
            {
                float h = GetVertexHeight(vx, vz);
                if (h < min) min = h;
                if (h > max) max = h;
            }
        }

        /// <summary>Bilinear height at a world-space position, clamped to the terrain edge.</summary>
        public float SampleHeight(float worldX, float worldZ)
        {
            float fx = Math.Max(0, Math.Min(Width, worldX / CellSize));
            float fz = Math.Max(0, Math.Min(Depth, worldZ / CellSize));
            int x0 = Math.Min((int)fx, Width - 1);
            int z0 = Math.Min((int)fz, Depth - 1);
            float tx = fx - x0, tz = fz - z0;
            float h00 = GetVertexHeight(x0, z0), h10 = GetVertexHeight(x0 + 1, z0);
            float h01 = GetVertexHeight(x0, z0 + 1), h11 = GetVertexHeight(x0 + 1, z0 + 1);
            float a = h00 + (h10 - h00) * tx;
            float b = h01 + (h11 - h01) * tx;
            return a + (b - a) * tz;
        }

        public GridPos WorldToCell(float worldX, float worldZ) =>
            new GridPos((int)Math.Floor(worldX / CellSize), (int)Math.Floor(worldZ / CellSize));

        public bool IsCellUnderwater(GridPos c) => CellMinHeight(c) <= SeaLevel;
    }
}
