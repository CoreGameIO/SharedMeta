using System;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Client;
using SharedMeta.Core.Auth;
using SharedMeta.Core.Logging;
using SharedMeta.IntegrationTests.Infrastructure;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Covers the access-token shape diagnostic in <see cref="MetaTokenManager"/>: a credential that a
/// JWT-bearer server cannot even parse must announce itself on the client, instead of surfacing only
/// as an unreadable Bearer in the server log with nothing pointing at the token cache as the source.
/// <para>
/// Joins the cluster collection because it swaps the process-wide <see cref="MetaLog"/> logger, which
/// would otherwise race with the other tests that capture log output.
/// </para>
/// </summary>
[Collection(TestClusterCollection.Name)]
public class MetaTokenManagerShapeTests : IDisposable
{
    private readonly IMetaLogger _previousLogger;
    private readonly CapturingLogger _logger = new();

    private MetaAuthClient Auth => new MetaAuthClient("https://h/meta/auth", new StubAuthProvider());

    public MetaTokenManagerShapeTests()
    {
        _previousLogger = MetaLog.Logger;
        MetaLog.SetLogger(_logger);
    }

    public void Dispose() => MetaLog.SetLogger(_previousLogger);

    [Fact] // no Timeout: xUnit only honours it on async tests
    public void CachedToken_NotJwsCompact_WarnsOnSeed()
    {
        // "local" is what an in-process auth provider hands out; landing in the storage a remote
        // connection reads is the failure this warning exists to name.
        var storage = new MemoryTokenStorage(new CachedToken(
            "local", "p1", DateTime.UtcNow.AddDays(365), "local-refresh:p1", DateTime.UtcNow.AddDays(365)));

        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);

        Assert.Contains("not in JWS compact form", _logger.Warnings);
        Assert.DoesNotContain("local", _logger.Warnings); // the credential itself is never logged
    }

    [Fact]
    public void CachedToken_JwsCompact_DoesNotWarn()
    {
        var storage = new MemoryTokenStorage(new CachedToken(
            "header.payload.signature", "p1", DateTime.UtcNow.AddHours(1), "r", DateTime.UtcNow.AddDays(30)));

        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);

        Assert.DoesNotContain("not in JWS compact form", _logger.Warnings);
    }

    [Fact(Timeout = 30_000)]
    public async Task RefreshedToken_NotJwsCompact_WarnsOnAdoption()
    {
        // Nothing cached → full login, whose result is what the transport will present.
        var storage = new MemoryTokenStorage(null);
        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);
        Assert.DoesNotContain("not in JWS compact form", _logger.Warnings);

        await mgr.GetTokenAsync();

        Assert.Contains("not in JWS compact form", _logger.Warnings);
    }

    [Fact(Timeout = 30_000)]
    public async Task SameBadToken_WarnsOnce()
    {
        var storage = new MemoryTokenStorage(new CachedToken(
            "opaque", "p1", DateTime.UtcNow.AddDays(365), "r", DateTime.UtcNow.AddDays(365)));
        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);

        await mgr.GetTokenAsync();
        await mgr.GetTokenAsync();

        Assert.Equal(1, _logger.CountContaining("not in JWS compact form"));
    }

    [Fact(Timeout = 30_000)]
    public async Task Reauthentication_YieldsRejectedTokenAgain_LogsError()
    {
        // The stub answers login and refresh with the same string in both, so even escalating past
        // refresh cannot produce a credential the server would take — the dead end must be announced.
        var storage = new MemoryTokenStorage(new CachedToken(
            "still-opaque", "p1", DateTime.UtcNow.AddDays(365), "r", DateTime.UtcNow.AddDays(365)));
        using var mgr = new MetaTokenManager(Auth, "dev-1", storage);

        mgr.Invalidate();
        await mgr.GetTokenAsync();

        Assert.Contains("the auth source is not issuing new credentials", _logger.Errors);
    }

    // ---- fakes ----

    private sealed class MemoryTokenStorage : ITokenStorage
    {
        private CachedToken? _token;
        public MemoryTokenStorage(CachedToken? initial) { _token = initial; }
        public CachedToken? Load() => _token;
        public void Save(CachedToken token) { _token = token; }
        public void Clear() { _token = null; }
    }

    private sealed class CapturingLogger : IMetaLogger
    {
        private readonly System.Collections.Generic.List<string> _warnings = new();
        private readonly System.Collections.Generic.List<string> _errors = new();

        public string Warnings => Join(_warnings);
        public string Errors => Join(_errors);

        private static string Join(System.Collections.Generic.List<string> lines)
        {
            lock (lines) return string.Join("\n", lines);
        }

        public int CountContaining(string fragment)
        {
            lock (_warnings)
            {
                int n = 0;
                foreach (var w in _warnings)
                    if (w.Contains(fragment, StringComparison.Ordinal)) n++;
                return n;
            }
        }

        public bool IsEnabled(MetaLogLevel level) => true;

        public void Log(MetaLogLevel level, string message)
        {
            var sink = level == MetaLogLevel.Warning ? _warnings
                : level == MetaLogLevel.Error ? _errors
                : null;
            if (sink == null) return;
            lock (sink) sink.Add(message);
        }

        public void Log(MetaLogLevel level, string message, Exception exception) => Log(level, message);
    }

    private sealed class StubAuthProvider : IMetaAuthProvider
    {
        public Task<MetaLoginResult> LoginAsync(string authUrl, string deviceId, CancellationToken cancellation)
            => Task.FromResult(new MetaLoginResult
            {
                Token = "still-opaque",
                PlayerId = "p1",
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
                RefreshToken = "r2",
                RefreshExpiresAt = DateTime.UtcNow.AddDays(30),
            });

        public Task<MetaLoginResult> RefreshAsync(string authUrl, string refreshToken, CancellationToken cancellation)
            => LoginAsync(authUrl, "dev-1", cancellation);

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
