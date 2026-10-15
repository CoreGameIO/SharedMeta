using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
            // The tape is positional and untagged: values the body did not read mean it took a
            // different path than on the server (a ServerRandom / cross-entity read inside a branch
            // only one side took). Named here, not as a wrong value later in unrelated code.
            if (_reader != null && _reader.HasMore)
                MetaLog.Error($"[Replay] Entity '{EntityId}': the replayed body left recorded server values unread — it diverged from the server's execution path.");
            _reader?.Dispose();
            _reader = null;
        }

        public override Task<TEntityState?> GetState<TEntityState>(string entityId) where TEntityState : class
        {
            var stateBytes = Reader.Read<byte[]?>();
            if (stateBytes == null || stateBytes.Length == 0)
                return Task.FromResult<TEntityState?>(null);
            return Task.FromResult<TEntityState?>(_serializer.Unpack<TEntityState>(stateBytes));
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
    }
}
