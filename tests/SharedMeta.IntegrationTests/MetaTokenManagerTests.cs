using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using SharedMeta.Client;
using SharedMeta.Core.Auth;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Unit-style coverage for <see cref="MetaTokenManager"/>: the cached fast path, on-demand refresh,
/// single-flight under concurrency, and the full-login fallback. Drives a fake
/// <see cref="IMetaAuthProvider"/> through a per-test <see cref="MetaAuthClient"/> — no shared state,
/// so these run in parallel with everything else.
/// </summary>
public class MetaTokenManagerTests
{
    private readonly FakeAuthProvider _provider = new();

    private MetaAuthClient Auth => new MetaAuthClient("https://h/meta/auth", _provider);

    [Fact(Timeout = 30_000)]
    public async Task GetToken_WhenAccessValid_ReturnsCached_NoNetwork()
    {
        var storage = new MemoryTokenStorage(new CachedToken(
            "access-valid", "p1", DateTime.UtcNow.AddHours(1), "refresh-1", DateTime.UtcNow.AddDays(30)));
        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);

        var token = await mgr.GetTokenAsync();

        Assert.Equal("access-valid", token);
        Assert.Equal(0, _provider.RefreshCalls);
        Assert.Equal(0, _provider.LoginCalls);
    }

    [Fact(Timeout = 30_000)]
    public async Task GetToken_WhenAccessExpired_RefreshesAndPersists()
    {
        var storage = new MemoryTokenStorage(new CachedToken(
            "access-old", "p1", DateTime.UtcNow.AddMinutes(-1), "refresh-1", DateTime.UtcNow.AddDays(30)));
        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);

        var token = await mgr.GetTokenAsync();

        Assert.Equal("access-from-refresh", token);
        Assert.Equal(1, _provider.RefreshCalls);
        Assert.Equal(0, _provider.LoginCalls);
        Assert.Equal("access-from-refresh", storage.Saved?.Token); // persisted
    }

    [Fact(Timeout = 30_000)]
    public async Task GetToken_Concurrent_RefreshesOnlyOnce()
    {
        var storage = new MemoryTokenStorage(new CachedToken(
            "access-old", "p1", DateTime.UtcNow.AddMinutes(-1), "refresh-1", DateTime.UtcNow.AddDays(30)));
        _provider.RefreshDelay = TimeSpan.FromMilliseconds(100); // make callers pile up on the gate
        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);

        var tasks = Enumerable.Range(0, 20).Select(_ => mgr.GetTokenAsync()).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, t => Assert.Equal("access-from-refresh", t));
        Assert.Equal(1, _provider.RefreshCalls); // single-flight
    }

    [Fact(Timeout = 30_000)]
    public async Task Invalidate_ForcesReacquire_EvenWhenAccessStillValid()
    {
        // A locally-valid access token that the server would reject (e.g. signing key changed).
        var storage = new MemoryTokenStorage(new CachedToken(
            "access-stale", "p1", DateTime.UtcNow.AddHours(1), "refresh-1", DateTime.UtcNow.AddDays(30)));
        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);

        // Without invalidation the cached token is returned (fast path).
        Assert.Equal("access-stale", await mgr.GetTokenAsync());
        Assert.Equal(0, _provider.RefreshCalls);

        // Invalidate → next acquisition reacquires despite local validity.
        mgr.Invalidate();
        Assert.Equal("access-from-refresh", await mgr.GetTokenAsync());
        Assert.Equal(1, _provider.RefreshCalls);

        // Flag cleared after success — subsequent calls use the fast path again.
        Assert.Equal("access-from-refresh", await mgr.GetTokenAsync());
        Assert.Equal(1, _provider.RefreshCalls);
    }

    [Fact(Timeout = 30_000)]
    public async Task Invalidate_RefreshReturnsRejectedToken_EscalatesToLogin()
    {
        // The auth source hands back the very token the server refused (e.g. a provider that mints a
        // deterministic credential). Adopting it again would spin the reject/reacquire loop forever.
        var storage = new MemoryTokenStorage(new CachedToken(
            "access-stale", "p1", DateTime.UtcNow.AddHours(1), "refresh-1", DateTime.UtcNow.AddDays(30)));
        _provider.RefreshResult = "access-stale";
        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);

        Assert.Equal("access-stale", await mgr.GetTokenAsync()); // fast path, server hasn't spoken yet

        mgr.Invalidate();                                        // server rejected "access-stale"

        Assert.Equal("access-from-login", await mgr.GetTokenAsync());
        Assert.Equal(1, _provider.RefreshCalls);                 // refresh tried first…
        Assert.Equal(1, _provider.LoginCalls);                   // …then escalated past it
    }

    [Fact(Timeout = 30_000)]
    public async Task Refresh_ReturnsSameTokenWithoutRejection_DoesNotEscalate()
    {
        // Same string back, but nothing was rejected — an expiry-driven refresh whose backend reissues
        // an identical token is not a failure and must not trigger a full login.
        var storage = new MemoryTokenStorage(new CachedToken(
            "access-old", "p1", DateTime.UtcNow.AddMinutes(-1), "refresh-1", DateTime.UtcNow.AddDays(30)));
        _provider.RefreshResult = "access-old";
        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);

        Assert.Equal("access-old", await mgr.GetTokenAsync());
        Assert.Equal(1, _provider.RefreshCalls);
        Assert.Equal(0, _provider.LoginCalls);
    }

    [Fact(Timeout = 30_000)]
    public async Task GetToken_NoUsableToken_FallsBackToLogin()
    {
        var storage = new MemoryTokenStorage(null); // nothing stored
        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);

        var token = await mgr.GetTokenAsync();

        Assert.Equal("access-from-login", token);
        Assert.Equal(0, _provider.RefreshCalls);
        Assert.Equal(1, _provider.LoginCalls);
    }

    [Fact(Timeout = 30_000)]
    public async Task GetToken_RefreshRejected_FallsBackToLogin()
    {
        var storage = new MemoryTokenStorage(new CachedToken(
            "access-old", "p1", DateTime.UtcNow.AddMinutes(-1), "refresh-bad", DateTime.UtcNow.AddDays(30)));
        _provider.FailRefresh = true;
        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);

        var token = await mgr.GetTokenAsync();

        Assert.Equal("access-from-login", token);
        Assert.Equal(1, _provider.RefreshCalls);
        Assert.Equal(1, _provider.LoginCalls);
    }

    [Fact(Timeout = 30_000)]
    public async Task CustomLogin_NoToken_UsesCustomLoginNotDevice()
    {
        var storage = new MemoryTokenStorage(null); // nothing stored → forces a full login
        int customCalls = 0;
        using var mgr = new MetaTokenManager(Auth, storage, login: (client, ct) =>
        {
            customCalls++;
            return Task.FromResult(PlatformResult());
        });

        Assert.Equal("access-platform", await mgr.GetTokenAsync());
        Assert.Equal(1, customCalls);
        Assert.Equal(0, _provider.LoginCalls);   // device login NOT used
        Assert.Equal(0, _provider.RefreshCalls);
    }

    [Fact(Timeout = 30_000)]
    public async Task CustomLogin_RefreshRejected_FallsBackToCustomLogin()
    {
        var storage = new MemoryTokenStorage(new CachedToken(
            "access-old", "p-g", DateTime.UtcNow.AddMinutes(-1), "refresh-bad", DateTime.UtcNow.AddDays(30)));
        _provider.FailRefresh = true;   // server rejects the refresh
        int customCalls = 0;
        using var mgr = new MetaTokenManager(Auth, storage, login: (client, ct) =>
        {
            customCalls++;
            return Task.FromResult(PlatformResult());
        });

        Assert.Equal("access-platform", await mgr.GetTokenAsync());
        Assert.Equal(1, _provider.RefreshCalls);  // tried refresh first
        Assert.Equal(1, customCalls);             // then fell back to the custom (platform) login
        Assert.Equal(0, _provider.LoginCalls);    // device login NOT used
    }

    private static MetaLoginResult PlatformResult() => new MetaLoginResult
    {
        Token = "access-platform",
        PlayerId = "p-g",
        ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        RefreshToken = "refresh-platform",
        RefreshExpiresAt = DateTime.UtcNow.AddDays(30),
    };

    // ---- fakes ----

    private sealed class MemoryTokenStorage : ITokenStorage
    {
        private CachedToken? _token;
        public CachedToken? Saved { get; private set; }
        public MemoryTokenStorage(CachedToken? initial) { _token = initial; Saved = initial; }
        public CachedToken? Load() => _token;
        public void Save(CachedToken token) { _token = token; Saved = token; }
        public void Clear() { _token = null; }
    }

    private sealed class FakeAuthProvider : IMetaAuthProvider
    {
        private int _refreshCalls;
        private int _loginCalls;
        public int RefreshCalls => _refreshCalls;
        public int LoginCalls => _loginCalls;
        public TimeSpan RefreshDelay { get; set; }
        public bool FailRefresh { get; set; }
        public string RefreshResult { get; set; } = "access-from-refresh";

        public async Task<MetaLoginResult> RefreshAsync(string authUrl, string refreshToken, CancellationToken cancellation)
        {
            Interlocked.Increment(ref _refreshCalls);
            if (RefreshDelay > TimeSpan.Zero) await Task.Delay(RefreshDelay, cancellation);
            if (FailRefresh) throw new InvalidOperationException("refresh rejected (401)");
            return new MetaLoginResult
            {
                Token = RefreshResult,
                PlayerId = "p1",
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
                RefreshToken = "refresh-2",
                RefreshExpiresAt = DateTime.UtcNow.AddDays(30),
            };
        }

        public Task<MetaLoginResult> LoginAsync(string authUrl, string deviceId, CancellationToken cancellation)
        {
            Interlocked.Increment(ref _loginCalls);
            return Task.FromResult(new MetaLoginResult
            {
                Token = "access-from-login",
                PlayerId = "p1",
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
                RefreshToken = "refresh-login",
                RefreshExpiresAt = DateTime.UtcNow.AddDays(30),
            });
        }

        public Task<MetaLoginResult> LoginWithPlatformAsync(string authUrl, string platform, string platformToken, CancellationToken cancellation)
            => throw new NotSupportedException();
        public Task<bool> LinkAsync(string authUrl, string platform, string platformToken, string accessToken, CancellationToken cancellation)
            => throw new NotSupportedException();
        public Task<bool> UnlinkAsync(string authUrl, string authKey, string accessToken, CancellationToken cancellation)
            => throw new NotSupportedException();
        public Task<bool> ResetDeviceAsync(string authUrl, string deviceId, string accessToken, CancellationToken cancellation)
            => throw new NotSupportedException();
    }
}
