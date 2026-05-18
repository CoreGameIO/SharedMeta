using System.Collections.Generic;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Transport;

namespace SharedMeta.Orleans.Serialization
{
    /// <summary>
    /// Orleans-serializable surrogate for RpcCall.
    /// Used for streaming broadcasts through Orleans.
    /// </summary>
    [GenerateSerializer]
    public struct RpcCallSurrogate
    {
        [Id(0)] public string ServiceName;
        [Id(1)] public string MethodName;
        [Id(2)] public int MethodVersion;
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
                ServiceName = surrogate.ServiceName ?? "",
                MethodName = surrogate.MethodName ?? "",
                MethodVersion = surrogate.MethodVersion,
                Payload = surrogate.PayloadBytes ?? [],
                CallerId = surrogate.CallerId,
                Debug = surrogate.DebugInfo != null ? new PayloadDebug { PayloadItemInfo = surrogate.DebugInfo } : null
            };
        }

        public RpcCallSurrogate ConvertToSurrogate(in RpcCall value)
        {
            return new RpcCallSurrogate
            {
                ServiceName = value.ServiceName,
                MethodName = value.MethodName,
                MethodVersion = value.MethodVersion,
                PayloadBytes = value.Payload ?? [],
                CallerId = value.CallerId,
                DebugInfo = value.Debug?.PayloadItemInfo
            };
        }
    }
}
