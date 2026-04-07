using System.Text.Json.Serialization;
using SharedMeta.Core;
using SharedMeta.Core.Packets;
using SharedMeta.Core.Transport;

namespace SharedMeta.Transport.HttpPolling
{
    /// <summary>
    /// Source-generated JSON serialization context for all transport DTOs.
    /// Using source generation for AOT compatibility (Unity WebGL).
    /// </summary>
    [JsonSerializable(typeof(SessionConnectRequest))]
    [JsonSerializable(typeof(SessionConnectResponse))]
    [JsonSerializable(typeof(SubscribeRequest))]
    [JsonSerializable(typeof(SubscribeResponse))]
    [JsonSerializable(typeof(UnsubscribeRequest))]
    [JsonSerializable(typeof(UnsubscribeResponse))]
    [JsonSerializable(typeof(RpcCallRequest))]
    [JsonSerializable(typeof(QueryCallRequest))]
    [JsonSerializable(typeof(QueryCallResponse))]
    [JsonSerializable(typeof(DebugOptionsRequest))]
    [JsonSerializable(typeof(DebugOptionsResponse))]
    [JsonSerializable(typeof(SessionResponse))]
    [JsonSerializable(typeof(AcknowledgeRequest))]
    [JsonSerializable(typeof(AcknowledgeResponse))]
    [JsonSerializable(typeof(ConfigDownloadUrlRequest))]
    [JsonSerializable(typeof(ConfigDownloadUrlResponse))]
    [JsonSerializable(typeof(PollRequest))]
    [JsonSerializable(typeof(PollResponse))]
    [JsonSerializable(typeof(SessionOp))]
    [JsonSerializable(typeof(OperationResult))]
    [JsonSerializable(typeof(RpcCall))]
    [JsonSerializable(typeof(RpcResponse))]
    [JsonSerializable(typeof(CrossEntityOperationInfo))]
    [JsonSerializable(typeof(PayloadDebug))]
    [JsonSerializable(typeof(List<SessionResponse>))]
    [JsonSerializable(typeof(List<SessionOp>))]
    [JsonSerializable(typeof(List<OperationResult>))]
    [JsonSerializable(typeof(List<CrossEntityOperationInfo>))]
    [JsonSerializable(typeof(ResubscribedEntityInfo))]
    [JsonSerializable(typeof(List<ResubscribedEntityInfo>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(Dictionary<string, ulong>))]
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false)]
    internal partial class MetaJsonContext : JsonSerializerContext
    {
    }
}
