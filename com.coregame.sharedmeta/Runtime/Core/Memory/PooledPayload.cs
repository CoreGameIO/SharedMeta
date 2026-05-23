using System;
using MemoryPack;
using MessagePack;
using Orleans;
using Orleans.Concurrency;

namespace SharedMeta.Core.Memory
{
    /// <summary>
    /// A reference to a buffer rented from a per-silo pool. The struct lives in
    /// <c>SharedMeta.Core</c> so wire-DTOs and dispatch results can carry the ownership
    /// token without depending on the server-only <c>PooledPayloadRegistry</c>.
    /// <para>
    /// <see cref="Memory"/> is the actual-length slice of a pool-rented byte[] (NOT full capacity).
    /// The struct is marked <see cref="ImmutableAttribute"/> so Orleans skips defensive
    /// deep-copies on in-silo grain hops — the receiving grain works against the same buffer
    /// and releases its ref-count share through <c>PooledPayloadRegistry</c> when done.
    /// </para>
    /// <para>
    /// <see cref="Ref"/> encodes <c>(siloId, slotIndex)</c> as a single <c>uint</c>:
    /// the top <see cref="SiloIdBits"/> bits identify the source silo (up to <see cref="MaxSilos"/>);
    /// the remaining <see cref="SlotBits"/> bits identify the slot within that silo's registry
    /// (up to <see cref="MaxSlotsPerSilo"/>). <c>Ref == 0</c> is the byte[]-fallback wrapper
    /// (no registry slot — the inner Memory is a GC-managed byte[] and there is nothing to
    /// release). Senders do NOT release their outgoing payloads — the receiving side calls
    /// <c>PooledPayloadRegistry.Release(uint)</c> when done. The exception is cross-entity
    /// call results: WE are the receiver of those, so we release after extracting the bytes.
    /// </para>
    /// <para>
    /// In this iteration cross-silo decrement is not implemented — buffers are released locally
    /// in the silo that allocated them. The silo id field is reserved for the future protocol
    /// (destination silo sends fire-and-forget decrement to source silo after copying the payload).
    /// </para>
    /// </summary>
    [GenerateSerializer, Immutable, MemoryPackable, MessagePackObject]
    public partial struct PooledPayload : IEquatable<PooledPayload>
    {
        /// <summary>Bits reserved for the silo id within <see cref="Ref"/>.</summary>
        public const int SiloIdBits = 5;

        /// <summary>Bits reserved for the slot index within <see cref="Ref"/>.</summary>
        public const int SlotBits = 32 - SiloIdBits;

        /// <summary>Maximum number of silos representable in <see cref="Ref"/> (32).</summary>
        public const int MaxSilos = 1 << SiloIdBits;

        /// <summary>Maximum number of slots a single silo's registry can address (~134M).</summary>
        public const int MaxSlotsPerSilo = 1 << SlotBits;

        private const uint SlotMask = (1u << SlotBits) - 1u;

        /// <summary>Sentinel for "no payload" — empty Memory, <c>Ref == 0</c>.</summary>
        public static readonly PooledPayload Empty = default;

        /// <summary>The pool-rented memory slice. Empty for <see cref="Empty"/>.</summary>
        [Id(0), Key(0), MemoryPackOrder(0), MemoryPackAllowSerialize]
        public ReadOnlyMemory<byte> Memory { get; set; }

        /// <summary>Encoded <c>(siloId, slotIndex)</c>. Zero for the byte[]-fallback wrapper
        /// (no registry slot to release).</summary>
        [Id(1), Key(1), MemoryPackOrder(1)]
        public uint Ref { get; set; }

        [SerializationConstructor, MemoryPackConstructor]
        public PooledPayload(ReadOnlyMemory<byte> memory, uint @ref)
        {
            Memory = memory;
            Ref = @ref;
        }

        /// <summary>Silo id encoded in the top <see cref="SiloIdBits"/> bits of <see cref="Ref"/>.</summary>
        [IgnoreMember, MemoryPackIgnore]
        public byte SiloId => (byte)(Ref >> SlotBits);

        /// <summary>Slot index encoded in the low <see cref="SlotBits"/> bits of <see cref="Ref"/>.</summary>
        [IgnoreMember, MemoryPackIgnore]
        public int SlotIndex => (int)(Ref & SlotMask);

        /// <summary>Shortcut for <c>Memory.Span</c>. Method instead of property so the JSON
        /// source generator doesn't try to register a <c>ReadOnlySpan&lt;byte&gt;</c>
        /// <c>JsonTypeInfo</c> (it's a ref struct and rejects).</summary>
        public ReadOnlySpan<byte> Span() => Memory.Span;

        /// <summary>Byte length of the slice. Zero when empty.</summary>
        [IgnoreMember, MemoryPackIgnore]
        public int Length => Memory.Length;

        /// <summary><c>true</c> when there is no underlying buffer (sentinel or zero-length payload).</summary>
        [IgnoreMember, MemoryPackIgnore]
        public bool IsEmpty => Memory.IsEmpty;

        /// <summary>Pack <c>(siloId, slotIndex)</c> into a single <c>uint</c>.</summary>
        public static uint EncodeRef(byte siloId, int slotIndex)
        {
            if (siloId >= MaxSilos)
                throw new ArgumentOutOfRangeException(nameof(siloId), siloId,
                    $"siloId must fit in {SiloIdBits} bits (< {MaxSilos}).");
            if ((uint)slotIndex >= MaxSlotsPerSilo)
                throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex,
                    $"slotIndex must fit in {SlotBits} bits (< {MaxSlotsPerSilo}).");
            return ((uint)siloId << SlotBits) | (uint)slotIndex;
        }

        public static byte DecodeSiloId(uint refId) => (byte)(refId >> SlotBits);
        public static int DecodeSlotIndex(uint refId) => (int)(refId & SlotMask);

        public bool Equals(PooledPayload other) => Ref == other.Ref && Memory.Equals(other.Memory);
        public override bool Equals(object? obj) => obj is PooledPayload p && Equals(p);
        public override int GetHashCode() => HashCode.Combine(Ref, Memory);
        public static bool operator ==(PooledPayload a, PooledPayload b) => a.Equals(b);
        public static bool operator !=(PooledPayload a, PooledPayload b) => !a.Equals(b);

        public override string ToString() =>
            IsEmpty ? "PooledPayload.Empty" : $"PooledPayload(silo={SiloId}, slot={SlotIndex}, len={Length})";
    }
}
