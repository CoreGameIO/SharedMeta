using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace SharedMeta.Server
{
    public class ServerMetaContext<TState> : MetaContext<TState>, IServerRecordContext where TState : class, ISharedState, new()
    {
        private readonly TState _state;
        private readonly IMetaSerializer _serializer;
        private readonly Dictionary<Type, object> _wrapperCache = new();
        
        private IPayloadWriter? _writer;
        private PayloadDebug? _debug;

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
        /// Active payload writer for current operation.
        /// </summary>
        public IPayloadWriter Writer => _writer ?? throw new InvalidOperationException("No operation in progress. Call BeginOperation first.");

        /// <summary>
        /// Current debug info (if enabled).
        /// </summary>
        public PayloadDebug? CurrentDebug => _debug;

        /// <summary>
        /// Start recording a new operation.
        /// </summary>
        public void BeginOperation()
        {
            _writer = _serializer.CreateWriter();
            _debug = DebugEnabled ? new PayloadDebug() : null;
            _crossEntityCalls = null;
        }

        /// <summary>
        /// Complete recording and return the payload bytes.
        /// </summary>
        public byte[] EndOperation()
        {
            if (_writer == null) throw new InvalidOperationException("No operation in progress.");
            var payloadBytes = _writer.Complete();
            _writer = null;
            return payloadBytes;
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

        public override TInterface GetEntityApi<TInterface>(string id)
        {
            throw new NotImplementedException("GetEntityApi requires generated recorder.");
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
        public Func<string, string, string, byte[], long, Task<CrossEntityCallInfo>>? EntityCallHandler { get; set; }

        private List<CrossEntityCallInfo>? _crossEntityCalls;

        /// <summary>
        /// Cross-entity calls made during current operation.
        /// Cleared on BeginOperation(), populated by CallEntityAsync().
        /// </summary>
        public List<CrossEntityCallInfo>? CrossEntityCalls => _crossEntityCalls;

        /// <summary>
        /// Call a method on another entity asynchronously.
        /// Implementation of IServerRecordContext.CallEntityAsync.
        /// Collects CrossEntityCallInfo as a side effect.
        /// </summary>
        public async Task<byte[]> CallEntityAsync(string targetEntityId, string serviceName, string methodName, byte[] argsBytes)
        {
            if (EntityCallHandler == null)
            {
                throw new InvalidOperationException(
                    $"EntityCallHandler not set. Cannot call {serviceName}.{methodName} on entity {targetEntityId}. " +
                    "The MetaProvider must set EntityCallHandler to enable cross-entity calls.");
            }

            var info = await EntityCallHandler(targetEntityId, serviceName, methodName, argsBytes, ServerTimeTicks);

            _crossEntityCalls ??= new();
            _crossEntityCalls.Add(info);

            return info.ResultBytes ?? Array.Empty<byte>();
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
