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

        /// <summary>Service containing the query method.</summary>
        [Id(1), Key(1)] public string ServiceName { get; set; } = "";

        /// <summary>Query method name to execute.</summary>
        [Id(2), Key(2)] public string MethodName { get; set; } = "";

        /// <summary>Serialized method arguments.</summary>
        [Id(3), Key(3)] public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Method version (0.22.0+). Mirrors <c>RpcCall.MethodVersion</c> — stamped by the
        /// generated query proxy from <c>[MetaMethod(Version = N)]</c>. The server query
        /// dispatcher routes <c>(MethodName, MethodVersion)</c> to the matching declared body;
        /// legacy callers (MethodVersion = 0) resolve to the lowest-versioned method.
        /// </summary>
        [Id(4), Key(4)] public int MethodVersion { get; set; }
    }
}
