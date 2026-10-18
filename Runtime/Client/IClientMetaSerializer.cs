using SharedMeta.Core;

namespace SharedMeta.Client
{
    /// <summary>
    /// Client-only marker extension of <see cref="IMetaSerializer"/>. Symmetric counterpart
    /// to <c>SharedMeta.Server.Core.Memory.IServerMetaSerializer</c>: client and server
    /// serializer implementations are physically separated even though their codec contract
    /// (<see cref="IMetaSerializer"/>) overlaps. Reserved for future client-only methods —
    /// pooled reader caches, replay-tape decode helpers, etc.
    /// <para>
    /// No members are added yet; the interface exists so client code that needs to type
    /// against "the client serializer" can do so explicitly, and so reviewers of new
    /// PRs can immediately see whether a serializer concern is client-side, server-side,
    /// or genuinely shared.
    /// </para>
    /// </summary>
    public interface IClientMetaSerializer : IMetaSerializer
    {
    }
}
