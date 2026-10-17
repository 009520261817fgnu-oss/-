using System;

namespace WorldSim.Core
{
    /// <summary>
    /// Small, serializable PRNG (xorshift64*). System.Random is avoided because its
    /// algorithm differs between runtimes and its state cannot be saved.
    /// </summary>
    public sealed class DeterministicRandom
    {
        public ulong State { get; set; }

        public DeterministicRandom(ulong seed)
        {
            State = SplitMix(seed);
            if (State == 0) State = 0x9E3779B97F4A7C15UL;
        }

        public static ulong SplitMix(ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }

        public ulong NextULong()
        {
            ulong x = State;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            State = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        /// <summary>Uniform double in [0, 1).</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        /// <summary>Uniform int in [minInclusive, maxExclusive).</summary>
        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            ulong range = (ulong)((long)maxExclusive - minInclusive);
            return (int)((long)minInclusive + (long)(NextULong() % range));
        }

        public bool Chance(double probability) => NextDouble() < probability;
    }
}
