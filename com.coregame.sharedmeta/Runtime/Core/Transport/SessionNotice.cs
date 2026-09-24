using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// A message from the server about the session itself rather than about any entity: a stall in
    /// request ordering, a change to the player's permissions. Exactly one payload is set per notice.
    /// </summary>
    /// <remarks>
    /// Notices travel on their own channel, apart from <see cref="SessionResponse"/>. They carry no
    /// sequence number, are never stored for replay and are never acknowledged: a notice missed while
    /// disconnected is not redelivered, and whatever still matters after a reconnect is reported again
    /// by <c>SessionConnect</c>. Keeping them out of the sequenced envelope means a new kind of notice
    /// is a field here and a branch in the dispatcher, with no transport deciding whether a response
    /// that carries no operations is worth delivering.
    /// </remarks>
    // VersionTolerant: adding a notice kind must not break a peer built before it — the older side
    // skips the field it does not know and sees an empty notice, which it ignores.
    [MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer, Immutable]
    public partial class SessionNotice
    {
        /// <summary>
        /// The server's request-ordering layer is waiting on a missing request, has hit its hard
        /// timeout, or has recovered. Routed on the client to <c>ISessionHealthListener</c>.
        /// </summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public StallNotification? Stall { get; set; }

        /// <summary>
        /// The player's permission set after a change made while they are connected. Refreshes what
        /// the client's gate and UI believe; the server gates on its own copy, so a client that
        /// misses this is refused by the server rather than wrongly admitted.
        /// </summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public SharedMeta.Core.PlayerPermissions? Permissions { get; set; }
    }
}
