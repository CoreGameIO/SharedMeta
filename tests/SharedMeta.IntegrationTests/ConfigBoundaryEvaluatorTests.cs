using SharedMeta.Core;
using SharedMeta.Core.Transport;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.22.0 ConfigBoundaryEvaluator (per-entity force-patch compute). Pure semantics tests —
/// the OPEN-CLOSED config evolution rule is asymmetric:
/// <code>force-patch ⇔ clientCode.Version &lt; boundary AND pinned.Version >= boundary</code>
/// Server config classes evolve additively (new fields added, old fields kept and marked
/// deprecated). A NEW client can read OLD config bytes natively (deprecated fields handled
/// by old method paths). An OLD client CANNOT read NEW config bytes — it doesn't know about
/// fields the new structure added.
/// <para>
/// Migrated from ClientSignatureCapabilitiesTests (0.24.0 sweep) — kept here because the
/// evaluator is independent of <see cref="ClientSignatureAnnotated"/>; the rest of that file
/// moved into <c>ClientSignatureAnnotatedShapeTests</c> + <c>CapabilitiesGateStatusesTests</c>.
/// </para>
/// </summary>
public class ConfigBoundaryEvaluatorTests
{
    private static MetaConfigVersion V(int major, int minor) => new(major, minor, 0);

    private static ConfigBoundaryEntry Boundary(string configType, string minVer)
        => new() { ConfigTypeFullName = configType, MinConfigVersion = minVer };

    private static ServerMethodEntry ServerMethod(string service, string configType)
        => new() { ServiceName = service, Alias = "X", Version = 0, ConfigTypeFullName = configType };

    private static ServerMethodEntry ServerMethod(string service, string configType, bool patchTracking)
        => new() { ServiceName = service, Alias = "X", Version = 0, ConfigTypeFullName = configType, PatchTrackingAvailable = patchTracking };

    [Fact]
    public void EntityAndClientBothOnOldConfig_NoForcePatch()
    {
        // Entity pinned 1.0, client code-version 1.0. Neither side of the 2.0 boundary —
        // direct execution OK.
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            new[] { Boundary("Cfg", "2.0") },
            new[] { ServerMethod("IService", "Cfg") },
            pinned: V(1, 0), clientCode: V(1, 0));
        Assert.Empty(result);
    }

    [Fact]
    public void EntityOldClientNew_NoForcePatch()
    {
        // Entity pinned 1.0 (old config bytes), client code-version 2.0 (NEW schema). Under
        // open-closed evolution, the new client KNOWS the old schema is a strict subset of its
        // new class — deprecated fields are present in code, just unused by old methods. The
        // new client can natively replay the old method body against 1.0 bytes; no patch needed.
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            new[] { Boundary("Cfg", "2.0") },
            new[] { ServerMethod("IService", "Cfg") },
            pinned: V(1, 0), clientCode: V(2, 0));
        Assert.Empty(result);
    }

    [Fact]
    public void EntityNewClientOld_ForcePatch()
    {
        // Entity pinned 2.0 (NEW bytes), client code-version 1.0 (OLD schema). Client's
        // config class is missing the new fields entirely — it cannot deserialize the 2.0
        // bytes correctly. Force-patch: client applies the state diff and never tries to
        // interpret 2.0 structure. The asymmetric "only old-on-new" case.
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            new[] { Boundary("Cfg", "2.0") },
            new[] { ServerMethod("IService", "Cfg") },
            pinned: V(2, 0), clientCode: V(1, 0));
        Assert.Equal(new[] { "IService" }, result);
    }

    [Fact]
    public void EntityAndClientBothOnNewConfig_NoForcePatch()
    {
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            new[] { Boundary("Cfg", "2.0") },
            new[] { ServerMethod("IService", "Cfg") },
            pinned: V(2, 0), clientCode: V(2, 0));
        Assert.Empty(result);
    }

    [Fact]
    public void MultipleBoundariesAcrossMultipleConfigs()
    {
        // CfgA boundary at 2.0; CfgB boundary at 3.0. Client at 1.0 (below both), entity
        // pinned at 2.5. Only CfgA's boundary triggers (clientCode 1.0 < 2.0 <= pinned 2.5).
        // CfgB's boundary at 3.0 does NOT trigger (pinned 2.5 < 3.0).
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            new[] { Boundary("CfgA", "2.0"), Boundary("CfgB", "3.0") },
            new[] { ServerMethod("IA", "CfgA"), ServerMethod("IB", "CfgB") },
            pinned: V(2, 5), clientCode: V(1, 0));
        Assert.Equal(new[] { "IA" }, result);
    }

    [Fact]
    public void FarOlderClient_StillForcePatch()
    {
        // Distance doesn't matter; presence of any crossed boundary is what counts.
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            new[] { Boundary("Cfg", "2.0") },
            new[] { ServerMethod("IService", "Cfg") },
            pinned: V(3, 5), clientCode: V(1, 0));
        Assert.Equal(new[] { "IService" }, result);
    }

    [Fact]
    public void ClientExactlyAtBoundary_NoForcePatch()
    {
        // Client code-version EXACTLY at the boundary (2.0). Client knows the new schema
        // (built against >= 2.0). No boundary in (2.0, 3.0] in this test → no force-patch.
        var result = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
            new[] { Boundary("Cfg", "2.0") },
            new[] { ServerMethod("IService", "Cfg") },
            pinned: V(3, 0), clientCode: V(2, 0));
        Assert.Empty(result);
    }

    // ── SplitByPatchTrackability: boundary force-patch is only serveable when the service has
    //    a patch-tracking copy; an opted-out service must be rejected, not empty-patched. ──

    [Fact]
    public void Split_PatchTrackableService_GoesToForcePatch()
    {
        SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.SplitByPatchTrackability(
            new[] { "IService" },
            new[] { ServerMethod("IService", "Cfg", patchTracking: true) },
            out var forcePatch, out var rejected);
        Assert.Equal(new[] { "IService" }, forcePatch);
        Assert.Empty(rejected);
    }

    [Fact]
    public void Split_OptedOutService_GoesToRejected()
    {
        // PatchTracking = false → no {Impl}_PatchTracked copy → can't ship a diff → reject.
        SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.SplitByPatchTrackability(
            new[] { "IService" },
            new[] { ServerMethod("IService", "Cfg", patchTracking: false) },
            out var forcePatch, out var rejected);
        Assert.Empty(forcePatch);
        Assert.Equal(new[] { "IService" }, rejected);
    }

    [Fact]
    public void Split_MixedServices_PartitionedByTrackability()
    {
        SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.SplitByPatchTrackability(
            new[] { "ITrackable", "IOptedOut" },
            new[]
            {
                ServerMethod("ITrackable", "Cfg", patchTracking: true),
                ServerMethod("IOptedOut", "Cfg", patchTracking: false),
            },
            out var forcePatch, out var rejected);
        Assert.Equal(new[] { "ITrackable" }, forcePatch);
        Assert.Equal(new[] { "IOptedOut" }, rejected);
    }
}
