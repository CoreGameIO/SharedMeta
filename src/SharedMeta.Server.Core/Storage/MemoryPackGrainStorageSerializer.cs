#if USE_MEMORYPACK
using System;
using MemoryPack;
using Orleans.Storage;

namespace SharedMeta.Server.Core.Storage
{
    /// <summary>
    /// <see cref="IGrainStorageSerializer"/> backed by <see cref="MemoryPackSerializer"/>.
    /// <para>
    /// Drop-in replacement for the Orleans default <c>JsonGrainStorageSerializer</c>. Allocation
    /// profile of the default serializer is dominated by <c>Newtonsoft.Json</c> string/Char[]/
    /// StringBuilder churn (~50% of total server allocations under stress) — switching to a
    /// binary codec cuts that to near-zero. All persisted grain state types must already be
    /// <c>[MemoryPackable]</c> with <c>[MemoryPackOrder]</c> (which is the framework requirement
    /// for transport serialization anyway, so no new attributes are needed in practice).
    /// </para>
    /// <para>
    /// Wire it into a storage provider via the provider's <c>GrainStorageSerializer</c> option:
    /// <code>
    /// siloBuilder.AddMemoryGrainStorage("Default", o =&gt;
    ///     o.GrainStorageSerializer = new MemoryPackGrainStorageSerializer());
    /// </code>
    /// </para>
    /// </summary>
    public sealed class MemoryPackGrainStorageSerializer : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T input)
        {
            var bytes = MemoryPackSerializer.Serialize(input);
            return new BinaryData(bytes);
        }

        public T Deserialize<T>(BinaryData input)
        {
            var bytes = input.ToArray();
            return MemoryPackSerializer.Deserialize<T>(bytes)!;
        }
    }
}
#endif
