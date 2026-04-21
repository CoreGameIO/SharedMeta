using System;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Request to fire a signal method on an entity. Wire shape mirrors <see cref="QueryCallRequest"/>
    /// (EntityId + ServiceName + MethodName + Payload), but the semantics are fire-and-forget:
    /// the transport acknowledges receipt only — server execution completion is never reported
    /// back to the client. No RequestId, no response envelope.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class SignalCallRequest
    {
        /// <summary>Entity whose grain handles the signal.</summary>
        [Id(0), Key(0)] public string EntityId { get; set; } = "";

        /// <summary>Service containing the signal method.</summary>
        [Id(1), Key(1)] public string ServiceName { get; set; } = "";

        /// <summary>Signal method name to execute.</summary>
        [Id(2), Key(2)] public string MethodName { get; set; } = "";

        /// <summary>Serialized method arguments.</summary>
        [Id(3), Key(3)] public byte[] Payload { get; set; } = Array.Empty<byte>();
    }
}
