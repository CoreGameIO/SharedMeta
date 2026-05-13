namespace SharedMeta.Server.Core.Storage
{
    /// <summary>
    /// Options for file-based grain storage.
    /// </summary>
    public class FileGrainStorageOptions
    {
        /// <summary>Root directory for grain state files.</summary>
        public string RootDirectory { get; set; } = "./data";

        /// <summary>
        /// When true (default), grain state is serialized via Orleans
        /// <c>Orleans.Serialization.Serializer</c>. This matches the behavior of real Orleans storage
        /// providers (Azure Tables, Redis, ADO.NET) and works with any grain state type that has
        /// <c>[GenerateSerializer]</c>.
        ///
        /// When false, grain state is serialized via the framework's <c>IMetaSerializer</c>
        /// (MemoryPack / MessagePack). Use this only if you intentionally want the transport
        /// serializer to also drive on-disk persistence — typical reasons are sharing the same
        /// version-tolerant format with replay payloads, or avoiding <c>[GenerateSerializer]</c>
        /// on grain state types.
        ///
        /// Note: the two modes produce incompatible byte formats. Switching this flag against
        /// an existing data directory makes prior files unreadable.
        /// </summary>
        public bool UseOrleansSerializer { get; set; } = true;
    }
}
