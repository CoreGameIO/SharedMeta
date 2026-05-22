using SharedMeta.Core;
using SharedMeta.Core.Memory;

namespace SharedMeta.Server.Core.Memory
{
    /// <summary>
    /// Server-side helper that serializes a value directly into a fresh slot in the
    /// <see cref="PooledPayloadRegistry"/> — returns a ref-counted <see cref="PooledPayload"/>
    /// suitable for OUTGOING wire fanout. Distinct from <c>IMetaSerializer.Pack&lt;T&gt;(T)</c>
    /// which writes into the per-grain scratch buffer and returns a GC-managed ROM slice.
    /// </summary>
    public static class MetaSerializerPoolExtensions
    {
        public static PooledPayload PackPooled<T>(
            this IMetaSerializer serializer,
            T value,
            PooledPayloadRegistry registry,
            int initialCapacity = 256)
        {
            var writer = registry.AcquireWriter(initialCapacity);
            try
            {
                serializer.Pack(value, writer);
                return writer.Complete();
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }
    }
}
