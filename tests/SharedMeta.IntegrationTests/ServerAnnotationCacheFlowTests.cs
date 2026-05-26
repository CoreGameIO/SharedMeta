using SharedMeta.Client;
using SharedMeta.Core.Transport;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.24.0 cache lifecycle flow. Documents the protocol contract on the client side:
/// the cache key is <c>ClientSignatureHash</c>; an entry is reused only when the
/// server's freshly-reported <c>ServerSignatureHash</c> matches the one stored with
/// the cached entry. Mismatch (server redeployed) forces invalidation + phase-2
/// re-registration.
/// </summary>
public class ServerAnnotationCacheFlowTests
{
    private static ClientSignatureAnnotated Make(ulong clientHash, ulong serverHash, MethodStatus first = MethodStatus.Ok)
        => new()
        {
            ClientSignatureHash = clientHash,
            ServerSignatureHash = serverHash,
            ServerToClient = new ushort[] { 0, 1, 2 },
            Statuses = new[] { first, MethodStatus.Ok, MethodStatus.Ok },
        };

    [Fact]
    public void ColdStart_NoCachedEntry_TreatAsMiss()
    {
        // Brand-new app install: cache is empty. The protocol contract says client
        // should treat any lookup as MISS and follow up with phase-2 registration.
        var cache = new InMemoryServerAnnotationCache();
        var serverReported = 0xABCDUL;          // arbitrary; server's hash this connect
        var clientHash = 0x1234UL;

        var cached = cache.TryGet(clientHash);
        Assert.Null(cached);
        // Decision logic the client applies on every connect:
        bool needsPhase2 = cached == null || cached.ServerSignatureHash != serverReported;
        Assert.True(needsPhase2);
    }

    [Fact]
    public void WarmRestart_SameServerHash_TreatAsHit()
    {
        // Subsequent app launch with the same server build still up: cached entry's
        // ServerSignatureHash matches the server's reported one → HIT, skip phase-2.
        var cache = new InMemoryServerAnnotationCache();
        var serverReported = 0xABCDUL;
        var clientHash = 0x1234UL;
        cache.Set(Make(clientHash, serverReported));

        var cached = cache.TryGet(clientHash);
        Assert.NotNull(cached);
        bool needsPhase2 = cached == null || cached.ServerSignatureHash != serverReported;
        Assert.False(needsPhase2);
    }

    [Fact]
    public void ServerRedeploy_HashChanged_TreatAsInvalid()
    {
        // Server deployed a new build mid-session uptime: client cache still has the
        // old (clientHash → annotated) entry, but the cached ServerSignatureHash no
        // longer matches what the server reports. Mismatch ⇒ invalidate + phase-2.
        var cache = new InMemoryServerAnnotationCache();
        var clientHash = 0x1234UL;
        var oldServerHash = 0xAAAAUL;
        var newServerHash = 0xBBBBUL;
        cache.Set(Make(clientHash, oldServerHash));

        var cached = cache.TryGet(clientHash);
        Assert.NotNull(cached);
        bool needsPhase2 = cached == null || cached.ServerSignatureHash != newServerHash;
        Assert.True(needsPhase2);

        // Client invalidates the stale entry before phase-2 fetches the fresh one.
        cache.Invalidate(clientHash);
        Assert.Null(cache.TryGet(clientHash));
    }

    [Fact]
    public void Phase2_PopulatesCacheWithFreshAnnotation()
    {
        // Phase-2 round-trip returned a fresh annotation; client stores it under its
        // clientHash. Subsequent connects against the same server hash hit the cache.
        var cache = new InMemoryServerAnnotationCache();
        var clientHash = 0x1234UL;
        var serverHash = 0xBBBBUL;

        cache.Set(Make(clientHash, serverHash));

        // Simulate a connect right after — same server hash → HIT.
        var cached = cache.TryGet(clientHash);
        Assert.NotNull(cached);
        Assert.Equal(serverHash, cached!.ServerSignatureHash);
    }

    [Fact]
    public void ClientUpgrade_DifferentClientHash_NoCacheCollision()
    {
        // Client app upgraded → its ClientSignatureHash changes. The cache for the
        // OLD hash stays (until evicted), but the new connect uses the new hash and
        // gets a MISS, prompting phase-2. Both entries coexist correctly.
        var cache = new InMemoryServerAnnotationCache();
        var serverHash = 0xCCCCUL;
        cache.Set(Make(oldClient: 0x1111UL, serverHash));    // see local function below

        // After client upgrade, the OLD entry is still there but unreachable through
        // the new hash:
        Assert.NotNull(cache.TryGet(0x1111UL));
        Assert.Null(cache.TryGet(0x9999UL));     // fresh client hash — no entry yet

        // Phase-2 populates under the new hash; both entries coexist.
        cache.Set(Make(0x9999UL, serverHash));
        Assert.NotNull(cache.TryGet(0x1111UL));
        Assert.NotNull(cache.TryGet(0x9999UL));

        static ClientSignatureAnnotated Make(ulong oldClient, ulong serverHash)
            => new()
            {
                ClientSignatureHash = oldClient,
                ServerSignatureHash = serverHash,
                ServerToClient = System.Array.Empty<ushort>(),
                Statuses = System.Array.Empty<MethodStatus>(),
            };
    }
}
