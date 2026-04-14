using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Auth
{
    /// <summary>
    /// Authentication grain. Grain key = auth key (device ID or "platform:platformUserId").
    /// Maps an auth key to a persistent PlayerId.
    /// </summary>
    public interface IAuthGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Login with this auth key. Returns existing PlayerId or creates a new one.
        /// </summary>
        Task<AuthLoginResult> LoginAsync();

        /// <summary>
        /// Link this auth key to an existing PlayerId.
        /// Fails if already linked to a different player.
        /// </summary>
        Task<AuthLinkResult> LinkAsync(string playerId);

        /// <summary>
        /// Unlink this auth key. Clears the PlayerId mapping.
        /// Only succeeds if currently linked to the given playerId.
        /// </summary>
        Task<bool> UnlinkAsync(string playerId);

        /// <summary>
        /// Get the PlayerId currently linked to this auth key, or null.
        /// </summary>
        Task<string?> GetPlayerIdAsync();

        /// <summary>
        /// Force-unlink this auth key regardless of remaining key count.
        /// Used for device reset scenarios where the player wants to start fresh.
        /// Returns the PlayerId that was unlinked, or null if not linked.
        /// </summary>
        Task<string?> ForceUnlinkAsync();
    }

    /// <summary>
    /// Index grain: tracks all auth keys linked to a PlayerId.
    /// Grain key = PlayerId.
    /// </summary>
    public interface IAuthIndexGrain : IGrainWithStringKey
    {
        /// <summary>Add an auth key to this player's index.</summary>
        Task AddKeyAsync(string authKey);

        /// <summary>Remove an auth key from this player's index.</summary>
        Task RemoveKeyAsync(string authKey);

        /// <summary>Get all auth keys linked to this player.</summary>
        Task<List<string>> GetKeysAsync();
    }

    /// <summary>
    /// Result of a login operation.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class AuthLoginResult
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public string PlayerId { get; set; } = "";
        [Id(1), Key(1), MemoryPackOrder(1)] public bool IsNewPlayer { get; set; }
    }

    /// <summary>
    /// Result of a link operation.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class AuthLinkResult
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public bool Success { get; set; }
        [Id(1), Key(1), MemoryPackOrder(1)] public string? Error { get; set; }
        [Id(2), Key(2), MemoryPackOrder(2)] public string? ExistingPlayerId { get; set; }
    }
}
