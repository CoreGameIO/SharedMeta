using System.Threading.Tasks;
using SharedMeta.Client;
using SharedMeta.Core.Transport;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.24.0 <see cref="InMemoryServerAnnotationCache"/> contract. PlayerPrefs variant is
/// Unity-only and exercised via the Wizard / Expedition integration projects, not here.
/// </summary>
public class ServerAnnotationCacheTests
{
    private static ClientSignatureAnnotated Make(ulong clientHash, ulong serverHash, params MethodStatus[] statuses)
        => new()
        {
            ClientSignatureHash = clientHash,
            ServerSignatureHash = serverHash,
            ServerToClient = new ushort[] { 0, 1, 2 },
            Statuses = statuses,
        };

    [Fact]
    public void TryGet_EmptyCache_ReturnsNull()
    {
        var cache = new InMemoryServerAnnotationCache();
        Assert.Null(cache.TryGet(0xAA));
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsSameInstance()
    {
        var cache = new InMemoryServerAnnotationCache();
        var ann = Make(0xAA, 0xBB, MethodStatus.Ok, MethodStatus.Rejected);
        cache.Set(ann);
        Assert.Same(ann, cache.TryGet(0xAA));
    }

    [Fact]
    public void Set_OverwritesExisting()
    {
        var cache = new InMemoryServerAnnotationCache();
        cache.Set(Make(0xAA, 0xBB, MethodStatus.Ok));
        var fresh = Make(0xAA, 0xCC, MethodStatus.ForceServerPatch);
        cache.Set(fresh);
        Assert.Same(fresh, cache.TryGet(0xAA));
        Assert.Equal(0xCCUL, cache.TryGet(0xAA)!.ServerSignatureHash);
    }

    [Fact]
    public void Invalidate_DropsEntry()
    {
        var cache = new InMemoryServerAnnotationCache();
        cache.Set(Make(0xAA, 0xBB, MethodStatus.Ok));
        cache.Invalidate(0xAA);
        Assert.Null(cache.TryGet(0xAA));
    }

    [Fact]
    public void Invalidate_OnMissingKey_NoThrow()
    {
        var cache = new InMemoryServerAnnotationCache();
        cache.Invalidate(0xFFFF);  // never inserted — must not throw
        Assert.Null(cache.TryGet(0xFFFF));
    }

    [Fact]
    public void DistinctClientHashes_Coexist()
    {
        var cache = new InMemoryServerAnnotationCache();
        cache.Set(Make(0x01, 0xAA));
        cache.Set(Make(0x02, 0xBB));
        cache.Set(Make(0x03, 0xCC));

        Assert.Equal(0xAAUL, cache.TryGet(0x01)!.ServerSignatureHash);
        Assert.Equal(0xBBUL, cache.TryGet(0x02)!.ServerSignatureHash);
        Assert.Equal(0xCCUL, cache.TryGet(0x03)!.ServerSignatureHash);
    }

    [Fact]
    public void Set_NullEntry_NoOp()
    {
        var cache = new InMemoryServerAnnotationCache();
        cache.Set(null!);
        Assert.Null(cache.TryGet(0));
    }

    [Fact]
    public async Task ConcurrentSetAndGet_NoDeadlock_AndConsistent()
    {
        var cache = new InMemoryServerAnnotationCache();
        var tasks = new Task[8];
        for (int t = 0; t < tasks.Length; t++)
        {
            int local = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    ulong key = (ulong)((local << 8) | (i & 0xFF));
                    cache.Set(Make(key, 0xBEEF));
                    Assert.NotNull(cache.TryGet(key));
                }
            });
        }
        await Task.WhenAll(tasks);
    }
}

/// <summary>
/// 0.24.0 <see cref="CapabilitiesGate"/> with the new Statuses[]-based lookup.
/// </summary>
public class CapabilitiesGateStatusesTests
{
    private static ClientSignatureAnnotated AnnFromStatuses(params MethodStatus[] statuses)
        => new() { Statuses = statuses, ServerToClient = System.Array.Empty<ushort>() };

    [Fact]
    public void IsRejected_NullAnnotation_False()
    {
        Assert.False(CapabilitiesGate.IsRejected((ClientSignatureAnnotated?)null, 0));
    }

    [Fact]
    public void IsRejected_OutOfRangeMethodId_False()
    {
        var ann = AnnFromStatuses(MethodStatus.Ok, MethodStatus.Rejected);
        Assert.False(CapabilitiesGate.IsRejected(ann, 100));   // beyond statuses length
    }

    [Fact]
    public void IsRejected_StatusRejected_True()
    {
        var ann = AnnFromStatuses(MethodStatus.Ok, MethodStatus.Rejected, MethodStatus.Ok);
        Assert.True(CapabilitiesGate.IsRejected(ann, 1));
        Assert.False(CapabilitiesGate.IsRejected(ann, 0));
        Assert.False(CapabilitiesGate.IsRejected(ann, 2));
    }

    [Fact]
    public void IsForcedServerPatch_StatusForcePatch_True()
    {
        var ann = AnnFromStatuses(MethodStatus.Ok, MethodStatus.ForceServerPatch, MethodStatus.Rejected);
        Assert.True(CapabilitiesGate.IsForcedServerPatch(ann, 1));
        Assert.False(CapabilitiesGate.IsForcedServerPatch(ann, 0));
        // Rejected method is NOT force-patch — the two statuses are mutually exclusive.
        Assert.False(CapabilitiesGate.IsForcedServerPatch(ann, 2));
    }

    [Fact]
    public void TranslateServerToClient_NullAnnotation_PassThrough()
    {
        // Negotiation disabled — assume identity mapping (legacy path).
        Assert.Equal((ushort?)5, CapabilitiesGate.TranslateServerToClient(null, 5));
    }

    [Fact]
    public void TranslateServerToClient_MappedId_ReturnsClientId()
    {
        var ann = new ClientSignatureAnnotated
        {
            ServerToClient = new ushort[] { 10, 20, 30 },
            Statuses = System.Array.Empty<MethodStatus>(),
        };
        Assert.Equal((ushort?)20, CapabilitiesGate.TranslateServerToClient(ann, 1));
        Assert.Equal((ushort?)30, CapabilitiesGate.TranslateServerToClient(ann, 2));
    }

    [Fact]
    public void TranslateServerToClient_SentinelMeansUnknown()
    {
        var ann = new ClientSignatureAnnotated
        {
            ServerToClient = new ushort[] { ClientSignatureAnnotated.UnknownClientMethodId, 5 },
            Statuses = System.Array.Empty<MethodStatus>(),
        };
        Assert.Null(CapabilitiesGate.TranslateServerToClient(ann, 0));
        Assert.Equal((ushort?)5, CapabilitiesGate.TranslateServerToClient(ann, 1));
    }

    [Fact]
    public void TranslateServerToClient_OutOfRange_ReturnsNull()
    {
        var ann = new ClientSignatureAnnotated
        {
            ServerToClient = new ushort[] { 0 },
            Statuses = System.Array.Empty<MethodStatus>(),
        };
        Assert.Null(CapabilitiesGate.TranslateServerToClient(ann, 99));
    }
}
