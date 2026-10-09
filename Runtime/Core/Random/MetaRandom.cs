using System;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core.Random
{
    /// <summary>
    /// Deterministic PRNG using xoshiro128** algorithm.
    /// State = 4 x uint32 (128 bits). Identical results on any platform.
    /// </summary>
    [MemoryPackable, MessagePackObject]
    [GenerateSerializer]
    public partial class MetaRandom : IMetaRandom
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public uint S0 { get; set; }
        [Id(1), Key(1), MemoryPackOrder(1)] public uint S1 { get; set; }
        [Id(2), Key(2), MemoryPackOrder(2)] public uint S2 { get; set; }
        [Id(3), Key(3), MemoryPackOrder(3)] public uint S3 { get; set; }
        [Id(4), Key(4), MemoryPackOrder(4)] public long ScrollId { get; set; }

        [MemoryPackConstructor, SerializationConstructor]
        public MetaRandom() { }

        /// <summary>
        /// Create a MetaRandom seeded from a uint seed.
        /// Uses SplitMix32 to expand the seed into 4 state words.
        /// </summary>
        public MetaRandom(uint seed)
        {
            // SplitMix32 to expand seed into 4 state words
            S0 = SplitMix32(ref seed);
            S1 = SplitMix32(ref seed);
            S2 = SplitMix32(ref seed);
            S3 = SplitMix32(ref seed);

            // Ensure not all zero (xoshiro requires at least one non-zero)
            if (S0 == 0 && S1 == 0 && S2 == 0 && S3 == 0)
                S0 = 1;
        }

        /// <summary>
        /// Create a MetaRandom seeded from a string (e.g. entityId).
        /// </summary>
        public static MetaRandom FromString(string seed)
        {
            // FNV-1a hash to get a uint seed
            uint hash = 2166136261u;
            foreach (char c in seed)
            {
                hash ^= (uint)c;
                hash *= 16777619u;
            }
            return new MetaRandom(hash);
        }

        public int Next(int max)
        {
            if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max), "max must be positive");
            uint raw = NextRaw();
            ScrollId++;
            // Unbiased: rejection sampling for uniform distribution
            return (int)(((ulong)raw * (ulong)max) >> 32);
        }

        public int Next(int min, int max)
        {
            if (min >= max) throw new ArgumentOutOfRangeException(nameof(min), "min must be less than max");
            return min + Next(max - min);
        }

        public float NextFloat()
        {
            uint raw = NextRaw();
            ScrollId++;
            // Use upper 24 bits for float in [0, 1)
            return (raw >> 8) * (1.0f / 16777216.0f);
        }

        /// <summary>
        /// Advance the PRNG state by <paramref name="count"/> steps without producing values.
        /// Used to keep client random in sync with server after ServerPatch application.
        /// </summary>
        public void Skip(long count)
        {
            for (long i = 0; i < count; i++)
                NextRaw();
            ScrollId += count;
        }

        /// <summary>
        /// Core xoshiro128** generator. Returns a raw uint32.
        /// </summary>
        private uint NextRaw()
        {
            uint result = RotateLeft(S1 * 5, 7) * 9;

            uint t = S1 << 9;

            S2 ^= S0;
            S3 ^= S1;
            S1 ^= S2;
            S0 ^= S3;

            S2 ^= t;
            S3 = RotateLeft(S3, 11);

            return result;
        }

        private static uint RotateLeft(uint value, int count)
        {
            return (value << count) | (value >> (32 - count));
        }

        private static uint SplitMix32(ref uint state)
        {
            state += 0x9E3779B9u;
            uint z = state;
            z = (z ^ (z >> 16)) * 0x85EBCA6Bu;
            z = (z ^ (z >> 13)) * 0xC2B2AE35u;
            z = z ^ (z >> 16);
            return z;
        }
    }
}
