using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core.Diagnostics;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Inspector;

/// <summary>
/// The inspector of one process: current capture rules, the queue from the capture points, the
/// background encoder (decode → JSON → redact → sinks) and the in-memory store it feeds.
/// <para>
/// One instance per process. Hosts whose web server and silo use different service containers
/// create one and pass it to <c>AddSharedMetaInspector</c> in both.
/// </para>
/// </summary>
public sealed class InspectorRuntime : IAsyncDisposable
{
    private static readonly Meter s_meter = new("SharedMeta.Inspector");
    private static readonly Counter<long> s_dropped = s_meter.CreateCounter<long>(
        "sharedmeta.inspector.dropped", unit: "{event}", description: "Events dropped because the encoder queue was full.");

    private readonly Channel<RawEvent> _queue;
    private readonly List<IMetaEventSink> _sinks = new();
    private readonly Task _encoder;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _settingsLock = new();
    private readonly SeenEntities _seen;
    private volatile CaptureRules _rules;
    private long _dropped;
    private int _bound;

    public InspectorRuntime(InspectorOptions? options = null)
    {
        Options = options ?? new InspectorOptions();
        Json = new MetaJson(Options.RedactMembers);
        Store = new InMemoryEventStore(Options.MaxEvents, Options.MaxBytes);
        _sinks.Add(Store);
        if (!string.IsNullOrEmpty(Options.JsonlPath)) _sinks.Add(new JsonlFileSink(Options.JsonlPath!));
        _seen = new SeenEntities(Options.SeenEntitiesCapacity);
        _rules = new CaptureRules(Options.Capture.Clone(), null);
        // Wait, not DropWrite: capture points only TryWrite, which then refuses when full (and is
        // counted as a drop), while FlushAsync can wait for room. DropWrite reports success.
        _queue = Channel.CreateBounded<RawEvent>(new BoundedChannelOptions(Math.Max(16, Options.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        ServerObserver = new InspectorServerObserver(this);
        _encoder = Task.Factory.StartNew(EncodeLoopAsync, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    public InspectorOptions Options { get; }
    public MetaJson Json { get; }
    public InMemoryEventStore Store { get; }
    public SessionTracker Sessions { get; } = new();

    /// <summary>
    /// Transport observer (client replies, sessions). Registered by <c>AddSharedMetaInspector</c>;
    /// hosts that build <c>SharedMetaTelemetry</c> by hand pass it in themselves.
    /// </summary>
    public SharedMeta.Core.Diagnostics.IMetaServerObserver ServerObserver { get; }

    internal CaptureRules Rules => _rules;

    // ── distribution (broker) ──────────────────────────────────────────────────

    private volatile bool _storeFedBySource;

    /// <summary>Shared capture settings; null = this process only.</summary>
    public IInspectorControlChannel? Control { get; private set; }

    /// <summary>Durable per-entity history beyond the in-memory window; null = store only.</summary>
    public IMetaEventHistory? History { get; private set; }

    /// <summary>Human-readable description of the broker wiring, for status output.</summary>
    public string? Distribution { get; private set; }

    /// <summary>
    /// Feeds the store from a broker instead of from this process's encoder: the local events go
    /// to the sinks (the broker) and come back with everyone else's through <see cref="IngestAsync"/>.
    /// </summary>
    public void UseEventSource()
    {
        _storeFedBySource = true;
        lock (_sinks) _sinks.Remove(Store);
    }

    public void UseControlChannel(IInspectorControlChannel control) => Control = control;

    public void UseHistory(IMetaEventHistory history) => History = history;

    public void DescribeDistribution(string description) => Distribution = description;

    /// <summary>Adds events from a source (other silos included) to the store.</summary>
    public ValueTask IngestAsync(IReadOnlyList<MetaInspectorEvent> events)
    {
        foreach (var e in events) TouchSeen(e);
        return Store.WriteAsync(events);
    }

    private void TouchSeen(MetaInspectorEvent e)
    {
        if (e.StateType != null) LearnStateType(e.StateType);
        if (e.EntityKey != null && e.StateType != null && e.EntityId != null)
            _seen.Touch(e.EntityKey, e.StateType, e.EntityId, e.EntitySequence, e.StartedAtUtc);
    }

    // Short → full state type names seen on events: a standalone inspector has no method table to
    // resolve "ProfileState" with, but its events carry the full names.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _stateTypesByShortName = new(StringComparer.Ordinal);

    internal void LearnStateType(string fullName)
    {
        var dot = fullName.LastIndexOfAny(new[] { '.', '+' });
        _stateTypesByShortName.TryAdd(dot >= 0 ? fullName.Substring(dot + 1) : fullName, fullName);
    }

    // ── services bound from the silo container ─────────────────────────────────

    public IMetaSerializer? Serializer { get; private set; }
    public IMetaMethodDescriptors? Descriptors { get; private set; }
    public MetaServerSignature? Signature { get; private set; }
    public IGrainFactory? GrainFactory { get; private set; }
    public IEntityGrainResolver? EntityGrainResolver { get; private set; }
    public string Silo { get; private set; } = Environment.MachineName;
    private ILogger _logger = NullLogger.Instance;

    /// <summary>True when the generated providers report calls (a call tap was referenced at build time).</summary>
    public bool CaptureAvailable => Descriptors != null;

    /// <summary>Takes what the encoder and the live tools need from the silo container. First call wins.</summary>
    public void Bind(IServiceProvider services)
    {
        if (Interlocked.Exchange(ref _bound, 1) == 1) return;
        Serializer = services.GetService<IMetaSerializer>();
        Descriptors = services.GetService<IMetaMethodDescriptors>();
        Signature = services.GetService<MetaServerSignature>();
        GrainFactory = services.GetService<IGrainFactory>();
        EntityGrainResolver = services.GetService<IEntityGrainResolver>();
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger("SharedMeta.Inspector") ?? NullLogger.Instance;
        try
        {
            var siloName = services.GetService<Orleans.Runtime.ILocalSiloDetails>()?.Name;
            if (!string.IsNullOrEmpty(siloName)) Silo = siloName!;
        }
        catch (InvalidOperationException) { /* not a silo container */ }
        lock (_settingsLock) _rules = new CaptureRules(_rules.Settings, Signature);
    }

    /// <summary>Adds a sink after the in-memory store (e.g. a broker).</summary>
    public void AddSink(IMetaEventSink sink)
    {
        lock (_sinks) _sinks.Add(sink);
    }

    // ── capture settings ───────────────────────────────────────────────────────

    public InspectorCaptureSettings GetCaptureSettings() => _rules.Settings.Clone();

    public void SetCaptureSettings(InspectorCaptureSettings settings)
    {
        lock (_settingsLock) _rules = new CaptureRules(settings.Clone(), Signature);
    }

    /// <summary>
    /// Changes the capture settings everywhere they are shared: through the control channel when
    /// one is attached (and here at once, before the channel echoes it back), else in this process.
    /// </summary>
    public async Task<InspectorCaptureSettings> UpdateCaptureSettingsAsync(Action<InspectorCaptureSettings> change, CancellationToken cancellationToken = default)
    {
        var control = Control;
        if (control == null) return UpdateCaptureSettings(change);
        var next = await control.UpdateAsync(change, cancellationToken).ConfigureAwait(false);
        SetCaptureSettings(next);
        return next.Clone();
    }

    /// <summary>Applies <paramref name="change"/> to a copy of this process's settings atomically.</summary>
    public InspectorCaptureSettings UpdateCaptureSettings(Action<InspectorCaptureSettings> change)
    {
        lock (_settingsLock)
        {
            var next = _rules.Settings.Clone();
            change(next);
            _rules = new CaptureRules(next, Signature);
            return next.Clone();
        }
    }

    // ── capture side (grain turns) ─────────────────────────────────────────────

    internal void CopyPayload(ReadOnlySpan<byte> bytes, out byte[]? copy, out int skipped)
    {
        if (bytes.Length > Options.MaxPayloadBytes)
        {
            copy = null;
            skipped = bytes.Length;
            return;
        }
        copy = bytes.ToArray();
        skipped = 0;
    }

    internal void Enqueue(RawEvent raw)
    {
        if (_queue.Writer.TryWrite(raw)) return;
        Interlocked.Increment(ref _dropped);
        s_dropped.Add(1);
    }

    public long Dropped => Interlocked.Read(ref _dropped);
    public int Queued => _queue.Reader.Count;

    /// <summary>Waits until every event queued so far has reached the store (for tests and tools).</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        var marker = new RawEvent { Kind = FlushKind };
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_flushes) _flushes.Add((marker, done));
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _queue.Writer.WriteAsync(marker, cts.Token);
            await done.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Inspector queue did not drain within {timeout}.");
        }
        finally
        {
            lock (_flushes) _flushes.RemoveAll(f => ReferenceEquals(f.Marker, marker));
        }
    }

    private const string FlushKind = "__flush";
    private readonly List<(RawEvent Marker, TaskCompletionSource Done)> _flushes = new();

    // ── encoder ────────────────────────────────────────────────────────────────

    private async Task EncodeLoopAsync()
    {
        var reader = _queue.Reader;
        var batch = new List<MetaInspectorEvent>(256);
        var folded = new Dictionary<(string Entity, long Seq), MetaInspectorEvent>();
        var completedFlushes = new List<RawEvent>();
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token))
            {
                while (batch.Count < 512 && reader.TryRead(out var raw))
                {
                    if (raw.Kind == FlushKind) { completedFlushes.Add(raw); continue; }
                    try
                    {
                        var e = Encode(raw);
                        e.ApproxBytes = Size(raw);
                        PlaceInOperation(e);
                        if (e.Kind == InspectorEventKinds.BroadcastSent && raw.Reason != "watched"
                            && !_rules.Settings.BroadcastPerSubscriber && e.EntityKey != null)
                        {
                            // One event per operation, listing its recipients.
                            var key = (e.EntityKey, e.EntitySequence);
                            if (folded.TryGetValue(key, out var existing))
                            {
                                existing.SubscriberCount = (existing.SubscriberCount ?? 1) + 1;
                                if (e.TargetPlayer != null) (existing.TargetPlayers ??= new()).Add(e.TargetPlayer);
                                continue;
                            }
                            e.SubscriberCount = 1;
                            e.TargetPlayers = e.TargetPlayer != null ? new List<string> { e.TargetPlayer } : null;
                            e.TargetPlayer = null;
                            folded[key] = e;
                        }
                        batch.Add(e);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Inspector failed to encode a {Kind} event", raw.Kind);
                    }
                }

                if (!_storeFedBySource)
                {
                    foreach (var e in batch) TouchSeen(e);
                }

                if (batch.Count > 0)
                {
                    IMetaEventSink[] sinks;
                    lock (_sinks) sinks = _sinks.ToArray();
                    foreach (var sink in sinks)
                    {
                        try { await sink.WriteAsync(batch); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Inspector sink {Sink} failed", sink.GetType().Name); }
                    }
                }
                batch.Clear();
                folded.Clear();

                foreach (var marker in completedFlushes) CompleteFlush(marker);
                completedFlushes.Clear();
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── reply placement (encoder thread only) ──────────────────────────────────

    private const int RecentCallCapacity = 8192;
    private readonly Dictionary<string, (string EntityKey, long Sequence)> _recentCalls = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentCallOrder = new();

    /// <summary>
    /// The transport reports a reply without the entity's sequence. It follows the dispatch it
    /// answers in the queue, so it takes that dispatch's sequence and goes last in the operation;
    /// a reply with no captured dispatch (rejected before the entity ran it) goes after the
    /// entity's latest known operation.
    /// </summary>
    private void PlaceInOperation(MetaInspectorEvent e)
    {
        if (e.Kind == InspectorEventKinds.CallCompleted)
        {
            if (e.CallId == null || e.EntityKey == null || e.CallKind is not ("rpc" or "query" or "signal")) return;
            if (!_recentCalls.ContainsKey(e.CallId))
            {
                _recentCallOrder.Enqueue(e.CallId);
                while (_recentCallOrder.Count > RecentCallCapacity) _recentCalls.Remove(_recentCallOrder.Dequeue());
            }
            _recentCalls[e.CallId] = (e.EntityKey, e.EntitySequence);
            return;
        }
        if (e.Kind != InspectorEventKinds.ClientResponded || e.EntityKey == null) return;
        e.SubOrder = ReplySubOrder;
        if (e.CallId != null && _recentCalls.TryGetValue(e.CallId, out var call) && call.EntityKey == e.EntityKey)
            e.EntitySequence = call.Sequence;
        else if (_seen.TryGetLastSequence(e.EntityKey, out var last))
            e.EntitySequence = last;
    }

    internal const int ReplySubOrder = int.MaxValue;

    private void CompleteFlush(RawEvent marker)
    {
        lock (_flushes)
        {
            for (int i = 0; i < _flushes.Count; i++)
            {
                if (!ReferenceEquals(_flushes[i].Marker, marker)) continue;
                _flushes[i].Done.TrySetResult();
                _flushes.RemoveAt(i);
                return;
            }
        }
    }

    private MetaInspectorEvent Encode(RawEvent raw)
    {
        var e = new MetaInspectorEvent
        {
            Kind = raw.Kind,
            CallId = raw.CallId,
            SpanId = raw.SpanId != 0 ? raw.SpanId.ToString("x16") : null,
            ParentSpanId = raw.ParentSpanId != 0 ? raw.ParentSpanId.ToString("x16") : null,
            StateType = raw.StateType,
            EntityId = raw.EntityId,
            EntityKey = raw.StateType != null && raw.EntityId != null ? MetaInspectorEvent.MakeEntityKey(raw.StateType, raw.EntityId) : null,
            EntitySequence = raw.Sequence,
            SubOrder = raw.SubOrder,
            StartedAtUtc = raw.StartedAtUtc,
            DurationMs = Math.Round(raw.DurationMs, 3),
            Silo = Silo,
            MethodId = raw.MethodId,
            Service = raw.Service,
            Method = raw.Method,
            CallKind = raw.CallKind,
            CallerId = raw.CallerId,
            CallerEntity = raw.CallerEntity,
            TargetEntity = raw.TargetEntity,
            TargetPlayer = raw.TargetPlayer,
            Result = raw.Result,
            ErrorKind = raw.ErrorKind,
            Error = raw.Error,
            FromVersion = raw.FromVersion,
            ToVersion = raw.ToVersion,
            SessionId = raw.SessionId,
            ClientVersion = raw.ClientVersion,
            Reason = raw.Kind == InspectorEventKinds.BroadcastSent ? null : raw.Reason,
        };

        MetaMethodDescriptor? descriptor = null;
        if (raw.MethodId is ushort id)
        {
            descriptor = Descriptors?.Get(id);
            var entry = Signature?.GetMethodEntry(id);
            e.Service ??= entry?.ServiceName ?? descriptor?.ServiceName;
            e.Method ??= entry?.Alias ?? descriptor?.Alias;
        }

        if (raw.Payload != null) e.Args = DecodeArgs(raw.MethodId, descriptor, raw.Payload);
        else if (raw.PayloadSkipped > 0) e.Args = Skipped(raw.PayloadSkipped);

        if (raw.ResultBytes != null) e.ResultValue = DecodeResult(raw.MethodId, descriptor, raw.ResultBytes);
        else if (raw.ResultSkipped > 0) e.ResultValue = Skipped(raw.ResultSkipped);

        if (raw.StateBytes != null) e.State = DecodeState(raw.StateType, raw.StateBytes);
        else if (raw.StateSkipped > 0) e.State = Skipped(raw.StateSkipped);

        return e;
    }

    private static JsonNode Skipped(int bytes) => new JsonObject { ["_skipped_bytes"] = bytes };

    private JsonNode DecodeArgs(ushort? methodId, MetaMethodDescriptor? descriptor, byte[] payload)
    {
        if (descriptor == null || Descriptors == null || Serializer == null || methodId == null)
            return Json.Raw(payload, "no method descriptor");
        try
        {
            var values = Descriptors.DecodeArgs(methodId.Value, payload, Serializer);
            var obj = new JsonObject();
            for (int i = 0; i < values.Length; i++)
            {
                var p = i < descriptor.Parameters.Count ? descriptor.Parameters[i] : null;
                var name = p?.Name ?? "arg" + i;
                obj[name] = Json.IsRedacted(name) ? "***" : Json.ToNode(values[i], p?.WireType);
            }
            return obj;
        }
        catch (Exception ex)
        {
            return Json.Raw(payload, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private JsonNode? DecodeResult(ushort? methodId, MetaMethodDescriptor? descriptor, byte[] bytes)
    {
        if (descriptor == null || Descriptors == null || Serializer == null || methodId == null)
            return Json.Raw(bytes, "no method descriptor");
        try
        {
            return Json.ToNode(Descriptors.DecodeResult(methodId.Value, bytes, Serializer), descriptor.ResultType);
        }
        catch (Exception ex)
        {
            return Json.Raw(bytes, ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal JsonNode? DecodeState(string? stateTypeName, byte[] bytes)
    {
        var type = stateTypeName != null ? Descriptors?.GetStateType(stateTypeName) : null;
        if (type == null || Serializer == null) return Json.Raw(bytes, "unknown state type");
        try
        {
            return Json.ToNode(Serializer.Unpack(type, bytes), type);
        }
        catch (Exception ex)
        {
            return Json.Raw(bytes, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Full state type name for a short or full one; the input when unknown.</summary>
    public string? ResolveStateTypeName(string? name)
    {
        if (name == null) return null;
        return Descriptors?.GetStateType(name)?.FullName
               ?? (_stateTypesByShortName.TryGetValue(name, out var full) ? full : name);
    }

    // Estimated from the wire bytes: a decoded JSON tree takes several times its binary form.
    // Serializing every snapshot just to measure it would double the encoder's work at Full.
    private const int JsonTreeBytesPerWireByte = 8;

    private static int Size(RawEvent raw)
    {
        long wire = (raw.Payload?.Length ?? 0) + (raw.ResultBytes?.Length ?? 0) + (raw.StateBytes?.Length ?? 0);
        return (int)Math.Min(int.MaxValue, 400 + wire * JsonTreeBytesPerWireByte);
    }

    // ── seen entities ──────────────────────────────────────────────────────────

    public IReadOnlyList<SeenEntities.Entry> SeenEntitiesSnapshot() => _seen.Snapshot();

    public bool WasSeen(string entityKey) => _seen.Contains(entityKey);

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _queue.Writer.TryComplete();
        _stop.CancelAfter(TimeSpan.FromSeconds(2));
        try { await _encoder.ConfigureAwait(false); } catch { /* shutting down */ }
        IDisposable[] disposables;
        lock (_sinks) disposables = _sinks.OfType<IDisposable>().ToArray();
        foreach (var sink in disposables) sink.Dispose();
        _stop.Dispose();
    }
}

/// <summary>Bounded LRU of entities that produced events, independent of event eviction.</summary>
public sealed class SeenEntities
{
    public sealed record Entry(string EntityKey, string StateType, string EntityId, long LastSequence, DateTime LastSeenUtc);

    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new();

    public SeenEntities(int capacity) => _capacity = Math.Max(1, capacity);

    internal void Touch(string key, string stateType, string entityId, long sequence, DateTime at)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                var seq = Math.Max(node.Value.LastSequence, sequence);
                node.Value = new Entry(key, stateType, entityId, seq, at);
                _order.Remove(node);
                _order.AddFirst(node);
                return;
            }
            _map[key] = _order.AddFirst(new Entry(key, stateType, entityId, sequence, at));
            while (_map.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.EntityKey);
            }
        }
    }

    public bool Contains(string key)
    {
        lock (_lock) return _map.ContainsKey(key);
    }

    public bool TryGetLastSequence(string key, out long sequence)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                sequence = node.Value.LastSequence;
                return true;
            }
            sequence = 0;
            return false;
        }
    }

    public IReadOnlyList<Entry> Snapshot()
    {
        lock (_lock) return _order.ToList();
    }
}
