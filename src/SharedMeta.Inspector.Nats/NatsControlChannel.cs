using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NATS.Client.KeyValueStore;

namespace SharedMeta.Inspector.Nats;

/// <summary>
/// Capture settings in one key of a JetStream key-value bucket. Changes are compare-and-set on the
/// key's revision, so concurrent watches from two inspectors both land; every process watches the
/// key and applies what it sees. Silos also publish their method schema under <c>schema</c> so
/// standalone inspectors can serve <c>list_methods</c> and resolve short state type names.
/// </summary>
internal sealed class NatsControlChannel : IInspectorControlChannel
{
    private const string Key = "capture";
    private const string SchemaKey = "schema";
    private const int MaxAttempts = 16;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly INatsKVStore _kv;
    private readonly InspectorRuntime _runtime;
    private readonly ILogger _logger;

    public NatsControlChannel(INatsKVStore kv, InspectorRuntime runtime, ILogger logger)
    {
        _kv = kv;
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>Puts this process's settings in the bucket unless another process already did.</summary>
    public async Task SeedAsync(CancellationToken cancellationToken)
        => await _kv.TryCreateAsync(Key, Encode(_runtime.GetCaptureSettings()), cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<InspectorCaptureSettings> UpdateAsync(Action<InspectorCaptureSettings> change, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await _kv.TryGetEntryAsync<byte[]>(Key, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!current.Success)
            {
                var fresh = _runtime.GetCaptureSettings();
                change(fresh);
                if ((await _kv.TryCreateAsync(Key, Encode(fresh), cancellationToken: cancellationToken).ConfigureAwait(false)).Success)
                    return fresh;
                continue;
            }

            var settings = Decode(current.Value.Value) ?? _runtime.GetCaptureSettings();
            change(settings);
            var written = await _kv.TryUpdateAsync(Key, Encode(settings), current.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (written.Success) return settings;
        }
        throw new InvalidOperationException("Capture settings kept changing under this update; try again.");
    }

    public async Task WatchAsync(Action<InspectorCaptureSettings> apply, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var entry in _kv.WatchAsync<byte[]>(Key, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (entry.Operation != NatsKVOperation.Put || entry.Value == null) continue;
                    if (Decode(entry.Value) is { } settings) apply(settings);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Inspector capture-settings watch on bucket {Bucket} failed; retrying", _kv.Bucket);
                try { await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Publishes this silo's method table under <c>schema</c> so standalone inspectors can read it.</summary>
    public async Task PublishSchemaAsync(JsonObject schema, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(schema);
        await _kv.PutAsync(SchemaKey, bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Watches <c>schema</c> in the bucket and forwards every published method table to <paramref name="apply"/>.</summary>
    public async Task WatchSchemaAsync(Action<JsonObject?> apply, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var entry in _kv.WatchAsync<byte[]>(SchemaKey, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (entry.Operation != NatsKVOperation.Put || entry.Value is not { Length: > 0 }) continue;
                    try
                    {
                        if (JsonNode.Parse(entry.Value) is JsonObject obj) apply(obj);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Inspector ignored unreadable method schema in bucket {Bucket}", _kv.Bucket);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Inspector method-schema watch on bucket {Bucket} failed; retrying", _kv.Bucket);
                try { await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static byte[] Encode(InspectorCaptureSettings settings) => JsonSerializer.SerializeToUtf8Bytes(settings, s_json);

    private InspectorCaptureSettings? Decode(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            var settings = JsonSerializer.Deserialize<InspectorCaptureSettings>(bytes, s_json);
            if (settings == null) return null;
            // Keys are compared ordinally everywhere else; deserialization builds default comparers.
            settings.MethodLevels = new Dictionary<string, InspectorLevel>(settings.MethodLevels ?? new(), StringComparer.Ordinal);
            settings.WatchedEntities = new Dictionary<string, InspectorLevel>(settings.WatchedEntities ?? new(), StringComparer.Ordinal);
            settings.WatchedPlayers = new Dictionary<string, InspectorLevel>(settings.WatchedPlayers ?? new(), StringComparer.Ordinal);
            return settings;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Inspector ignored unreadable capture settings in bucket {Bucket}", _kv.Bucket);
            return null;
        }
    }
}
