using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace SharedMeta.Auth
{
    /// <summary>
    /// Per-player refresh-token store with rotation and reuse detection. Grain key = PlayerId.
    /// Orleans guarantees single-threaded execution per grain, so rotation is naturally serialized —
    /// two concurrent refreshes of the same family can't race.
    /// </summary>
    public class RefreshTokenGrain : Grain, IRefreshTokenGrain
    {
        private readonly IPersistentState<RefreshTokenGrainState> _state;

        public RefreshTokenGrain(
            [PersistentState("refreshTokens", "Default")] IPersistentState<RefreshTokenGrainState> state)
        {
            _state = state;
        }

        public async Task<string> IssueAsync(string authKey, TimeSpan lifetime)
        {
            var playerId = this.GetPrimaryKeyString();
            PruneExpired();

            var familyId = Guid.NewGuid().ToString("N");
            var secret = RefreshTokens.NewSecret();
            var raw = RefreshTokens.Compose(playerId, familyId, secret);

            _state.State.Sessions.Add(new RefreshSession
            {
                FamilyId = familyId,
                CurrentTokenHash = RefreshTokens.Hash(raw),
                AuthKey = authKey,
                IssuedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow + lifetime,
            });

            await _state.WriteStateAsync();
            return raw;
        }

        public async Task<RefreshRotateResult> RotateAsync(string presentedToken, TimeSpan lifetime)
        {
            if (!RefreshTokens.TryParse(presentedToken, out _, out var familyId, out _))
                return new RefreshRotateResult { Outcome = RefreshOutcome.Invalid };

            PruneExpired();

            var session = _state.State.Sessions.Find(s => s.FamilyId == familyId);
            if (session == null)
            {
                // Unknown or already-pruned family. Could be a genuine expiry or a replay of a token
                // whose family we revoked — either way there's nothing to rotate.
                if (_state.State.Sessions.Count == 0) DeactivateOnIdle();
                return new RefreshRotateResult { Outcome = RefreshOutcome.Invalid };
            }

            if (session.ExpiresAt <= DateTime.UtcNow)
            {
                _state.State.Sessions.Remove(session);
                await _state.WriteStateAsync();
                return new RefreshRotateResult { Outcome = RefreshOutcome.Invalid };
            }

            var presentedHash = RefreshTokens.Hash(presentedToken);
            if (!RefreshTokens.HashEquals(presentedHash, session.CurrentTokenHash))
            {
                // The family exists but the presented token isn't the current one → a previously
                // rotated (used) token was replayed. Treat as compromise: revoke the whole family.
                _state.State.Sessions.Remove(session);
                await _state.WriteStateAsync();
                return new RefreshRotateResult { Outcome = RefreshOutcome.Reused };
            }

            // Valid → rotate the secret in place. Family expiry is absolute (not extended), so a
            // stolen-then-rotated session can't be kept alive indefinitely past its original window.
            var playerId = this.GetPrimaryKeyString();
            var newSecret = RefreshTokens.NewSecret();
            var newRaw = RefreshTokens.Compose(playerId, familyId, newSecret);
            session.CurrentTokenHash = RefreshTokens.Hash(newRaw);
            await _state.WriteStateAsync();

            return new RefreshRotateResult
            {
                Outcome = RefreshOutcome.Valid,
                NewRefreshToken = newRaw,
                RefreshExpiresAt = session.ExpiresAt,
                AuthKey = session.AuthKey,
            };
        }

        public async Task RevokeFamilyAsync(string familyId)
        {
            if (_state.State.Sessions.RemoveAll(s => s.FamilyId == familyId) > 0)
                await _state.WriteStateAsync();
        }

        public async Task RevokeByAuthKeyAsync(string authKey)
        {
            if (_state.State.Sessions.RemoveAll(s => s.AuthKey == authKey) > 0)
                await _state.WriteStateAsync();
        }

        public async Task RevokeAllAsync()
        {
            if (_state.State.Sessions.Count > 0)
            {
                _state.State.Sessions.Clear();
                await _state.WriteStateAsync();
            }
        }

        private void PruneExpired()
        {
            var now = DateTime.UtcNow;
            _state.State.Sessions.RemoveAll(s => s.ExpiresAt <= now);
        }
    }

    /// <summary>
    /// Refresh-token primitives. Token format is <c>{playerId}.{familyId}.{secret}</c> — the playerId
    /// prefix lets the HTTP layer route to the owning per-player grain without a separate field, and
    /// familyId identifies the rotation chain. Only the SHA-256 hash of the whole token is persisted.
    /// </summary>
    internal static class RefreshTokens
    {
        public static string NewSecret()
        {
            var bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            // base64url, no padding — keeps the token free of '.' and '/' so the 3-part split is safe.
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static string Compose(string playerId, string familyId, string secret)
            => playerId + "." + familyId + "." + secret;

        /// <summary>
        /// Parse <c>{playerId}.{familyId}.{secret}</c>. Splits from the right so a playerId that itself
        /// contains '.' is tolerated (familyId/secret never contain '.').
        /// </summary>
        public static bool TryParse(string token, out string playerId, out string familyId, out string secret)
        {
            playerId = familyId = secret = "";
            if (string.IsNullOrEmpty(token)) return false;

            int lastDot = token.LastIndexOf('.');
            if (lastDot <= 0) return false;
            int firstDot = token.LastIndexOf('.', lastDot - 1);
            if (firstDot <= 0) return false;

            playerId = token.Substring(0, firstDot);
            familyId = token.Substring(firstDot + 1, lastDot - firstDot - 1);
            secret = token.Substring(lastDot + 1);
            return playerId.Length > 0 && familyId.Length > 0 && secret.Length > 0;
        }

        public static string Hash(string token)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(token)));
        }

        /// <summary>Constant-time comparison of two equal-length hex hashes.</summary>
        public static bool HashEquals(string a, string b)
            => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    }
}
