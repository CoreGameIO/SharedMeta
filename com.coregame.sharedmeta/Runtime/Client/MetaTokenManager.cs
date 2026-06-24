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
    public sealed class MetaTokenManager : IAccessTokenSource, IDisposable
    {
        private readonly string _authUrl;
        private readonly Func<string, CancellationToken, Task<MetaLoginResult>> _login;
        private readonly ITokenStorage _storage;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _proactiveSkew;

        private CachedToken? _current;
        private CancellationTokenSource? _autoRefreshCts;
        private volatile bool _forceReacquire;

        /// <summary>
        /// Device-login manager: the full-login fallback (used when there's no valid refresh token) is a
        /// device login with <paramref name="deviceId"/>.
        /// </summary>
        /// <param name="authUrl">Base auth URL (e.g. "https://host/meta/auth"); /refresh, /login, /login-platform are appended.</param>
        /// <param name="deviceId">Device id used for the full-login fallback.</param>
        /// <param name="storage">Token storage; seeded from <see cref="ITokenStorage.Load"/> at construction.</param>
        /// <param name="proactiveSkew">How long before access-token expiry the auto-refresh loop renews. Default 5 min.</param>
        public MetaTokenManager(string authUrl, string deviceId, ITokenStorage storage, TimeSpan? proactiveSkew = null)
            : this(authUrl, storage, MakeDeviceLogin(deviceId), proactiveSkew)
        {
        }

        /// <summary>
        /// Platform/custom-login manager: supply the full-login strategy. Use this for platform sign-in
        /// (Google Play, Apple, Steam) — the delegate obtains a fresh platform credential and calls
        /// <see cref="MetaAuth.LoginWithPlatformAsync"/>, so a session reset re-logs in via the right
        /// account rather than falling back to a device login. Refresh tokens still cover ordinary
        /// renewal, so this runs only on a full re-login (no valid refresh token, or one rejected).
        /// <code>
        /// var tokens = new MetaTokenManager(authUrl, storage, login: async (url, ct) =>
        /// {
        ///     var serverAuthCode = await GetGooglePlayServerAuthCodeAsync(); // game-side GPGS call (fresh each time)
        ///     return await MetaAuth.LoginWithPlatformAsync(url, "google", serverAuthCode, cancellation: ct);
        /// });
        /// </code>
        /// </summary>
        /// <param name="authUrl">Base auth URL; the delegate receives it and appends the right path.</param>
        /// <param name="storage">Token storage; seeded from <see cref="ITokenStorage.Load"/> at construction.</param>
        /// <param name="login">Full-login strategy invoked when no valid refresh token is available.</param>
        /// <param name="proactiveSkew">How long before access-token expiry the auto-refresh loop renews. Default 5 min.</param>
        public MetaTokenManager(string authUrl, ITokenStorage storage,
            Func<string, CancellationToken, Task<MetaLoginResult>> login, TimeSpan? proactiveSkew = null)
        {
            _authUrl = authUrl ?? throw new ArgumentNullException(nameof(authUrl));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _login = login ?? throw new ArgumentNullException(nameof(login));
            _proactiveSkew = proactiveSkew ?? TimeSpan.FromMinutes(5);
            _current = storage.Load();
        }

        private static Func<string, CancellationToken, Task<MetaLoginResult>> MakeDeviceLogin(string deviceId)
        {
            if (deviceId == null) throw new ArgumentNullException(nameof(deviceId));
            return (url, ct) => MetaAuth.LoginAsync(url, deviceId, ct);
        }

        /// <summary>The player id of the current token, or null before the first successful token acquisition.</summary>
        public string? PlayerId => _current?.PlayerId;

        /// <summary>
        /// Force the next <see cref="GetTokenAsync()"/> to reacquire a token even if the cached access
        /// token hasn't locally expired. Use when the server <b>rejected</b> a token the client still
        /// considers valid — e.g. the JWT signing key changed, so a not-yet-expired cached token now
        /// fails signature validation. The next acquisition refreshes (or, if that fails, re-logs in),
        /// yielding a token signed with the current key.
        /// </summary>
        public void Invalidate() => _forceReacquire = true;

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
            if (!_forceReacquire && current != null && current.IsValid)
                return current.Token; // fast path: still valid, no lock, no network

            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                // Re-check under the gate — another caller may have refreshed while we waited.
                current = _current;
                if (!_forceReacquire && current != null && current.IsValid)
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
                    // Refresh token rejected (expired / revoked / reuse-detected) → fall back to a full
                    // login via the configured strategy (device by default, or platform sign-in).
                    MetaLog.Warning("[MetaTokenManager] Refresh rejected (" + ex.Message + ") — falling back to full login.");
                    result = await _login(_authUrl, cancellation).ConfigureAwait(false);
                }
            }
            else
            {
                result = await _login(_authUrl, cancellation).ConfigureAwait(false);
            }

            var token = new CachedToken(result.Token, result.PlayerId, result.ExpiresAt,
                result.RefreshToken, result.RefreshExpiresAt);
            // Adopt the new token first; persistence is best-effort so a storage failure (e.g. a
            // platform that throws off the main thread) never discards a freshly acquired token.
            _current = token;
            _forceReacquire = false; // freshly acquired — clear any pending invalidation
            try { _storage.Save(token); }
            catch (Exception ex) { MetaLog.Warning("[MetaTokenManager] Token persist failed: " + ex.Message); }
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
