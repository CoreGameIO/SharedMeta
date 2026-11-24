using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace SharedMeta.Inspector.Nats;

/// <summary>
/// Connects the process's <see cref="InspectorRuntime"/> to NATS: stream sink, stream source,
/// history and shared capture settings. Set up in the background with retries — an inspector
/// without its broker must never keep the game server from starting.
/// </summary>
internal sealed class NatsInspectorBridge : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan SetupRetryDelay = TimeSpan.FromSeconds(5);

    private readonly InspectorRuntime _runtime;
    private readonly NatsInspectorOptions _options;
    private readonly ILogger _logger;
    private readonly NatsSubjects _subjects;
    private readonly CancellationTokenSource _stop = new();
    // The background setup adds loops and sets the client while StopAsync may already run.
    private readonly object _lock = new();
    private readonly List<Task> _loops = new();
    private Task? _setup;
    private NatsClient? _client;

    public NatsInspectorBridge(InspectorRuntime runtime, NatsInspectorOptions options, ILoggerFactory? loggerFactory)
    {
        _runtime = runtime;
        _options = options;
        _logger = loggerFactory?.CreateLogger("SharedMeta.Inspector.Nats") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _subjects = new NatsSubjects(options.SubjectPrefix);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _setup = Task.Run(() => SetupWithRetriesAsync(_stop.Token));
        return Task.CompletedTask;
    }

    private async Task SetupWithRetriesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SetupAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Inspector could not set up NATS at {Url}; retrying in {Delay}", _options.Url, SetupRetryDelay);
                NatsClient? failed;
                lock (_lock)
                {
                    failed = _client;
                    _client = null;
                }
                if (failed != null) await failed.DisposeAsync().ConfigureAwait(false);
                try { await Task.Delay(SetupRetryDelay, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task SetupAsync(CancellationToken cancellationToken)
    {
        var opts = new NatsOpts { Url = _options.Url, Name = "sharedmeta-inspector:" + _runtime.Silo };
        if (_options.ConfigureConnection != null) opts = _options.ConfigureConnection(opts);
        var client = new NatsClient(opts);
        lock (_lock) _client = client;
        await client.ConnectAsync().ConfigureAwait(false);
        var js = client.CreateJetStreamContext();

        if (_options.ManageStream)
        {
            await js.CreateOrUpdateStreamAsync(new StreamConfig(_options.StreamName, new[] { _subjects.All })
            {
                MaxAge = _options.MaxAge,
                MaxBytes = _options.MaxBytes,
                NumReplicas = _options.Replicas,
                Storage = StreamConfigStorage.File,
                Discard = StreamConfigDiscard.Old,
            }, cancellationToken).ConfigureAwait(false);
        }

        NatsControlChannel? control = null;
        if (_options.ShareCaptureSettings)
        {
            var kvContext = client.CreateKeyValueStoreContext();
            var kv = _options.ManageStream
                ? await kvContext.CreateOrUpdateStoreAsync(new NatsKVConfig(_options.ControlBucket) { History = 1, NumberOfReplicas = _options.Replicas }, cancellationToken).ConfigureAwait(false)
                : await kvContext.GetStoreAsync(_options.ControlBucket, cancellationToken).ConfigureAwait(false);
            control = new NatsControlChannel(kv, _runtime, _logger);
            // Only a process that captures seeds the shared settings with its configured ones: a
            // standalone inspector starting first would otherwise switch capture off cluster-wide.
            if (_options.Publish) await control.SeedAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Wired only once every resource exists, so a failed setup leaves the runtime local. The
        // store stops taking local events before the sink starts publishing them, or the ones in
        // between would reach the store twice (directly and back from the stream).
        if (_options.Consume) _runtime.UseEventSource();
        if (_options.Publish)
            _runtime.AddSink(new NatsEventSink(client.Connection, _subjects, _logger));
        if (_options.History)
            _runtime.UseHistory(new NatsEventHistory(js, _options.StreamName, _subjects));
        lock (_lock)
        {
            if (control != null)
            {
                _runtime.UseControlChannel(control);
                _loops.Add(Task.Run(() => control.WatchAsync(_runtime.SetCaptureSettings, _stop.Token)));
                if (_options.Publish)
                {
                    var ctrl = control;
                    _runtime.OnBound(() =>
                    {
                        if (_runtime.BuildLocalSchema() is not { } schema) return;
                        _ = Task.Run(async () =>
                        {
                            try { await ctrl.PublishSchemaAsync(schema, _stop.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { }
                            catch (Exception ex) { _logger.LogWarning(ex, "Inspector failed to publish method schema to NATS KV"); }
                        });
                    });
                }
                else
                {
                    _loops.Add(Task.Run(() => control.WatchSchemaAsync(_runtime.SetRemoteSchema, _stop.Token)));
                }
            }
            if (_options.Consume)
            {
                var source = new NatsEventSource(js, _options.StreamName, _subjects, _options.WarmStart, _logger);
                _loops.Add(Task.Run(() => source.RunAsync(_runtime.IngestAsync, _stop.Token)));
            }
        }

        var roles = new List<string>();
        if (_options.Publish) roles.Add("publish");
        if (_options.Consume) roles.Add("consume");
        if (_options.History) roles.Add("history");
        if (control != null) roles.Add("shared capture settings");
        _runtime.DescribeDistribution($"NATS JetStream {_options.Url}, stream {_options.StreamName} ({string.Join(", ", roles)})");
        _logger.LogInformation("Inspector connected to NATS {Url}: stream {Stream}, {Roles}", _options.Url, _options.StreamName, string.Join(", ", roles));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Hand the queued events to the broker before the connection goes.
        bool connected;
        lock (_lock) connected = _client != null && _runtime.Distribution != null;
        if (_options.Publish && connected)
        {
            try { await _runtime.FlushAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or System.Threading.Channels.ChannelClosedException) { }
        }

        // Setup first, so no loop is added after the snapshot below.
        _stop.Cancel();
        try
        {
            if (_setup != null) await _setup.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task[] loops;
            lock (_lock) loops = _loops.ToArray();
            await Task.WhenAll(loops).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        NatsClient? client;
        lock (_lock)
        {
            client = _client;
            _client = null;
        }
        if (client != null) await client.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
