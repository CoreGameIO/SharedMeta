using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Auth
{
    /// <summary>
    /// Per-player store of active refresh-token sessions. Grain key = PlayerId.
    /// <para>
    /// Each login opens a rotation "family" (one row in <see cref="RefreshTokenGrainState.Sessions"/>).
    /// Refreshing rotates the family's secret: a new refresh token is issued and the previous one is
    /// invalidated. Only a SHA-256 hash of the current token is stored — a leak of grain state never
    /// yields a usable token. Presenting an already-rotated (used) token is treated as theft and
    /// revokes the whole family (reuse detection).
    /// </para>
    /// </summary>
    public interface IRefreshTokenGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Open a new refresh-token family for the given credential and return the raw refresh token
        /// (the only time it is ever revealed). <paramref name="authKey"/> is recorded so the family
        /// can be revoked when that credential is reset/unlinked.
        /// </summary>
        Task<string> IssueAsync(string authKey, TimeSpan lifetime);

        /// <summary>
        /// Validate and rotate a presented refresh token. On success returns a freshly-rotated token;
        /// the presented one is now invalid. Replay of a used token revokes its family.
        /// </summary>
        Task<RefreshRotateResult> RotateAsync(string presentedToken, TimeSpan lifetime);

        /// <summary>Revoke a single session family (e.g. logout of one device).</summary>
        Task RevokeFamilyAsync(string familyId);

        /// <summary>Revoke every session opened with the given credential (e.g. device reset / unlink).</summary>
        Task RevokeByAuthKeyAsync(string authKey);

        /// <summary>Revoke all sessions for this player (e.g. "log out everywhere").</summary>
        Task RevokeAllAsync();
    }

    /// <summary>Outcome of <see cref="IRefreshTokenGrain.RotateAsync"/>.</summary>
    public enum RefreshOutcome
    {
        /// <summary>Token was valid; a rotated token was issued.</summary>
        Valid = 0,
        /// <summary>Token is unknown or expired — no session matched.</summary>
        Invalid = 1,
        /// <summary>A previously-rotated token was replayed; the whole family was revoked.</summary>
        Reused = 2
    }

    /// <summary>Result of a rotate/validate attempt.</summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class RefreshRotateResult
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public RefreshOutcome Outcome { get; set; }
        /// <summary>The new refresh token — populated only when <see cref="Outcome"/> is Valid.</summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public string? NewRefreshToken { get; set; }
        /// <summary>Absolute expiry of the rotated token (family expiry) when Valid.</summary>
        [Id(2), Key(2), MemoryPackOrder(2)] public DateTime RefreshExpiresAt { get; set; }
        /// <summary>The credential that owns the session when Valid (for re-issuing the access JWT's auth_type).</summary>
        [Id(3), Key(3), MemoryPackOrder(3)] public string? AuthKey { get; set; }
    }

    /// <summary>One active rotation family (login session).</summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class RefreshSession
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public string FamilyId { get; set; } = "";
        /// <summary>SHA-256 (hex) of the current valid refresh token. The raw token is never stored.</summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public string CurrentTokenHash { get; set; } = "";
        /// <summary>Credential (auth key) that opened this session.</summary>
        [Id(2), Key(2), MemoryPackOrder(2)] public string AuthKey { get; set; } = "";
        [Id(3), Key(3), MemoryPackOrder(3)] public DateTime IssuedAt { get; set; }
        [Id(4), Key(4), MemoryPackOrder(4)] public DateTime ExpiresAt { get; set; }
    }

    /// <summary>Persistent state for <see cref="RefreshTokenGrain"/>.</summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class RefreshTokenGrainState
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public List<RefreshSession> Sessions { get; set; } = new();
    }
}
