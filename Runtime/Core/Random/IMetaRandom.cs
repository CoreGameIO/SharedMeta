namespace SharedMeta.Core.Random
{
    /// <summary>
    /// Deterministic random number generator interface.
    /// Used by services via Context.Random (optimistic) and Context.ServerRandom (server-only).
    /// </summary>
    public interface IMetaRandom
    {
        /// <summary>
        /// Returns a non-negative random integer less than max.
        /// </summary>
        int Next(int max);

        /// <summary>
        /// Returns a random integer in [min, max).
        /// </summary>
        int Next(int min, int max);

        /// <summary>
        /// Returns a random float in [0.0, 1.0).
        /// </summary>
        float NextFloat();

        /// <summary>
        /// Number of Next/NextFloat calls made since creation or last state load.
        /// Used for desync detection between client and server.
        /// </summary>
        long ScrollId { get; }
    }
}
