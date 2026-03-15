using System;
using System.Collections.Generic;
using SharedMeta.Core;
using SharedMeta.Core.Logging;

namespace SharedMeta.Client
{
    public class ClientMetaContext<TState> : MetaContext<TState>, IClientReplayContext, IReplayContext where TState : class, ISharedState, new()
    {
        private readonly TState _state;
        private readonly IMetaSerializer _serializer;
        private readonly Dictionary<Type, object> _wrapperCache = new();
        
        private IPayloadReader? _reader;

        public ClientMetaContext(TState state, IMetaSerializer serializer)
        {
            _state = state;
            _serializer = serializer;
            Log = MetaLog.Logger;
        }
        
        public IMetaSerializer Serializer => _serializer;

        public override IMetaSerializer MetaSerializer => _serializer;

        public override object StateObject => _state;

        /// <summary>
        /// True when BeginReplay has been called (broadcast replay in progress).
        /// </summary>
        public override bool IsReplaying => _reader != null;

        /// <summary>
        /// Active payload reader for current replay.
        /// </summary>
        public IPayloadReader Reader => _reader ?? throw new InvalidOperationException("No replay in progress. Call BeginReplay first.");

        /// <summary>
        /// Start replaying from the given payload bytes.
        /// </summary>
        public void BeginReplay(byte[] payloadBytes)
        {
            _reader = _serializer.CreateReader(payloadBytes);
        }

        /// <summary>
        /// End replay and dispose reader.
        /// </summary>
        public void EndReplay()
        {
            _reader?.Dispose();
            _reader = null;
        }

        public override TInterface GetEntityApi<TInterface>(string id)
        {
            throw new NotImplementedException("GetEntityApi requires generated replayer.");
        }

        public override void Observe<TInterface>(string id)
        {
            // Client observation logic (subscription)
        }
        
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
            // Client cannot resolve server services
            throw new InvalidOperationException($"Cannot resolve {typeof(TService).Name} on client. Use Replayer instead.");
        }

        public override TInterface GetExternal<TInterface>()
        {
            var interfaceType = typeof(TInterface);
            
            // Check cache
            if (_wrapperCache.TryGetValue(interfaceType, out var cached))
            {
                return (TInterface)cached;
            }
            
            // Find generated Replayer type via naming convention
            // IServerRandom -> Namespace.Client.ServerRandomReplayer
            var interfaceName = interfaceType.Name;
            var baseName = interfaceName.StartsWith("I") && interfaceName.Length > 1 && char.IsUpper(interfaceName[1])
                ? interfaceName.Substring(1)
                : interfaceName;
            
            var replayerTypeName = $"{interfaceType.Namespace}.Client.{baseName}Replayer";
            var replayerType = interfaceType.Assembly.GetType(replayerTypeName);
            
            if (replayerType == null)
            {
                throw new InvalidOperationException($"Replayer type '{replayerTypeName}' not found. Ensure [{nameof(ServerMetaServiceAttribute)}] is on the interface.");
            }
            
            // Instantiate: Replayer(IClientReplayContext context)
            var replayer = Activator.CreateInstance(replayerType, this);
            _wrapperCache[interfaceType] = replayer!;
            return (TInterface)replayer!;
        }

    }
}
