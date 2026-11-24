using Microsoft.Extensions.Logging;
using NATS.Client.Core;

namespace SharedMeta.Inspector.Nats;

/// <summary>
/// Publishes events to the stream's subjects with core publish: fire-and-forget (at most once), so
/// a slow or absent broker never holds up the encoder longer than the publish timeout.
/// </summary>
internal sealed class NatsEventSink : IMetaEventSink
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(5);
    // Room for the protocol line and headers within the server's max payload.
    private const int Overhead = 1024;

    private readonly INatsConnection _connection;
    private readonly NatsSubjects _subjects;
    private readonly ILogger _logger;

    public NatsEventSink(INatsConnection connection, NatsSubjects subjects, ILogger logger)
    {
        _connection = connection;
        _subjects = subjects;
        _logger = logger;
    }

    public async ValueTask WriteAsync(IReadOnlyList<MetaInspectorEvent> events)
    {
        using var timeout = new CancellationTokenSource(PublishTimeout);
        var limit = (_connection.ServerInfo?.MaxPayload ?? 1024 * 1024) - Overhead;
        foreach (var e in events)
        {
            var bytes = NatsEventCodec.Encode(e);
            if (bytes.Length > limit)
            {
                bytes = NatsEventCodec.Encode(NatsEventCodec.WithoutPayloads(e));
                if (bytes.Length > limit)
                {
                    _logger.LogWarning("Inspector event {Kind} of {Entity} is over the broker's message limit even without payloads; not published", e.Kind, e.EntityKey);
                    continue;
                }
            }
            await _connection.PublishAsync(_subjects.For(e), bytes, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
    }
}
