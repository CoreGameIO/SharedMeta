using System.Collections.Generic;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Transport;

namespace SharedMeta.Orleans.Serialization
{
    /// <summary>
    /// Orleans-serializable surrogate for RpcCall.
    /// Used for streaming broadcasts through Orleans.
    /// 0.24.0+ ServiceName/MethodName/MethodVersion were removed from <see cref="RpcCall"/>;
    /// MethodId is the only dispatch identifier on the wire.
    /// </summary>
    [GenerateSerializer]
    public struct RpcCallSurrogate
    {
        [Id(0)] public ushort MethodId;
        [Id(3)] public byte[] PayloadBytes;
        [Id(4)] public string? CallerId;
        [Id(5)] public List<string>? DebugInfo;
    }

    /// <summary>
    /// Converter between RpcCall and RpcCallSurrogate.
    /// </summary>
    [RegisterConverter]
    public sealed class RpcCallSurrogateConverter : IConverter<RpcCall, RpcCallSurrogate>
    {
        public RpcCall ConvertFromSurrogate(in RpcCallSurrogate surrogate)
        {
            return new RpcCall
            {
                MethodId = surrogate.MethodId,
                Payload = surrogate.PayloadBytes ?? [],
                CallerId = surrogate.CallerId,
                Debug = surrogate.DebugInfo != null ? new PayloadDebug { PayloadItemInfo = surrogate.DebugInfo } : null
            };
        }

        public RpcCallSurrogate ConvertToSurrogate(in RpcCall value)
        {
            return new RpcCallSurrogate
            {
                MethodId = value.MethodId,
                PayloadBytes = value.Payload ?? [],
                CallerId = value.CallerId,
                DebugInfo = value.Debug?.PayloadItemInfo
            };
        }
    }
}
