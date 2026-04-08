using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Stage of a server-side RPC stall, propagated to the client through
    /// <see cref="SessionResponse.StallNotification"/>.
    /// </summary>
    public enum StallStage
    {
        /// <summary>Default for serializers — should never appear on the wire.</summary>
        None = 0,

        /// <summary>
        /// Server has detected an out-of-order gap that hasn't been filled within the soft
        /// timeout. Client should show a low-key "syncing…" indicator.
        /// </summary>
        Stalled = 1,

        /// <summary>
        /// Hard timeout reached — gap is still open. Client should prompt the user
        /// (or auto-decide) to keep waiting or reconnect. The server is still holding the
        /// stashed requests and will recover automatically if the missing request arrives.
        /// </summary>
        TimeoutPending = 2,

        /// <summary>
        /// The previously reported stall has cleared — the missing request arrived (or was
        /// abandoned cleanly) and the server has resumed processing. Client should hide any
        /// stall UI it had shown.
        /// </summary>
        Recovered = 3,
    }

    /// <summary>
    /// Information about an in-flight RPC ordering stall on the server, delivered as a field
    /// of <see cref="SessionResponse"/>. Sent only when stall state changes; clients see at
    /// most one transition per state per stall episode.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class StallNotification
    {
        /// <summary>Current stall stage.</summary>
        [Id(0), Key(0)] public StallStage Stage { get; set; }

        /// <summary>Lowest <c>RequestId</c> the server is still waiting for.</summary>
        [Id(1), Key(1)] public long OldestMissingRequestId { get; set; }

        /// <summary>Number of out-of-order requests currently parked in the server stash.</summary>
        [Id(2), Key(2)] public int StashedCount { get; set; }

        /// <summary>How long the stall has been open, in milliseconds.</summary>
        [Id(3), Key(3)] public long ElapsedMilliseconds { get; set; }
    }
}
