using System;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Request to fire a signal method on an entity. Wire shape: EntityId + MethodId + Payload.
    /// Semantics are fire-and-forget: the transport acknowledges receipt only — server
    /// execution completion is never reported back to the client. No RequestId, no response
    /// envelope.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class SignalCallRequest
    {
        /// <summary>Entity whose grain handles the signal.</summary>
        [Id(0), Key(0)] public string EntityId { get; set; } = "";

        /// <summary>Serialized method arguments.</summary>
        [Id(3), Key(3), MemoryPackAllowSerialize] public ReadOnlyMemory<byte> Payload { get; set; }

        /// <summary>
        /// 0.24.0+ Client's global method index from <c>GameMethodIds</c>. The server
        /// translates to its own server-side index via the per-signature clientToServer map.
        /// (ServiceName, MethodName, MethodVersion) string triple was removed in 0.24.0 —
        /// version is encoded into the index, (service, method) resolved server-side.
        /// </summary>
        [Id(5), Key(5)] public ushort MethodId { get; set; }
    }
}
