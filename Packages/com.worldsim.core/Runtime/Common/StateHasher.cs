using System;

namespace WorldSim.Core
{
    /// <summary>FNV-1a 64-bit hasher used to fingerprint world state in determinism tests.</summary>
    public sealed class StateHasher
    {
        private const ulong Prime = 1099511628211UL;
        public ulong Value { get; private set; } = 14695981039346656037UL;

        public void Add(ulong v)
        {
            for (int i = 0; i < 8; i++)
            {
                Value ^= (byte)(v >> (i * 8));
                Value *= Prime;
            }
        }

        public void Add(long v) => Add((ulong)v);
        public void Add(int v) => Add((ulong)(uint)v);
        public void Add(bool v) => Add(v ? 1UL : 0UL);
        public void Add(float v) => Add((ulong)(uint)BitConverter.SingleToInt32Bits(v));
        public void Add(double v) => Add((ulong)BitConverter.DoubleToInt64Bits(v));

        public void Add(string s)
        {
            Add(s.Length);
            foreach (char c in s) Add((int)c);
        }
    }
}
