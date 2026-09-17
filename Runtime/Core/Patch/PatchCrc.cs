namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// FNV-1a hash for patch byte comparison.
    /// Used by deep desync detection to compare state mutations between server and client.
    /// </summary>
    public static class PatchCrc
    {
        private const uint FnvOffsetBasis = 2166136261u;
        private const uint FnvPrime = 16777619u;

        /// <summary>
        /// Compute FNV-1a hash of the given data.
        /// Returns 0 for null or empty input.
        /// </summary>
        public static uint Compute(byte[]? data)
            => data == null ? 0 : Compute(new System.ReadOnlySpan<byte>(data));

        /// <summary>FNV-1a over a span — caller can feed pool-rented / scratch-backed bytes
        /// without converting to byte[].</summary>
        public static uint Compute(System.ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return 0;

            uint hash = FnvOffsetBasis;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= FnvPrime;
            }
            return hash;
        }

        public static uint Compute(System.ReadOnlyMemory<byte> data) => Compute(data.Span);
    }
}
