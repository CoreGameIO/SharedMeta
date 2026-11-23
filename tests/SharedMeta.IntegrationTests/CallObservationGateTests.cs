using SharedMeta.Core.Diagnostics;
using SharedMeta.Server.Core.Telemetry;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// An observer implementing <see cref="IMetaCallObservationGate"/> only costs per-call work while
/// its gate is open; session notifications reach it regardless.
/// </summary>
public class CallObservationGateTests
{
    private sealed class GatedObserver : IMetaServerObserver, IMetaCallObservationGate
    {
        public volatile bool Open;
        public int Calls, SessionsStarted, SessionsEnded;

        public bool ObservesCalls => Open;
        public void OnCallCompleted(in MetaCallInfo call) => Interlocked.Increment(ref Calls);
        public void OnSessionStarted(string playerId, string sessionId, string? clientVersion, bool resumed)
            => Interlocked.Increment(ref SessionsStarted);
        public void OnSessionEnded(string playerId, string sessionId, MetaSessionEndReason reason, double durationMs)
            => Interlocked.Increment(ref SessionsEnded);
    }

    private sealed class PlainObserver : IMetaServerObserver
    {
        public int Calls;
        public void OnCallCompleted(in MetaCallInfo call) => Interlocked.Increment(ref Calls);
        public void OnSessionStarted(string playerId, string sessionId, string? clientVersion, bool resumed) { }
        public void OnSessionEnded(string playerId, string sessionId, MetaSessionEndReason reason, double durationMs) { }
    }

    private static MetaCallInfo Call() => new MetaCallInfo();

    [Fact]
    public void ClosedGate_SkipsCallsButKeepsSessions()
    {
        var gated = new GatedObserver();
        var telemetry = new SharedMetaTelemetry(null, new IMetaServerObserver[] { gated }, null);

        Assert.False(telemetry.HasObservers);
        telemetry.CallCompleted(Call());
        telemetry.SessionStarted("p", "s", "1.0.0", resumed: false);
        telemetry.SessionEnded("p", "s", MetaSessionEndReason.Graceful, 1);

        Assert.Equal(0, gated.Calls);
        Assert.Equal(1, gated.SessionsStarted);
        Assert.Equal(1, gated.SessionsEnded);
    }

    [Fact]
    public void OpeningTheGate_TurnsCallsOn()
    {
        var gated = new GatedObserver();
        var telemetry = new SharedMetaTelemetry(null, new IMetaServerObserver[] { gated }, null);

        gated.Open = true;
        Assert.True(telemetry.HasObservers);
        telemetry.CallCompleted(Call());
        Assert.Equal(1, gated.Calls);

        gated.Open = false;
        Assert.False(telemetry.HasObservers);
    }

    [Fact]
    public void UngatedObserver_StillSeesEveryCall_WhileAClosedGateIsSkipped()
    {
        var gated = new GatedObserver();
        var plain = new PlainObserver();
        var telemetry = new SharedMetaTelemetry(null, new IMetaServerObserver[] { gated, plain }, null);

        Assert.True(telemetry.HasObservers);
        telemetry.CallCompleted(Call());

        Assert.Equal(1, plain.Calls);
        Assert.Equal(0, gated.Calls);
    }
}
