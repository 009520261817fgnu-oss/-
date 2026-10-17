using System;

namespace WorldSim.Core
{
    /// <summary>Integer cell coordinate on the world grid. X runs east, Z runs north.</summary>
    public readonly struct GridPos : IEquatable<GridPos>
    {
        public readonly int X;
        public readonly int Z;

        public GridPos(int x, int z)
        {
            X = x;
            Z = z;
        }

        public bool Equals(GridPos other) => X == other.X && Z == other.Z;
        public override bool Equals(object obj) => obj is GridPos other && Equals(other);
        public override int GetHashCode() => (X * 73856093) ^ (Z * 19349663);
        public override string ToString() => $"({X},{Z})";

        public static bool operator ==(GridPos a, GridPos b) => a.Equals(b);
        public static bool operator !=(GridPos a, GridPos b) => !a.Equals(b);
    }

    /// <summary>Axis-aligned rectangle of cells. Max is exclusive.</summary>
    public readonly struct GridRect : IEquatable<GridRect>
    {
        public readonly int MinX;
        public readonly int MinZ;
        public readonly int MaxX;
        public readonly int MaxZ;

        public GridRect(int minX, int minZ, int maxX, int maxZ)
        {
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
        }

        public static GridRect FromSize(GridPos origin, int width, int depth) =>
            new GridRect(origin.X, origin.Z, origin.X + width, origin.Z + depth);

        public static readonly GridRect Empty = new GridRect(0, 0, 0, 0);

        public int Width => MaxX - MinX;
        public int Depth => MaxZ - MinZ;
        public bool IsEmpty => Width <= 0 || Depth <= 0;

        public bool Contains(GridPos p) => p.X >= MinX && p.X < MaxX && p.Z >= MinZ && p.Z < MaxZ;

        public GridRect Union(GridRect other)
        {
            if (IsEmpty) return other;
            if (other.IsEmpty) return this;
            return new GridRect(Math.Min(MinX, other.MinX), Math.Min(MinZ, other.MinZ),
                Math.Max(MaxX, other.MaxX), Math.Max(MaxZ, other.MaxZ));
        }

        public GridRect Expand(int cells) => new GridRect(MinX - cells, MinZ - cells, MaxX + cells, MaxZ + cells);

        public GridRect Clamp(int width, int depth) =>
            new GridRect(Math.Max(0, MinX), Math.Max(0, MinZ), Math.Min(width, MaxX), Math.Min(depth, MaxZ));

        public bool Equals(GridRect o) => MinX == o.MinX && MinZ == o.MinZ && MaxX == o.MaxX && MaxZ == o.MaxZ;
        public override bool Equals(object obj) => obj is GridRect o && Equals(o);
        public override int GetHashCode() => (MinX, MinZ, MaxX, MaxZ).GetHashCode();
        public override string ToString() => $"[{MinX},{MinZ} - {MaxX},{MaxZ})";
    }
}
