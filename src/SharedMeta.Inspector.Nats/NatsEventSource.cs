using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace SharedMeta.Inspector.Nats;

/// <summary>
/// Every event on the stream through an ordered consumer (ephemeral, resumes itself after
/// reconnects), delivered in batches: up to <see cref="MaxBatch"/>, or whatever has arrived once
/// the consumer has caught up.
/// </summary>
internal sealed class NatsEventSource : IMetaEventSource
{
    private const int MaxBatch = 256;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly INatsJSContext _js;
    private readonly string _stream;
    private readonly NatsSubjects _subjects;
    private readonly TimeSpan _warmStart;
    private readonly ILogger _logger;

    public NatsEventSource(INatsJSContext js, string stream, NatsSubjects subjects, TimeSpan warmStart, ILogger logger)
    {
        _js = js;
        _stream = stream;
        _subjects = subjects;
        _warmStart = warmStart;
        _logger = logger;
    }

    public async Task RunAsync(Func<IReadOnlyList<MetaInspectorEvent>, ValueTask> deliver, CancellationToken cancellationToken)
    {
        // Resumes after the last *delivered* stream position: a batch still being filled when the
        // consumer fails is read again, not skipped.
        ulong nextSequence = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var opts = new NatsJSOrderedConsumerOpts { FilterSubjects = new[] { _subjects.All } };
                if (nextSequence > 0)
                    opts = opts with { DeliverPolicy = ConsumerConfigDeliverPolicy.ByStartSequence, OptStartSeq = nextSequence };
                else if (_warmStart > TimeSpan.Zero)
                    opts = opts with { DeliverPolicy = ConsumerConfigDeliverPolicy.ByStartTime, OptStartTime = DateTimeOffset.UtcNow - _warmStart };
                else
                    opts = opts with { DeliverPolicy = ConsumerConfigDeliverPolicy.New };

                var consumer = await _js.CreateOrderedConsumerAsync(_stream, opts, cancellationToken).ConfigureAwait(false);
                var batch = new List<MetaInspectorEvent>(MaxBatch);
                ulong batchEnd = 0;
                await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    var metadata = msg.Metadata;
                    if (metadata is { } m) batchEnd = m.Sequence.Stream;
                    if (msg.Data is { Length: > 0 } data)
                    {
                        try
                        {
                            if (NatsEventCodec.Decode(data) is { } e)
                            {
                                e.BrokerSequence = metadata?.Sequence.Stream;
                                batch.Add(e);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Inspector skipped an undecodable event on {Subject}", msg.Subject);
                        }
                    }
                    if (batch.Count >= MaxBatch || metadata?.NumPending == 0)
                    {
                        if (batch.Count > 0) await deliver(batch).ConfigureAwait(false);
                        if (batchEnd > 0) nextSequence = batchEnd + 1;
                        batch = new List<MetaInspectorEvent>(MaxBatch);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Inspector event consumer on stream {Stream} failed; retrying", _stream);
                try { await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
