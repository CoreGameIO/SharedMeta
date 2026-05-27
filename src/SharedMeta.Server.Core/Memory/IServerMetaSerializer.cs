using SharedMeta.Core;

namespace SharedMeta.Server.Core.Memory
{
    /// <summary>
    /// Server-only extension of <see cref="IMetaSerializer"/>. Carries the grain-scoped
    /// lifecycle hooks that are nonsensical on the client (which has no scratch pool, no
    /// per-call reset boundary, and serializer instances live for the application lifetime
    /// rather than the duration of a grain method).
    /// <para>
    /// The split is intentional: <see cref="IMetaSerializer"/> is the cross-tier codec
    /// contract (Pack / Unpack / CreateReader / CreateWriter / RpcCall / Clone) — same shape
    /// on both client and server. Server-only concerns — scratch buffer reuse, per-grain
    /// writer pooling, etc. — live here so client code never accidentally calls them and
    /// server code can stop fishing for the concrete <c>GrainScopedSerializer</c> via
    /// runtime casts. <c>MetaProviderBase</c> and friends should accept
    /// <see cref="IServerMetaSerializer"/> where the lifecycle methods are needed.
    /// </para>
    /// </summary>
    public interface IServerMetaSerializer : IMetaSerializer
    {
        /// <summary>
        /// Rewind the per-grain scratch pool to offset 0. Invalidates every
        /// <see cref="ReadOnlyMemory{T}"/> previously returned by <see cref="IMetaSerializer.Pack{T}(T)"/>
        /// or written via <see cref="IMetaSerializer.Pack{T}(T,System.Buffers.IBufferWriter{byte})"/>
        /// against this serializer's scratch. Call at the start of each grain method entry
        /// (HandleCallAsync / HandleQueryAsync / HandleSignalAsync / HandleExternalEventAsync /
        /// SubscribeAsync) — the previous call's intermediate slices have already been
        /// embedded into outgoing payloads or copied across grain boundaries by then.
        /// </summary>
        void ResetScratch();
    }
}
