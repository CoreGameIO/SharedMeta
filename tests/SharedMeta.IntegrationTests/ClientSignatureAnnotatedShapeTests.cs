using System.Collections.Generic;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core.Session;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.24.0 ClientSignatureAnnotated shape — pure compute tests against
/// <see cref="ClientSignatureRegistry.ComputeAnnotated"/>. Same fixture style as
/// <see cref="ClientSignatureCapabilitiesTests"/>: synthetic signatures, no Orleans, no transport.
/// </summary>
public class ClientSignatureAnnotatedShapeTests
{
    /// <summary>Exposes the protected compute methods for direct invocation in tests.</summary>
    private class TestableRegistry : ClientSignatureRegistry
    {
        public TestableRegistry(MetaServerSignature serverSig)
            : base(grainFactory: null!, serverSignature: serverSig) { }

        public ClientSignatureAnnotated ComputeAnnotatedForTest(MetaClientSignature sig)
            => ComputeAnnotatedAndMap(sig).Item1;
    }

    private static MetaServerSignature ServerSig(ulong hash, params ServerMethodEntry[] methods)
        => new() { SignatureHash = hash, Methods = methods };

    private static MetaClientSignature ClientSig(ulong hash, params KnownMethodEntry[] methods)
        => new() { SignatureHash = hash, KnownMethods = new List<KnownMethodEntry>(methods) };

    private static ServerMethodEntry SM(string svc, string alias, int ver, ushort idx, ulong argHash = 0xAA,
        int minCompat = 0, bool genClientApi = true) =>
        new()
        {
            ServiceName = svc, Alias = alias, Version = ver, GlobalIndex = idx,
            ArgHash = argHash, MinCompatibleVersion = minCompat, GenerateClientApi = genClientApi,
        };

    private static KnownMethodEntry CM(string svc, string alias, int ver, ushort idx, ulong argHash = 0xAA) =>
        new() { ServiceName = svc, Alias = alias, Version = ver, GlobalIndex = idx, ArgHash = argHash };

    [Fact]
    public void EmptyClient_StatusesIsEmpty()
    {
        var server = ServerSig(0x123, SM("ISvc", "Do", 0, 0));
        var client = ClientSig(0x456);

        var ann = new TestableRegistry(server).ComputeAnnotatedForTest(client);

        Assert.Equal(0x456UL, ann.ClientSignatureHash);
        Assert.Equal(0x123UL, ann.ServerSignatureHash);
        Assert.Empty(ann.Statuses);
    }

    [Fact]
    public void CompatibleClient_AllStatusesOk()
    {
        var server = ServerSig(0xA,
            SM("ISvc", "Do",  0, 0),
            SM("ISvc", "Sit", 0, 1));
        var client = ClientSig(0xB,
            CM("ISvc", "Do",  0, 0),
            CM("ISvc", "Sit", 0, 1));

        var ann = new TestableRegistry(server).ComputeAnnotatedForTest(client);

        Assert.Equal(2, ann.Statuses.Length);
        Assert.Equal(MethodStatus.Ok, ann.Statuses[0]);
        Assert.Equal(MethodStatus.Ok, ann.Statuses[1]);
    }

    [Fact]
    public void ClientKnowsMethodServerDoesNotHave_StatusRejectedAtClientIndex()
    {
        var server = ServerSig(0xA);  // empty
        var client = ClientSig(0xB,
            CM("ISvc", "Phantom", 0, 0));

        var ann = new TestableRegistry(server).ComputeAnnotatedForTest(client);

        Assert.Single(ann.Statuses);
        Assert.Equal(MethodStatus.Rejected, ann.Statuses[0]);
    }

    [Fact]
    public void ServerOnlyGenerateClientApiFalse_StatusRejectedAtClientIndex()
    {
        var server = ServerSig(0xA,
            SM("ISvc", "Internal", 0, 0, genClientApi: false));
        var client = ClientSig(0xB,
            CM("ISvc", "Internal", 0, 0));

        var ann = new TestableRegistry(server).ComputeAnnotatedForTest(client);

        Assert.Equal(MethodStatus.Rejected, ann.Statuses[0]);
    }

    [Fact]
    public void ArgHashMismatch_StatusRejected()
    {
        var server = ServerSig(0xA,
            SM("ISvc", "Do", 0, 0, argHash: 0xAA));
        var client = ClientSig(0xB,
            CM("ISvc", "Do", 0, 0, argHash: 0xBB));

        var ann = new TestableRegistry(server).ComputeAnnotatedForTest(client);

        Assert.Equal(MethodStatus.Rejected, ann.Statuses[0]);
    }

    [Fact]
    public void OldClientBelowMinCompatibleVersion_StatusForceServerPatch()
    {
        var server = ServerSig(0xA,
            SM("ISvc", "Do", 1, 0, minCompat: 2));
        var client = ClientSig(0xB,
            CM("ISvc", "Do", 1, 0));

        var ann = new TestableRegistry(server).ComputeAnnotatedForTest(client);

        Assert.Equal(MethodStatus.ForceServerPatch, ann.Statuses[0]);
    }

    [Fact]
    public void ServerToClient_IdentityMatch()
    {
        var server = ServerSig(0xA,
            SM("ISvc", "Do",  0, 0),
            SM("ISvc", "Sit", 0, 1));
        var client = ClientSig(0xB,
            CM("ISvc", "Do",  0, 0),
            CM("ISvc", "Sit", 0, 1));

        var ann = new TestableRegistry(server).ComputeAnnotatedForTest(client);

        Assert.Equal(2, ann.ServerToClient.Length);
        Assert.Equal((ushort)0, ann.ServerToClient[0]);
        Assert.Equal((ushort)1, ann.ServerToClient[1]);
    }

    [Fact]
    public void ServerToClient_IdsDifferAcrossSides_MappingPreserved()
    {
        // Realistic cross-version shape: server has 6 methods (idx 0..5), client knows only
        // the first and last (idx 0, 1 — its own dense indexing). Server's idx 5 maps to
        // client's idx 1; other server slots are sentinels because client doesn't know them.
        var server = ServerSig(0xA,
            SM("ISvc", "A", 0, 0),
            SM("ISvc", "B", 0, 1),
            SM("ISvc", "C", 0, 2),
            SM("ISvc", "D", 0, 3),
            SM("ISvc", "E", 0, 4),
            SM("ISvc", "F", 0, 5));
        var client = ClientSig(0xB,
            CM("ISvc", "A", 0, 0),
            CM("ISvc", "F", 0, 1));

        var ann = new TestableRegistry(server).ComputeAnnotatedForTest(client);

        Assert.Equal(6, ann.ServerToClient.Length);
        Assert.Equal((ushort)0, ann.ServerToClient[0]);
        Assert.Equal((ushort)1, ann.ServerToClient[5]);
        for (int i = 1; i < 5; i++)
            Assert.Equal(ClientSignatureAnnotated.UnknownClientMethodId, ann.ServerToClient[i]);
    }

    [Fact]
    public void ServerOnlyMethod_ServerToClientSentinel()
    {
        var server = ServerSig(0xA,
            SM("ISvc", "Shared", 0, 0),
            SM("ISvc", "OnlyServer", 0, 1));
        var client = ClientSig(0xB,
            CM("ISvc", "Shared", 0, 0));

        var ann = new TestableRegistry(server).ComputeAnnotatedForTest(client);

        Assert.Equal((ushort)0, ann.ServerToClient[0]);
        Assert.Equal(ClientSignatureAnnotated.UnknownClientMethodId, ann.ServerToClient[1]);
    }

    [Fact]
    public void StatusesLength_EqualsClientMethodCount()
    {
        // Real-world generator emits dense GlobalIndex (0..N-1), so length == KnownMethods.Count.
        // ComputeAnnotated sizes defensively by max index + 1 — proven equivalent here.
        var server = ServerSig(0xA,
            SM("ISvc", "A", 0, 0),
            SM("ISvc", "B", 0, 1),
            SM("ISvc", "C", 0, 2));
        var client = ClientSig(0xB,
            CM("ISvc", "A", 0, 0),
            CM("ISvc", "B", 0, 1),
            CM("ISvc", "C", 0, 2));

        var ann = new TestableRegistry(server).ComputeAnnotatedForTest(client);

        Assert.Equal(client.KnownMethods.Count, ann.Statuses.Length);
        Assert.All(ann.Statuses, s => Assert.Equal(MethodStatus.Ok, s));
    }

    [Fact]
    public void HashesPropagateUnchanged()
    {
        var server = ServerSig(0xDEADBEEF,
            SM("ISvc", "Do", 0, 0));
        var client = ClientSig(0xCAFEBABE,
            CM("ISvc", "Do", 0, 0));

        var ann = new TestableRegistry(server).ComputeAnnotatedForTest(client);

        Assert.Equal(0xCAFEBABEUL, ann.ClientSignatureHash);
        Assert.Equal(0xDEADBEEFUL, ann.ServerSignatureHash);
    }

    [Fact]
    public void DeterministicCompute_SameInputsSameOutput()
    {
        var server = ServerSig(0xA,
            SM("ISvc", "Do", 1, 0, minCompat: 2),
            SM("ISvc", "Sit", 0, 1));
        var client = ClientSig(0xB,
            CM("ISvc", "Do", 1, 0),
            CM("ISvc", "Sit", 0, 1));

        var reg = new TestableRegistry(server);
        var a = reg.ComputeAnnotatedForTest(client);
        var b = reg.ComputeAnnotatedForTest(client);

        Assert.Equal(a.Statuses, b.Statuses);
        Assert.Equal(a.ServerToClient, b.ServerToClient);
    }
}
