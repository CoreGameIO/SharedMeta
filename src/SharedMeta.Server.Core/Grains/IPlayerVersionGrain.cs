using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Persistent per-player client version history. Grain key = PlayerId.
    /// Tracks the highest Major.Minor the player has ever connected with, preventing
    /// downgrade connections that could corrupt a profile migrated to a newer config schema.
    ///
    /// Patch is intentionally ignored — players can move freely between patches of the same
    /// minor version.
    /// </summary>
    public interface IPlayerVersionGrain : IGrainWithStringKey
    {
        /// <summary>Returns the stored max "Major.Minor" version string, or null if none recorded.</summary>
        Task<string?> GetMaxClientVersionAsync();

        /// <summary>
        /// Records a successful connect attempt with <paramref name="clientVersion"/>.
        /// Returns <see cref="ClientVersionRecordResult.Accepted"/> = true when allowed,
        /// false when the version is a downgrade by major or minor (caller should reject).
        /// </summary>
        Task<ClientVersionRecordResult> RecordClientVersionAsync(string clientVersion);
    }

    /// <summary>Result of <see cref="IPlayerVersionGrain.RecordClientVersionAsync"/>.</summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class ClientVersionRecordResult
    {
        /// <summary>True when the connect is allowed; false when the version is a downgrade.</summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public bool Accepted { get; set; }

        /// <summary>The stored max "Major.Minor" version (for error messages). Null when nothing was previously stored.</summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public string? MaxVersion { get; set; }
    }
}
