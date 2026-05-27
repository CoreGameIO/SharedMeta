using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core;
using SharedMeta.Core.Packets;

namespace SharedMeta.Server
{
    public class ServerMetaContext<TState> : MetaContext<TState>, IServerRecordContext where TState : class, ISharedState, new()
    {
        private readonly TState _state;
        private readonly IMetaSerializer _serializer;
        private readonly Dictionary<Type, object> _wrapperCache = new();
        
        // Per-context recorder writer. Lazily created on first BeginOperation, then Reset()
        // between operations — eliminates the per-RPC writer allocation in the prior pattern.
        private IPayloadWriter? _writer;
        private PayloadDebug? _debug;
        // Cached buffer writer for multi-parameter cross-entity arg packing in generated
        // EntityRecorder methods. Cleared on each Acquire so callers see offset 0; lives for
        // the duration of the grain method (resettable just like _scratchPool). Avoids
        // `new ArrayBufferWriter<byte>()` per cross-entity multi-param call.
        private System.Buffers.ArrayBufferWriter<byte>? _argsBufferWriter;
        // Cached IPayloadWriter for cross-entity args (separate from _writer which serves
        // the operation recorder). Returned to generated EntityCallerHelpers from
        // AcquireWriter(); Reset on each Acquire so callers always start at offset 0.
        private IPayloadWriter? _crossEntityArgsWriter;

        public System.Buffers.IBufferWriter<byte> AcquireArgsBufferWriter()
        {
            if (_argsBufferWriter == null) _argsBufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
            else _argsBufferWriter.Clear();
            return _argsBufferWriter;
        }

        public IPayloadWriter AcquireWriter()
        {
            // Generated EntityCallerHelpers calls this once per cross-entity RPC to pack args.
            // First call: lazy-create; subsequent: Reset to reuse the pool buffer. The returned
            // writer's Complete() ROM lifetime extends until the NEXT AcquireWriter call on
            // this context — long enough for the awaited grain hop to complete.
            if (_crossEntityArgsWriter == null) _crossEntityArgsWriter = _serializer.CreateWriter();
            else _crossEntityArgsWriter.Reset();
            return _crossEntityArgsWriter;
        }

        public ServerMetaContext(TState state, IMetaSerializer serializer)
        {
            _state = state;
            _serializer = serializer;
        }

        public override bool IsServer => true;
        
        public IMetaSerializer Serializer => _serializer;

        public override IMetaSerializer MetaSerializer => _serializer;

        public override object StateObject => _state!;

        /// <summary>
        /// Whether debug info should be recorded.
        /// </summary>
        public bool DebugEnabled { get; set; } = false;

        /// <summary>
        /// When true, this context is servicing a <c>[MetaMethod(Signal = true)]</c> call.
        /// <see cref="Writer"/> returns <see cref="NullPayloadWriter.Instance"/> and Recorder
        /// writes become no-ops, so signals can freely call <c>[ServerMetaService]</c> bridges
        /// without producing a replay payload. Set by <see cref="MetaProviderBase{TState}.HandleSignalAsync"/>
        /// for the duration of the dispatch and cleared in its <c>finally</c>.
        /// </summary>
        public bool SignalMode { get; set; } = false;


        /// <summary>
        /// Active payload writer for current operation. In signal mode, returns the shared
        /// <see cref="NullPayloadWriter.Instance"/> so bridge Recorders discard their output.
        /// </summary>
        public IPayloadWriter Writer => SignalMode
            ? (IPayloadWriter)NullPayloadWriter.Instance
            : _writer ?? throw new InvalidOperationException("No operation in progress. Call BeginOperation first.");

        /// <summary>
        /// Current debug info (if enabled).
        /// </summary>
        public PayloadDebug? CurrentDebug => _debug;

        /// <summary>
        /// Start recording a new operation. Reuses the cached writer (Reset between uses) —
        /// no per-RPC writer allocation on the steady-state path.
        /// </summary>
        public void BeginOperation()
        {
            if (_writer == null) _writer = _serializer.CreateWriter();
            else _writer.Reset();
            _debug = DebugEnabled ? new PayloadDebug() : null;
            _crossEntityCalls = null;
        }

        /// <summary>
        /// Complete recording and return the payload as ROM over the writer's internal pool
        /// buffer. ROM lifetime: valid until the NEXT <see cref="BeginOperation"/> on this
        /// context. Callers that need the bytes to outlive the next operation must copy
        /// (typically into the per-grain scratch buffer in <c>MetaProviderBase.HandleCallAsync</c>).
        /// </summary>
        public ReadOnlyMemory<byte> EndOperation()
        {
            if (_writer == null) throw new InvalidOperationException("No operation in progress.");
            return _writer.Complete();
            // Note: _writer is NOT cleared / disposed here — it's cached for the next
            // BeginOperation. The returned ROM is valid until that next BeginOperation Resets.
        }

        /// <summary>
        /// 0.20.0: Snapshot of the recorder fields that <see cref="BeginOperation"/> would
        /// otherwise clobber. Used by <see cref="MetaProviderBase{TState}.HandleNestedCallAsync"/>
        /// to run a nested same-grain dispatch (sibling sibling self-cross-call) inside an
        /// already-active outer operation without losing the outer's replay buffer or its
        /// in-flight cross-entity-call list.
        /// </summary>
        public readonly struct NestedOperationFrame
        {
            internal readonly IPayloadWriter? OuterWriter;
            internal readonly PayloadDebug? OuterDebug;
            internal readonly List<CrossEntityOperationInfo>? OuterCrossEntityCalls;
            internal NestedOperationFrame(IPayloadWriter? w, PayloadDebug? d, List<CrossEntityOperationInfo>? c)
            {
                OuterWriter = w; OuterDebug = d; OuterCrossEntityCalls = c;
            }
        }

        /// <summary>
        /// Begin a nested operation: saves the outer recorder state and starts a fresh
        /// operation context. The returned <see cref="NestedOperationFrame"/> must be passed
        /// back to <see cref="PopNestedOperation"/> to restore the outer state. Used by
        /// the gift-to-self short-circuit path in <c>EntityGrain.EntityCallHandler</c>.
        /// <para>
        /// Nesting is rare (sibling-on-same-entity self-call) — the inner gets a fresh
        /// transient writer; we don't pool a second writer just for this path.
        /// </para>
        /// </summary>
        public NestedOperationFrame PushNestedOperation()
        {
            var frame = new NestedOperationFrame(_writer, _debug, _crossEntityCalls);
            _writer = _serializer.CreateWriter();
            _debug = DebugEnabled ? new PayloadDebug() : null;
            _crossEntityCalls = null;
            return frame;
        }

        /// <summary>
        /// End the inner operation, return its payload bytes as a GC-managed byte[] (the
        /// inner writer's pool buffer is disposed here so the bytes survive past restoration),
        /// and restore the outer recorder state captured by the matching
        /// <see cref="PushNestedOperation"/> call.
        /// Inner cross-entity calls collected during the nested dispatch are returned via
        /// <paramref name="nestedCrossEntityCalls"/>.
        /// </summary>
        public byte[] PopNestedOperation(NestedOperationFrame frame, out List<CrossEntityOperationInfo>? nestedCrossEntityCalls)
        {
            if (_writer == null) throw new InvalidOperationException("No nested operation in progress.");
            // Materialize to GC byte[] before disposing the inner writer — its ROM would
            // be invalidated by the pool buffer return.
            var innerBytes = _writer.Complete().ToArray();
            _writer.Dispose();   // return inner writer's pool buffer
            nestedCrossEntityCalls = _crossEntityCalls;
            _writer = frame.OuterWriter;
            _debug = frame.OuterDebug;
            _crossEntityCalls = frame.OuterCrossEntityCalls;
            return innerBytes;
        }

        /// <summary>
        /// Get current debug info and reset.
        /// </summary>
        public PayloadDebug? GetAndClearDebug()
        {
            var debug = _debug;
            _debug = null;
            return debug;
        }

        public void WriteDebugInfo(string info)
        {
            _debug?.PayloadItemInfo.Add(info);
        }

        public override async Task<TEntityState?> GetState<TEntityState>(string entityId) where TEntityState : class
        {
            if (EntityStateHandler == null)
            {
                throw new InvalidOperationException(
                    $"EntityStateHandler not set. Cannot read state of entity {entityId}. " +
                    "The MetaProvider must set EntityStateHandler to enable cross-entity state reading.");
            }

            var stateBytes = await EntityStateHandler(entityId, typeof(TEntityState).Name);

            // Record for client replay (nullable byte array)
            Writer.Write(stateBytes);

            if (stateBytes == null || stateBytes.Length == 0)
                return null;

            return _serializer.Unpack<TEntityState>(stateBytes);
        }

        public override void Observe<TInterface>(string id)
        {
             // TODO
        }

        /// <summary>
        /// Resolver for real service implementations.
        /// </summary>
        public Func<Type, object>? ServiceResolver { get; set; }

        /// <summary>
        /// Handler for cross-entity method invocations.
        /// Set by the MetaProvider to enable calling other entities.
        /// Returns CrossEntityCallInfo with EntitySequenceNumber and ResultBytes.
        /// </summary>
        public Func<string, ushort, ReadOnlyMemory<byte>, long, Task<CrossEntityOperationInfo>>? EntityCallHandler { get; set; }

        /// <summary>
        /// 0.22.0+: Handler for fire-and-forget cross-entity calls. Source grain dispatches
        /// without waiting; <see cref="CallEntityOneWay"/> returns immediately.
        /// </summary>
        public Action<string, ushort, ReadOnlyMemory<byte>, long>? EntityCallOneWayHandler { get; set; }

        /// <summary>
        /// Handler for read-only cross-entity state access.
        /// Returns serialized state bytes, or null if entity doesn't exist.
        /// Set by the MetaProvider to enable cross-entity state reading.
        /// </summary>
        public Func<string, string, Task<byte[]?>>? EntityStateHandler { get; set; }

        /// <summary>
        /// Handler for mid-method state persistence.
        /// Set by EntityGrain to enable SaveStateAsync() from service methods.
        /// </summary>
        public Func<Task>? SaveStateHandler { get; set; }

        /// <summary>
        /// Force-persist the current entity state mid-method.
        /// Delegates to EntityGrain via SaveStateHandler.
        /// </summary>
        public override Task SaveStateAsync()
        {
            if (SaveStateHandler == null)
                throw new InvalidOperationException(
                    "SaveStateHandler not set. Cannot persist state mid-method. " +
                    "Ensure EntityGrain has wired SaveStateHandler.");
            return SaveStateHandler();
        }

        private List<CrossEntityOperationInfo>? _crossEntityCalls;

        /// <summary>
        /// Cross-entity calls made during current operation.
        /// Cleared on BeginOperation(), populated by CallEntityAsync().
        /// </summary>
        public List<CrossEntityOperationInfo>? CrossEntityCalls => _crossEntityCalls;

        /// <summary>
        /// Call a method on another entity asynchronously.
        /// Implementation of IServerRecordContext.CallEntityAsync.
        /// Collects CrossEntityCallInfo as a side effect.
        /// </summary>
        public async Task<byte[]> CallEntityAsync(string targetEntityId, ushort methodId, ReadOnlyMemory<byte> argsBytes)
        {
            if (EntityCallHandler == null)
            {
                throw new InvalidOperationException(
                    $"EntityCallHandler not set. Cannot call {methodId} on entity {targetEntityId}. " +
                    "The MetaProvider must set EntityCallHandler to enable cross-entity calls.");
            }

            var info = await EntityCallHandler(targetEntityId, methodId, argsBytes, ServerTimeTicks);

            _crossEntityCalls ??= new();
            _crossEntityCalls.Add(info);

            return info.ResultBytes.IsEmpty ? Array.Empty<byte>() : info.ResultBytes.ToArray();
        }

        /// <summary>
        /// 0.22.0+: Fire-and-forget variant. Returns immediately; the target grain is invoked
        /// via an Orleans <c>[OneWay]</c> entry point. No <see cref="CrossEntityOperationInfo"/> is
        /// recorded (nothing to replay client-side), and no result is observed by the caller.
        /// </summary>
        public void CallEntityOneWay(string targetEntityId, ushort methodId, ReadOnlyMemory<byte> argsBytes)
        {
            if (EntityCallOneWayHandler == null)
            {
                throw new InvalidOperationException(
                    $"EntityCallOneWayHandler not set. Cannot OneWay-call {methodId} on entity {targetEntityId}. " +
                    "The MetaProvider must set EntityCallOneWayHandler to enable fire-and-forget cross-entity calls.");
            }

            EntityCallOneWayHandler(targetEntityId, methodId, argsBytes, ServerTimeTicks);
        }

        // EntityId is inherited from MetaContext base class
        // The setter sets the base class property, which is used by generated code
        
        // ============================================
        // Service Caching Helpers (used by generated code)
        // ============================================
        
        public override bool TryGetCached(Type key, out object? value)
        {
            return _wrapperCache.TryGetValue(key, out value);
        }
        
        public override void CacheService(Type key, object wrapper)
        {
            _wrapperCache[key] = wrapper;
        }
        
        public override TService ResolveService<TService>()
        {
            if (ServiceResolver == null)
                throw new InvalidOperationException($"ServiceResolver not set. Cannot resolve {typeof(TService).Name}");
            
            var impl = ServiceResolver(typeof(TService));
            if (impl == null)
                throw new InvalidOperationException($"ServiceResolver returned null for {typeof(TService).Name}");
                
            return (TService)impl;
        }

        public override TInterface GetExternal<TInterface>()
        {
            var interfaceType = typeof(TInterface);
            
            // Check cache
            if (_wrapperCache.TryGetValue(interfaceType, out var cached))
            {
                return (TInterface)cached;
            }
            
            // Resolve real implementation
            if (ServiceResolver == null)
            {
                throw new InvalidOperationException($"ServiceResolver not set. Cannot resolve {interfaceType.Name}");
            }
            
            var realImpl = ServiceResolver(interfaceType);
            if (realImpl == null)
            {
                throw new InvalidOperationException($"ServiceResolver returned null for {interfaceType.Name}");
            }
            
            // Find generated Recorder type via naming convention
            // IServerRandom -> Namespace.Server.ServerRandomRecorder
            var interfaceName = interfaceType.Name;
            var baseName = interfaceName.StartsWith('I') && interfaceName.Length > 1 && char.IsUpper(interfaceName[1])
                ? interfaceName.Substring(1)
                : interfaceName;
            
            var recorderTypeName = $"{interfaceType.Namespace}.Server.{baseName}Recorder";
            var recorderType = interfaceType.Assembly.GetType(recorderTypeName);
            
            if (recorderType == null)
            {
                throw new InvalidOperationException($"Recorder type '{recorderTypeName}' not found. Ensure [{nameof(ServerMetaServiceAttribute)}] is on the interface.");
            }
            
            // Instantiate: Recorder(TInterface real, IServerRecordContext context)
            var recorder = Activator.CreateInstance(recorderType, realImpl, this);
            _wrapperCache[interfaceType] = recorder!;
            return (TInterface)recorder!;
        }
    }
}
