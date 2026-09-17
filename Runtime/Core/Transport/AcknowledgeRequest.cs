using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Request to acknowledge received packets up to a sequence number.
    /// Allows server to clean up cached packets for reconnection.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class AcknowledgeRequest
    {
        /// <summary>
        /// Highest sequence number the client has successfully processed.
        /// Server can discard packets up to and including this sequence.
        /// </summary>
        [Id(0), Key(0)] public long SequenceNumber { get; set; }
    }
}
