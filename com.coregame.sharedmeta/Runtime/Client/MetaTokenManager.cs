using System;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Core.Auth;
using SharedMeta.Core.Logging;

#nullable enable

namespace SharedMeta.Client
{
    /// <summary>
    /// Owns the access-token lifecycle for transports. Hands out a currently-valid access token via
    /// <see cref="GetTokenAsync()"/> — the function you pass to a connection's <c>accessTokenProvider</c>
    /// — and refreshes on demand when the cached token is expired or near expiry. Because SignalR calls
    /// the provider on every (re)connect and HTTP transports can read it per request, a reconnect after
    /// the access token expires automatically picks up a freshly refreshed token.
    /// <para>
    /// Refresh is single-flight: concurrent callers that arrive while a refresh is in progress share the
    /// one network round-trip rather than stampeding the /refresh endpoint. Refresh uses the stored
    /// refresh token when valid, otherwise falls back to a full device login. An optional background loop
    /// (<see cref="StartAutoRefresh"/>) refreshes proactively shortly before expiry.
    /// </para>
    /// </summary>
    public sealed class MetaTokenManager : IDisposable
    {
        private readonly string _authUrl;
        private readonly string _deviceId;
        private readonly ITokenStorage _storage;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _proactiveSkew;

        private CachedToken? _current;
        private CancellationTokenSource? _autoRefreshCts;

        /// <param name="authUrl">Base auth URL (e.g. "https://host/meta/auth"); /refresh and /login are appended.</param>
        /// <param name="deviceId">Device id used for the full-login fallback when no valid refresh token exists.</param>
        /// <param name="storage">Token storage; seeded from <see cref="ITokenStorage.Load"/> at construction.</param>
        /// <param name="proactiveSkew">How long before access-token expiry the auto-refresh loop renews. Default 5 min.</param>
        public MetaTokenManager(string authUrl, string deviceId, ITokenStorage storage, TimeSpan? proactiveSkew = null)
        {
            _authUrl = authUrl ?? throw new ArgumentNullException(nameof(authUrl));
            _deviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _proactiveSkew = proactiveSkew ?? TimeSpan.FromMinutes(5);
            _current = storage.Load();
        }

        /// <summary>The player id of the current token, or null before the first successful token acquisition.</summary>
        public string? PlayerId => _current?.PlayerId;

        /// <summary>
        /// Provider function to hand to a transport. Returns a currently-valid access token, refreshing
        /// transparently if needed. Never throws — on refresh failure it returns the last known token
        /// (possibly expired) so the transport can still attempt the call and surface the auth error.
        /// </summary>
        public Task<string?> GetTokenAsync() => GetTokenAsync(CancellationToken.None);

        /// <inheritdoc cref="GetTokenAsync()"/>
        public async Task<string?> GetTokenAsync(CancellationToken cancellation)
        {
            var current = _current;
            if (current != null && current.IsValid)
                return current.Token; // fast path: still valid, no lock, no network

            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                // Re-check under the gate — another caller may have refreshed while we waited.
                current = _current;
                if (current != null && current.IsValid)
                    return current.Token;

                return await RefreshLockedAsync(current, cancellation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MetaLog.Warning("[MetaTokenManager] Token acquisition failed: " + ex.Message);
                return _current?.Token; // best-effort: let the transport try with what we have
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Force a refresh now (single-flight), e.g. when the server reported the session lost. Returns the
        /// new access token, or null if refresh and the login fallback both failed.
        /// </summary>
        public async Task<string?> RefreshNowAsync(CancellationToken cancellation = default)
        {
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                return await RefreshLockedAsync(_current, cancellation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MetaLog.Warning("[MetaTokenManager] Forced refresh failed: " + ex.Message);
                return _current?.Token;
            }
            finally
            {
                _gate.Release();
            }
        }

        // Must be called holding _gate.
        private async Task<string?> RefreshLockedAsync(CachedToken? current, CancellationToken cancellation)
        {
            MetaLoginResult result;
            if (current != null && current.RefreshValid)
            {
                try
                {
                    result = await MetaAuth.RefreshAsync(_authUrl, current.RefreshToken, cancellation).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Refresh token rejected (expired / revoked / reuse-detected) → fall back to full login.
                    MetaLog.Warning("[MetaTokenManager] Refresh rejected (" + ex.Message + ") — falling back to full login.");
                    result = await MetaAuth.LoginAsync(_authUrl, _deviceId, cancellation).ConfigureAwait(false);
                }
            }
            else
            {
                result = await MetaAuth.LoginAsync(_authUrl, _deviceId, cancellation).ConfigureAwait(false);
            }

            var token = new CachedToken(result.Token, result.PlayerId, result.ExpiresAt,
                result.RefreshToken, result.RefreshExpiresAt);
            _storage.Save(token);
            _current = token;
            return token.Token;
        }

        /// <summary>
        /// Start a background loop that refreshes the access token shortly before it expires (by the
        /// configured proactive skew). Idempotent. Stop via <see cref="StopAutoRefresh"/> or <see cref="Dispose"/>.
        /// </summary>
        public void StartAutoRefresh()
        {
            if (_autoRefreshCts != null) return;
            _autoRefreshCts = new CancellationTokenSource();
            _ = AutoRefreshLoop(_autoRefreshCts.Token);
        }

        /// <summary>Stop the background auto-refresh loop.</summary>
        public void StopAutoRefresh()
        {
            _autoRefreshCts?.Cancel();
            _autoRefreshCts?.Dispose();
            _autoRefreshCts = null;
        }

        private async Task AutoRefreshLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var current = _current;
                    // Time until we should renew: (expiry - skew) from now. Clamp so a missing/expired
                    // token retries soon rather than spinning.
                    var renewAt = (current?.ExpiresAt ?? DateTime.UtcNow) - _proactiveSkew;
                    var wait = renewAt - DateTime.UtcNow;
                    if (wait < TimeSpan.FromSeconds(5)) wait = TimeSpan.FromSeconds(5);

                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    await GetTokenAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    MetaLog.Warning("[MetaTokenManager] Auto-refresh loop error: " + ex.Message);
                    try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        public void Dispose()
        {
            StopAutoRefresh();
            _gate.Dispose();
        }
    }
}
