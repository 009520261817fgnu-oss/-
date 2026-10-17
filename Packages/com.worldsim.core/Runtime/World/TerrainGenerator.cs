using System;

namespace WorldSim.Core.World
{
    public sealed class TerrainSettings
    {
        public int Width = 128;
        public int Depth = 128;
        public float CellSize = 10f;
        public float SeaLevel = 10f;
        /// <summary>Height the central plain is pulled toward, leaving room for the starting city.</summary>
        public float PlainHeight = 16f;
        public float NoiseScale = 28f;
        public int Octaves = 5;
    }

    /// <summary>
    /// Procedural heightfield: sea along the south edge, hills rising to mountains in the north,
    /// and a flattened plain in the centre for the starting city. Deterministic for a given seed.
    /// </summary>
    public static class TerrainGenerator
    {
        public static Terrain Generate(TerrainSettings s, ulong seed)
        {
            var terrain = new Terrain(s.Width, s.Depth, s.CellSize, s.SeaLevel);
            var noise = new ValueNoise(seed);
            var ridgeNoise = new ValueNoise(seed ^ 0xA5A5A5A5UL);
            double cx = s.Width * 0.5, cz = s.Depth * 0.5;
            double plainRadius = Math.Min(s.Width, s.Depth) * 0.22;

            for (int vz = 0; vz <= s.Depth; vz++)
            for (int vx = 0; vx <= s.Width; vx++)
            {
                double n = noise.Fbm(vx / s.NoiseScale, vz / s.NoiseScale, s.Octaves);
                double north = (double)vz / s.Depth;

                double h = 2 + north * 34 + (n - 0.5) * 26;
                if (north > 0.65)
                {
                    double ridge = 1 - Math.Abs(ridgeNoise.Fbm(vx / 18.0, vz / 18.0, 4) * 2 - 1);
                    h += (north - 0.65) / 0.35 * ridge * 70;
                }

                double d = Math.Sqrt((vx - cx) * (vx - cx) + (vz - cz) * (vz - cz)) / plainRadius;
                double w = SmoothStep(1.6, 0.6, d);
                h = h + (s.PlainHeight + (n - 0.5) * 3 - h) * w * 0.92;

                terrain.SetVertexHeight(vx, vz, (float)h);
            }
            return terrain;
        }

        /// <summary>Initial forest density per cell (0-255), on dry, gentle, noise-selected land.</summary>
        public static byte[] GenerateVegetation(Terrain t, ulong seed)
        {
            var veg = new byte[t.Width * t.Depth];
            var noise = new ValueNoise(seed ^ 0x5EEDF00DUL);
            for (int z = 0; z < t.Depth; z++)
            for (int x = 0; x < t.Width; x++)
            {
                var c = new GridPos(x, z);
                float min = t.CellMinHeight(c), max = t.CellMaxHeight(c);
                if (min <= t.SeaLevel + 1f || max > 85f || max - min > 6f) continue;
                double n = noise.Fbm(x / 14.0, z / 14.0, 3);
                if (n > 0.52) veg[z * t.Width + x] = (byte)Math.Min(255, (n - 0.52) / 0.2 * 255);
            }
            return veg;
        }

        private static double SmoothStep(double edge0, double edge1, double x)
        {
            double t = Math.Max(0, Math.Min(1, (x - edge0) / (edge1 - edge0)));
            return t * t * (3 - 2 * t);
        }
    }

    /// <summary>Hash-based 2D value noise with fractal summation. Output roughly in [0, 1].</summary>
    public sealed class ValueNoise
    {
        private readonly ulong _seed;

        public ValueNoise(ulong seed) { _seed = DeterministicRandom.SplitMix(seed); }

        private double Lattice(int x, int z)
        {
            ulong h = DeterministicRandom.SplitMix(_seed ^ ((ulong)(uint)x * 0x9E3779B1UL) ^ ((ulong)(uint)z << 32));
            return (h >> 11) * (1.0 / (1UL << 53));
        }

        public double Sample(double x, double z)
        {
            int x0 = (int)Math.Floor(x), z0 = (int)Math.Floor(z);
            double tx = x - x0, tz = z - z0;
            tx = tx * tx * (3 - 2 * tx);
            tz = tz * tz * (3 - 2 * tz);
            double a = Lattice(x0, z0) + (Lattice(x0 + 1, z0) - Lattice(x0, z0)) * tx;
            double b = Lattice(x0, z0 + 1) + (Lattice(x0 + 1, z0 + 1) - Lattice(x0, z0 + 1)) * tx;
            return a + (b - a) * tz;
        }

        public double Fbm(double x, double z, int octaves)
        {
            double sum = 0, amp = 1, norm = 0, freq = 1;
            for (int i = 0; i < octaves; i++)
            {
                sum += Sample(x * freq + i * 17.3, z * freq - i * 9.1) * amp;
                norm += amp;
                amp *= 0.5;
                freq *= 2;
            }
            return sum / norm;
        }
    }
}
