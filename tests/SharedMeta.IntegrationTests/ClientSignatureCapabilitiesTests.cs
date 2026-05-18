using System.Collections.Generic;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core.Session;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.22.0 client-signature capability compute. Focused logic tests against
/// <see cref="ClientSignatureRegistry"/>'s compute pipeline and the
/// <see cref="CapabilitiesGate"/> lookup helpers — no Orleans, no transport, no
/// server fixture. The pipeline is pure, so a synthetic
/// <see cref="MetaServerSignature"/> + synthetic <see cref="MetaClientSignature"/>
/// is enough to validate every rejection / force-patch path.
/// </summary>
public class ClientSignatureCapabilitiesTests
{
    /// <summary>
    /// Exposes the protected <see cref="ClientSignatureRegistry.ComputeCapabilities"/>
    /// for direct invocation. Production code path goes via
    /// <see cref="ClientSignatureRegistry.RegisterAsync"/>, which also writes to grains —
    /// here we want the pure compute output without the Orleans round trip.
    /// </summary>
    private class TestableRegistry : ClientSignatureRegistry
    {
        public TestableRegistry(MetaServerSignature serverSig)
            : base(grainFactory: null!, serverSignature: serverSig) { }

        public ClientCapabilities Compute(MetaClientSignature sig) => ComputeCapabilities(sig);
    }

    private static MetaServerSignature ServerSig(params ServerMethodEntry[] methods)
        => new() { Methods = methods };

    private static MetaClientSignature ClientSig(params KnownMethodEntry[] methods)
        => new() { KnownMethods = new List<KnownMethodEntry>(methods) };

    [Fact]
    public void CompatibleClient_NoRestrictions()
    {
        var server = ServerSig(
            new ServerMethodEntry { ServiceName = "IService", Alias = "Do", Version = 0, ArgHash = 0xAA });
        var client = ClientSig(
            new KnownMethodEntry { ServiceName = "IService", Alias = "Do", Version = 0, ArgHash = 0xAA });

        var caps = new TestableRegistry(server).Compute(client);

        Assert.Empty(caps.RejectedMethods);
        Assert.Empty(caps.ForceServerPatchMethods);
        Assert.Empty(caps.ForceServerPatchServices);
    }

    [Fact]
    public void ClientKnowsMethodServerDoesNotHave_IsRejected()
    {
        var server = ServerSig();  // empty
        var client = ClientSig(
            new KnownMethodEntry { ServiceName = "IService", Alias = "Phantom", Version = 0, ArgHash = 0xBB });

        var caps = new TestableRegistry(server).Compute(client);

        Assert.Single(caps.RejectedMethods);
        Assert.Equal("Phantom", caps.RejectedMethods[0].Alias);
    }

    [Fact]
    public void ServerOnlyMethod_GenerateClientApiFalse_IsRejected()
    {
        // Server has the method but flagged GenerateClientApi = false. Client claiming to
        // know it is a forged or out-of-sync client; reject so a wire call never reaches
        // the server-side gate that would also block it.
        var server = ServerSig(
            new ServerMethodEntry { ServiceName = "IService", Alias = "Internal", Version = 0,
                ArgHash = 0xCC, GenerateClientApi = false });
        var client = ClientSig(
            new KnownMethodEntry { ServiceName = "IService", Alias = "Internal", Version = 0, ArgHash = 0xCC });

        var caps = new TestableRegistry(server).Compute(client);

        Assert.Single(caps.RejectedMethods);
        Assert.Equal("Internal", caps.RejectedMethods[0].Alias);
    }

    [Fact]
    public void ArgHashMismatch_IsRejected()
    {
        // Same alias + version, different parameter shape — client would serialize bytes
        // the server can't deserialize. Reject.
        var server = ServerSig(
            new ServerMethodEntry { ServiceName = "IService", Alias = "Do", Version = 0, ArgHash = 0xAA });
        var client = ClientSig(
            new KnownMethodEntry { ServiceName = "IService", Alias = "Do", Version = 0, ArgHash = 0xBEEF });

        var caps = new TestableRegistry(server).Compute(client);

        Assert.Single(caps.RejectedMethods);
    }

    [Fact]
    public void OldClientBelowMinCompatibleVersion_IsForcedToServerPatch()
    {
        // Server method body changed at v2 (MinCompatibleVersion = 2); client at v1's
        // optimistic execution would desync. Downgrade to ServerPatch.
        var server = ServerSig(
            new ServerMethodEntry { ServiceName = "IService", Alias = "Mutate", Version = 1,
                MinCompatibleVersion = 2, ArgHash = 0xDD });
        var client = ClientSig(
            new KnownMethodEntry { ServiceName = "IService", Alias = "Mutate", Version = 1, ArgHash = 0xDD });

        var caps = new TestableRegistry(server).Compute(client);

        Assert.Empty(caps.RejectedMethods);
        Assert.Single(caps.ForceServerPatchMethods);
        Assert.Equal("Mutate", caps.ForceServerPatchMethods[0].Alias);
    }

    [Fact]
    public void ClientAtOrAboveMinCompatibleVersion_NoForcePatch()
    {
        // Client built against the same v2 body — no force-patch needed.
        var server = ServerSig(
            new ServerMethodEntry { ServiceName = "IService", Alias = "Mutate", Version = 2,
                MinCompatibleVersion = 2, ArgHash = 0xDD });
        var client = ClientSig(
            new KnownMethodEntry { ServiceName = "IService", Alias = "Mutate", Version = 2, ArgHash = 0xDD });

        var caps = new TestableRegistry(server).Compute(client);

        Assert.Empty(caps.RejectedMethods);
        Assert.Empty(caps.ForceServerPatchMethods);
    }

    // ── CapabilitiesGate helpers — pure lookup logic ──────────────────────────────

    [Fact]
    public void Gate_NullCapabilities_NoRestrictions()
    {
        Assert.False(CapabilitiesGate.IsRejected(null, "IService", "Do", 0));
        Assert.False(CapabilitiesGate.IsForcedServerPatch(null, "IService", "Do", 0));
    }

    [Fact]
    public void Gate_EmptyCapabilities_NoRestrictions()
    {
        var caps = new ClientCapabilities();
        Assert.False(CapabilitiesGate.IsRejected(caps, "IService", "Do", 0));
        Assert.False(CapabilitiesGate.IsForcedServerPatch(caps, "IService", "Do", 0));
    }

    [Fact]
    public void Gate_MatchOnExactIdentity()
    {
        var caps = new ClientCapabilities
        {
            RejectedMethods = { new MethodIdentity { ServiceName = "IService", Alias = "Do", Version = 1 } }
        };
        Assert.True(CapabilitiesGate.IsRejected(caps, "IService", "Do", 1));
        Assert.False(CapabilitiesGate.IsRejected(caps, "IService", "Do", 2));   // different version
        Assert.False(CapabilitiesGate.IsRejected(caps, "IService", "Other", 1)); // different alias
        Assert.False(CapabilitiesGate.IsRejected(caps, "IOther", "Do", 1));      // different service
    }

    [Fact]
    public void Gate_ForceServerPatchByServiceName()
    {
        var caps = new ClientCapabilities
        {
            ForceServerPatchServices = { "IService" }
        };
        // Whole-service force-patch — any method/version in that service hits.
        Assert.True(CapabilitiesGate.IsForcedServerPatch(caps, "IService", "Do", 0));
        Assert.True(CapabilitiesGate.IsForcedServerPatch(caps, "IService", "Other", 5));
        Assert.False(CapabilitiesGate.IsForcedServerPatch(caps, "IUnrelated", "Do", 0));
    }

    [Fact]
    public void Gate_RejectedExceptionCarriesStructuredRequirement()
    {
        var ex = CapabilitiesGate.RejectedException("IService", "Do", 3);
        Assert.Equal("Method", ex.Requirement.FeatureKind);
        Assert.Equal("IService.Do", ex.Requirement.Identifier);
        Assert.Contains("3", ex.Requirement.Reason);
    }

    // ── 0.22.0 Per-subscriber broadcast tailoring (Stage 15) ──────────────────────
    // SessionManagerGrain.BroadcastToSessionOp delegates to CapabilitiesGate.TailorBroadcastPayload
    // for the per-player payload selection. These tests verify the strip logic without
    // requiring a live Orleans cluster.

    private static readonly byte[] Replay = new byte[] { 0xAA, 0xBB };
    private static readonly byte[] Patch = new byte[] { 0xCC, 0xDD };

    [Fact]
    public void Tailor_NoCapabilities_PassesThrough()
    {
        var (replay, patch) = CapabilitiesGate.TailorBroadcastPayload(
            caps: null, entityCaps: null, "IService", "Do", 1, Replay, Patch);
        Assert.Same(Replay, replay);
        Assert.Same(Patch, patch);
    }

    [Fact]
    public void Tailor_ModernSubscriber_KeepsReplay_StripsPatch()
    {
        // Modern client: no force-patch entries. Replay path is preferred — patch bytes
        // would be wasted bandwidth (and the user's design specifies modern clients should
        // run the typed body, not apply a diff).
        var caps = new ClientCapabilities();  // empty lists
        var (replay, patch) = CapabilitiesGate.TailorBroadcastPayload(
            caps, entityCaps: null, "IService", "Do", 1, Replay, Patch);
        Assert.Same(Replay, replay);
        Assert.Null(patch);
    }

    [Fact]
    public void Tailor_LegacySubscriber_StripsReplay_KeepsPatch()
    {
        // Legacy client (force-patch by exact method identity): replay would crash the
        // client since it doesn't know the v2 body. Patch bytes carry the state diff.
        var caps = new ClientCapabilities
        {
            ForceServerPatchMethods = { new MethodIdentity { ServiceName = "IService", Alias = "Do", Version = 1 } }
        };
        var (replay, patch) = CapabilitiesGate.TailorBroadcastPayload(
            caps, entityCaps: null, "IService", "Do", 1, Replay, Patch);
        Assert.Null(replay);
        Assert.Same(Patch, patch);
    }

    [Fact]
    public void Tailor_LegacyByServiceForcePatch_StripsReplay_KeepsPatch()
    {
        // Whole-service force-patch: any method on this service force-patches for this
        // subscriber regardless of alias/version.
        var caps = new ClientCapabilities
        {
            ForceServerPatchServices = { "IService" }
        };
        var (replay, patch) = CapabilitiesGate.TailorBroadcastPayload(
            caps, entityCaps: null, "IService", "Do", 7, Replay, Patch);
        Assert.Null(replay);
        Assert.Same(Patch, patch);
    }

    [Fact]
    public void Tailor_DifferentMethod_NotForcedPatch_ActsAsModern()
    {
        // Force-patch entry exists but for a DIFFERENT (Alias, Version). The current
        // broadcast's method isn't gated → subscriber is treated as modern for it.
        var caps = new ClientCapabilities
        {
            ForceServerPatchMethods = { new MethodIdentity { ServiceName = "IService", Alias = "Other", Version = 1 } }
        };
        var (replay, patch) = CapabilitiesGate.TailorBroadcastPayload(
            caps, entityCaps: null, "IService", "Do", 1, Replay, Patch);
        Assert.Same(Replay, replay);
        Assert.Null(patch);
    }

    [Fact]
    public void Tailor_PerEntityForcePatchService_StripsReplay_KeepsPatch()
    {
        // Session-level caps are empty (no method-version mismatch), but per-entity overlay
        // says this service is force-patched on this entity (e.g. [MetaConfigStructureBoundary]
        // triggered for this entity's resolved config version).
        var caps = new ClientCapabilities();
        var entityCaps = new EntityAugmentedCapabilities
        {
            ForceServerPatchServices = { "IService" }
        };
        var (replay, patch) = CapabilitiesGate.TailorBroadcastPayload(
            caps, entityCaps, "IService", "Do", 1, Replay, Patch);
        Assert.Null(replay);
        Assert.Same(Patch, patch);
    }

    // ── 0.22.0 Per-entity config-boundary compute (Stage 16) ──────────────────────
    // ConfigBoundaryEvaluator.ComputeAffectedServices is the pure-logic core of
    // EntityGrain.ComputePerEntityCapabilities. The semantic is OPEN-CLOSED — server config
    // classes evolve additively (new fields added, old fields kept and marked deprecated).
    // Consequence: a NEW client can read OLD config bytes natively (deprecated fields handled
    // by old method paths). An OLD client CANNOT read NEW config bytes — it doesn't know
    // about fields the new structure added. The rule is therefore asymmetric:
    //
    //     force-patch ⇔ clientCode.Version < boundary  AND  pinned.Version >= boundary
    //
    // Older drafts had this either bidirectional or only "pinned below boundary" — both
    // wrong. These tests pin down the asymmetric semantics in both directions.

    private static SharedMeta.Core.MetaConfigVersion V(int major, int minor)
        => new(major, minor, 0);

    private static SharedMeta.Server.Core.Session.ConfigBoundaryEntry Boundary(string configType, string minVer)
        => new() { ConfigTypeFullName = configType, MinConfigVersion = minVer };

    private static SharedMeta.Server.Core.Session.ServerMethodEntry ServerMethod(string service, string configType)
        => new() { ServiceName = service, Alias = "X", Version = 0, ConfigTypeFullName = configType };

    [Fact]
    public void ConfigBoundary_EntityAndClientBothOnOldConfig_NoForcePatch()
    {
        // Entity pinned 1.0, client code-version 1.0. Neither side of the 2.0 boundary —
        // direct execution OK.
        var boundaries = new[] { Boundary("Cfg", "2.0") };
        var methods = new[] { ServerMethod("IService", "Cfg") };
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            boundaries, methods, pinned: V(1, 0), clientCode: V(1, 0));
        Assert.Empty(result);
    }

    [Fact]
    public void ConfigBoundary_EntityOldClientNew_NoForcePatch()
    {
        // Entity pinned 1.0 (old config bytes), client code-version 2.0 (NEW schema). Under
        // open-closed evolution, the new client KNOWS the old schema is a strict subset of its
        // new class — deprecated fields are present in code, just unused by old methods. The
        // new client can natively replay the old method body against 1.0 bytes; no patch needed.
        // This is the case where forward-compat saves bandwidth and keeps optimistic execution.
        var boundaries = new[] { Boundary("Cfg", "2.0") };
        var methods = new[] { ServerMethod("IService", "Cfg") };
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            boundaries, methods, pinned: V(1, 0), clientCode: V(2, 0));
        Assert.Empty(result);
    }

    [Fact]
    public void ConfigBoundary_EntityNewClientOld_ForcePatch()
    {
        // Entity pinned 2.0 (NEW bytes), client code-version 1.0 (OLD schema). Client's
        // config class is missing the new fields entirely — it cannot deserialize the 2.0
        // bytes correctly, and even if it could the method bodies don't know about the new
        // fields. Force-patch: client applies the state diff and never tries to interpret
        // 2.0 structure.
        // This is the asymmetric "only old-on-new" case — the primary motivation for the
        // [MetaConfigStructureBoundary] mechanism.
        var boundaries = new[] { Boundary("Cfg", "2.0") };
        var methods = new[] { ServerMethod("IService", "Cfg") };
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            boundaries, methods, pinned: V(2, 0), clientCode: V(1, 0));
        Assert.Equal(new[] { "IService" }, result);
    }

    [Fact]
    public void ConfigBoundary_EntityAndClientBothOnNewConfig_NoForcePatch()
    {
        // Both at 2.0+. Neither is below the boundary → no force-patch.
        var boundaries = new[] { Boundary("Cfg", "2.0") };
        var methods = new[] { ServerMethod("IService", "Cfg") };
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            boundaries, methods, pinned: V(2, 0), clientCode: V(2, 0));
        Assert.Empty(result);
    }

    [Fact]
    public void ConfigBoundary_MultipleBoundariesAcrossMultipleConfigs()
    {
        // CfgA boundary at 2.0; CfgB boundary at 3.0. Client at 1.0 (below both), entity
        // pinned at 2.5. Only CfgA's boundary triggers (clientCode 1.0 < 2.0 <= pinned 2.5).
        // CfgB's boundary at 3.0 does NOT trigger (pinned 2.5 < 3.0).
        var boundaries = new[]
        {
            Boundary("CfgA", "2.0"),
            Boundary("CfgB", "3.0"),
        };
        var methods = new[]
        {
            ServerMethod("IA", "CfgA"),
            ServerMethod("IB", "CfgB"),
        };
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            boundaries, methods, pinned: V(2, 5), clientCode: V(1, 0));
        Assert.Equal(new[] { "IA" }, result);
    }

    [Fact]
    public void ConfigBoundary_FarOlderClient_StillForcePatch()
    {
        // Client at 1.0, entity pinned at 3.5, boundary at 2.0. clientCode (1.0) < 2.0
        // <= pinned (3.5) → trigger. Distance doesn't matter; presence of any crossed
        // boundary is what counts.
        var boundaries = new[] { Boundary("Cfg", "2.0") };
        var methods = new[] { ServerMethod("IService", "Cfg") };
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            boundaries, methods, pinned: V(3, 5), clientCode: V(1, 0));
        Assert.Equal(new[] { "IService" }, result);
    }

    [Fact]
    public void ConfigBoundary_ClientExactlyAtBoundary_NoForcePatch()
    {
        // Client code-version EXACTLY at the boundary (2.0). Client knows the new schema
        // (built against >= 2.0). Even if entity pinned at 3.0 introduces yet another field,
        // there's no boundary in (2.0, 3.0] in this test, so no force-patch.
        var boundaries = new[] { Boundary("Cfg", "2.0") };
        var methods = new[] { ServerMethod("IService", "Cfg") };
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            boundaries, methods, pinned: V(3, 0), clientCode: V(2, 0));
        Assert.Empty(result);
    }

    [Fact]
    public void Tailor_PerEntityRejectedService_DoesNotAffectFanOut()
    {
        // RejectedServices on entityCaps gates client-side calls but does NOT change
        // fan-out shape — broadcasts that arrive ARE delivered. (Client gate prevents
        // the call from being made in the first place; if a broadcast still arrives for
        // a rejected service, the modern-vs-legacy tailoring still uses force-patch lists.)
        var caps = new ClientCapabilities();
        var entityCaps = new EntityAugmentedCapabilities
        {
            RejectedServices = { "IService" }
        };
        var (replay, patch) = CapabilitiesGate.TailorBroadcastPayload(
            caps, entityCaps, "IService", "Do", 1, Replay, Patch);
        // Falls through modern-style: replay only.
        Assert.Same(Replay, replay);
        Assert.Null(patch);
    }

    // ── 0.22.0 EntityGrain per-subscriber fan-out tailoring (Stage 16, point 1) ───
    // BroadcastTailor.TailorForSubscriber is the pure-logic core of
    // EntityGrain.DistributeBroadcasts → TailorBroadcastForSubscriber. An earlier draft kept
    // tailoring in SessionManagerGrain.BroadcastToSessionOp; that's correct but inefficient
    // (broadcast carries both payloads on the wire to every subscriber, then SessionManager
    // strips). New architecture: EntityGrain decides per-subscriber at fan-out time and ships
    // only the variant the subscriber actually needs.

    private static SharedMeta.Server.Core.Grains.EntityBroadcast Broadcast(
        string service, string method, int version, byte[]? replay, byte[]? patch)
        => new()
        {
            Op = new SharedMeta.Core.Packets.MetaOperation
            {
                ServiceName = service,
                MethodName = method,
                MethodVersion = version,
                ReplayPayload = replay,
                PatchBytes = patch,
                StateBytes = null,
            },
        };

    [Fact]
    public void TailorForSubscriber_NoContributions_ReturnsOriginalUnchanged()
    {
        // Modern subscriber: no method-level or service-level force-patch contributions.
        // Tailor returns the original broadcast reference (zero alloc), keeping both payloads
        // intact for whatever downstream chooses to do.
        var b = Broadcast("IService", "Do", 1, Replay, Patch);
        var result = SharedMeta.Server.Core.Grains.BroadcastTailor.TailorForSubscriber(
            b, subscriberMethodContributions: null, subscriberServiceContributions: null);
        Assert.Same(b, result);
    }

    [Fact]
    public void TailorForSubscriber_MethodForcePatched_StripsReplay()
    {
        // Subscriber contributed (IService, Do, 1) to session-level force-patch (their
        // ClientCapabilities.ForceServerPatchMethods had this entry). Tailor → patch only.
        var b = Broadcast("IService", "Do", 1, Replay, Patch);
        var methodContribs = new List<(string, string, int)> { ("IService", "Do", 1) };
        var result = SharedMeta.Server.Core.Grains.BroadcastTailor.TailorForSubscriber(
            b, methodContribs, subscriberServiceContributions: null);
        Assert.NotSame(b, result);
        Assert.Null(result.Op.ReplayPayload);
        Assert.Same(Patch, result.Op.PatchBytes);
    }

    [Fact]
    public void TailorForSubscriber_ServiceForcePatched_StripsReplay()
    {
        // Subscriber contributed "IService" to per-entity force-patch (config-boundary
        // triggered on this entity). Any method on the service force-patches for them.
        var b = Broadcast("IService", "DoOther", 5, Replay, Patch);
        var serviceContribs = new List<string> { "IService" };
        var result = SharedMeta.Server.Core.Grains.BroadcastTailor.TailorForSubscriber(
            b, subscriberMethodContributions: null, serviceContribs);
        Assert.NotSame(b, result);
        Assert.Null(result.Op.ReplayPayload);
        Assert.Same(Patch, result.Op.PatchBytes);
    }

    [Fact]
    public void TailorForSubscriber_DifferentMethod_PassesThroughUnchanged()
    {
        // Force-patch contribution is for (IService, Do, 1) — current broadcast is for
        // (IService, OtherMethod, 1). Different identity → no force-patch → original.
        var b = Broadcast("IService", "OtherMethod", 1, Replay, Patch);
        var methodContribs = new List<(string, string, int)> { ("IService", "Do", 1) };
        var result = SharedMeta.Server.Core.Grains.BroadcastTailor.TailorForSubscriber(
            b, methodContribs, subscriberServiceContributions: null);
        Assert.Same(b, result);
    }

    [Fact]
    public void TailorForSubscriber_StateBytesAlwaysPreserved()
    {
        // ServerReplace path: StateBytes always preserved regardless of force-patch verdict.
        var stateBytes = new byte[] { 0xEE };
        var b = new SharedMeta.Server.Core.Grains.EntityBroadcast
        {
            Op = new SharedMeta.Core.Packets.MetaOperation
            {
                ServiceName = "IService", MethodName = "Do", MethodVersion = 1,
                ReplayPayload = Replay, PatchBytes = Patch, StateBytes = stateBytes,
            },
        };
        var methodContribs = new List<(string, string, int)> { ("IService", "Do", 1) };
        var result = SharedMeta.Server.Core.Grains.BroadcastTailor.TailorForSubscriber(
            b, methodContribs, subscriberServiceContributions: null);
        Assert.Same(stateBytes, result.Op.StateBytes);
    }
}
