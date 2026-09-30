using System.Collections.Concurrent;
using SharedMeta.Client;
using SharedMeta.Core.Logging;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Test.Meta1;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// The replay tape is positional with no tags. A body that reads fewer recorded values than the
/// server wrote took a different path than the server — the classic "ServerRandom inside a branch
/// only one side took" desync. That must be named at the end of the replay, not surface later as
/// a wrong value somewhere unrelated.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ReplayTapeAlignmentTests
{
    [Fact]
    public void UnreadRecordedValues_AreReported()
    {
        var errors = CaptureErrors(() => Replay(recorded: 2, read: 1, entityId: "tape_left_over"));
        Assert.Contains(errors, e => e.Contains("tape_left_over") && e.Contains("[Replay]"));
    }

    [Fact]
    public void FullyReadTape_ReportsNothing()
    {
        var errors = CaptureErrors(() => Replay(recorded: 2, read: 2, entityId: "tape_consumed"));
        Assert.DoesNotContain(errors, e => e.Contains("tape_consumed"));
    }

    private static void Replay(int recorded, int read, string entityId)
    {
        var serializer = new MemoryPackMetaSerializer();
        using var writer = serializer.CreateWriter();
        for (int i = 0; i < recorded; i++) writer.Write(i);
        var tape = writer.Complete().ToArray();

        var ctx = new ClientMetaContext<CounterState>(new CounterState(), serializer) { EntityId = entityId };
        ctx.BeginReplay(tape);
        for (int i = 0; i < read; i++) ctx.Reader.Read<int>();
        ctx.EndReplay();
    }

    private static List<string> CaptureErrors(Action action)
    {
        var previous = MetaLog.Logger;
        var capture = new CapturingLogger();
        MetaLog.SetLogger(capture);
        try { action(); }
        finally { MetaLog.SetLogger(previous); }
        return capture.Errors.ToList();
    }

    private sealed class CapturingLogger : IMetaLogger
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public bool IsEnabled(MetaLogLevel level) => true;
        public void Log(MetaLogLevel level, string message)
        {
            if (level == MetaLogLevel.Error) Errors.Enqueue(message);
        }
        public void Log(MetaLogLevel level, string message, Exception exception) => Log(level, message);
    }
}
