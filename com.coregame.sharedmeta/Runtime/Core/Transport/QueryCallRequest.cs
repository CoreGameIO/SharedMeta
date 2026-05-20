using System;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Request to execute a query method on an entity without subscribing.
    /// Lightweight read-only call — no broadcasts, no replay, no persistence.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class QueryCallRequest
    {
        /// <summary>Entity to query.</summary>
        [Id(0), Key(0)] public string EntityId { get; set; } = "";

        /// <summary>Serialized method arguments.</summary>
        [Id(3), Key(3)] public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// 0.24.0+ Client's global method index from <c>GameMethodIds</c>. The server
        /// translates to its own server-side index via the per-signature clientToServer map.
        /// (ServiceName, MethodName, MethodVersion) string triple was removed in 0.24.0 —
        /// version is encoded into the index, (service, method) resolved server-side.
        /// </summary>
        [Id(5), Key(5)] public ushort MethodId { get; set; }
    }
}
