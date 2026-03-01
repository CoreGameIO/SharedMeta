namespace SharedMeta.Server.Core.Storage
{
    /// <summary>
    /// Options for file-based grain storage.
    /// </summary>
    public class FileGrainStorageOptions
    {
        /// <summary>Root directory for grain state files.</summary>
        public string RootDirectory { get; set; } = "./data";
    }
}
