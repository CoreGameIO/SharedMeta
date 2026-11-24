using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace SharedMeta.Inspector.Nats;

/// <summary>
/// One entity's events read back from the stream by its subject. A full scan of that subject
/// (JetStream has no "last N per subject"), keeping the newest <c>maxEvents</c>.
/// </summary>
internal sealed class NatsEventHistory : IMetaEventHistory
{
    private const int FetchBatch = 500;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    private readonly INatsJSContext _js;
    private readonly string _stream;
    private readonly NatsSubjects _subjects;

    public NatsEventHistory(INatsJSContext js, string stream, NatsSubjects subjects)
    {
        _js = js;
        _stream = stream;
        _subjects = subjects;
    }

    public async Task<(IReadOnlyList<MetaInspectorEvent> Events, bool Truncated)> ReadEntityAsync(
        string stateType, string entityId, int maxEvents, CancellationToken cancellationToken)
    {
        // A short name ("ProfileState") is what a caller without the method table has: match it
        // against every state type of that entity id, then keep the events whose type ends with it.
        bool shortName = stateType.IndexOfAny(new[] { '.', '+' }) < 0;
        var subject = shortName ? _subjects.ForAnyStateType(entityId) : _subjects.ForEntity(stateType, entityId);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        INatsJSConsumer? consumer = null;
        try
        {
            consumer = await _js.CreateOrderedConsumerAsync(_stream, new NatsJSOrderedConsumerOpts
            {
                FilterSubjects = new[] { subject },
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            }, timeout.Token).ConfigureAwait(false);

            var kept = new Queue<MetaInspectorEvent>();
            bool truncated = false;
            while (true)
            {
                int received = 0;
                ulong pending = 0;
                await foreach (var msg in consumer.FetchNoWaitAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = FetchBatch }, cancellationToken: timeout.Token).ConfigureAwait(false))
                {
                    received++;
                    pending = msg.Metadata?.NumPending ?? 0;
                    if (msg.Data is not { Length: > 0 } data) continue;
                    MetaInspectorEvent? e;
                    try { e = NatsEventCodec.Decode(data); }
                    catch { continue; }
                    if (e == null || (shortName && !IsShortNameOf(stateType, e.StateType))) continue;
                    e.BrokerSequence = msg.Metadata?.Sequence.Stream;
                    kept.Enqueue(e);
                    if (kept.Count > Math.Max(1, maxEvents))
                    {
                        kept.Dequeue();
                        truncated = true;
                    }
                }
                if (received == 0 || pending == 0) break;
            }
            return (kept.ToList(), truncated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Reading the history of {entityId} from stream {_stream} took over {ReadTimeout.TotalSeconds:0} s.");
        }
        finally
        {
            // One consumer per query: removed at once instead of idling on the server until its
            // inactivity threshold, which repeated queries would pile up against max_consumers.
            if (consumer != null)
            {
                try { await _js.DeleteConsumerAsync(_stream, consumer.Info.Name, CancellationToken.None).ConfigureAwait(false); }
                catch { /* already gone, or the server is unreachable; it expires on its own */ }
            }
        }
    }

    private static bool IsShortNameOf(string shortName, string? fullName)
        => fullName != null
           && (fullName == shortName
               || (fullName.EndsWith(shortName, StringComparison.Ordinal)
                   && fullName[fullName.Length - shortName.Length - 1] is '.' or '+'));
}
