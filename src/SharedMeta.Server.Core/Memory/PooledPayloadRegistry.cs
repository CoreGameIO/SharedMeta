using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using SharedMeta.Core;
using SharedMeta.Core.Memory;

namespace SharedMeta.Server.Core.Memory
{
    /// <summary>
    /// Per-silo pool of pinned byte[] buffers, each tracked by a slot with a ref-count.
    /// <para>
    /// <see cref="AcquireWriter"/> rents a buffer from <see cref="ArrayPool{T}.Shared"/> and a slot,
    /// returning an <see cref="PooledPayloadBufferWriter"/> that writes into the slot's buffer.
    /// On <see cref="PooledPayloadBufferWriter.Complete"/> the slot's <c>Length</c> is finalized
    /// and a <see cref="PooledPayload"/> (ref-count=1, owned by the caller) is returned.
    /// </para>
    /// <para>
    /// Owners that need to fan out to N consumers call <see cref="IncrementRef"/> with
    /// <c>delta=N</c> before handing out copies of the payload, then each consumer (or the
    /// creator, after the relevant await completes and Orleans has deep-copied the payload)
    /// calls <see cref="Release"/>. When the ref-count reaches zero the buffer is returned to
    /// <see cref="ArrayPool{T}.Shared"/> and the slot is recycled.
    /// </para>
    /// <para>
    /// Cross-silo decrement (releasing a ref from a silo other than the one that allocated
    /// the buffer) is intentionally out of scope in this iteration; <see cref="Release"/> on
    /// a ref whose silo id does not match <see cref="SiloId"/> throws.
    /// </para>
    /// </summary>
    public sealed class PooledPayloadRegistry
    {
        // Per-instance options snapshot taken at construction. UsePoolPath surfaces as
        // IsEnabled (gates MetaProviderBase.PackBroadcastVariant); EnableHistory surfaces
        // as the instance property below (read by RecordHistory). Static fields were
        // removed so multiple registries (e.g. tests + production in same process) don't
        // share global state.
        private readonly PooledPayloadOptions _options;

        /// <summary>True when the pool path is enabled for outgoing wire-payload serialization.
        /// Mirrors <see cref="PooledPayloadOptions.UsePoolPath"/>; checked by
        /// <c>MetaProviderBase.PackBroadcastVariant</c> on the hot path.</summary>
        public bool IsEnabled => _options.UsePoolPath;

        /// <summary>True when per-slot lifecycle stack capture is on. Mirrors
        /// <see cref="PooledPayloadOptions.EnableHistory"/>; checked by <c>RecordHistory</c>.</summary>
        public bool EnableHistory => _options.EnableHistory;

        internal enum HistoryOp : byte { Acquire, IncrementRef, Release, Free }

        internal readonly struct HistoryEntry
        {
            public readonly HistoryOp Op;
            public readonly int DeltaOrRefCountAfter;
            public readonly int ThreadId;
            public readonly DateTime TimestampUtc;
            public readonly string Stack;
            public HistoryEntry(HistoryOp op, int delta, string stack)
            {
                Op = op;
                DeltaOrRefCountAfter = delta;
                ThreadId = Environment.CurrentManagedThreadId;
                TimestampUtc = DateTime.UtcNow;
                Stack = stack;
            }
        }

        // Slots are stored in fixed-size chunks so that ref-into-slot references remain stable
        // when the registry grows. Each chunk is ChunkSize Slot[]; chunks are appended under
        // _growLock. Reading slots is lock-free once the chunk exists.
        private const int ChunkBits = 10;
        private const int ChunkSize = 1 << ChunkBits;
        private const int ChunkMask = ChunkSize - 1;
        private const int MaxChunks = PooledPayload.MaxSlotsPerSilo / ChunkSize;

        private readonly Slot[]?[] _chunks = new Slot[MaxChunks][];
        private int _highWater;          // next never-used slot index; only grows
        private int _allocatedSlotCount; // live (RefCount > 0) slots, for metrics
        private readonly ConcurrentQueue<int> _free = new();
        private readonly object _growLock = new();

        // Backing for SiloId: −1 means "not yet assigned" (registry created at DI time but
        // siloId allocation deferred to a startup task that calls the coordinator grain).
        // Production multi-silo uses this path so two silos cannot end up with the same id
        // and collide on Ref interpretation. CreatePinned bypasses for non-Orleans hosts.
        private int _siloIdRaw;

        /// <summary>
        /// The silo id encoded into every <see cref="PooledPayload.Ref"/> this registry mints.
        /// Throws if the registry was created without a pinned id and the startup task has
        /// not yet assigned one via <see cref="SetSiloId"/>.
        /// </summary>
        public byte SiloId
        {
            get
            {
                int raw = Volatile.Read(ref _siloIdRaw);
                if (raw < 0)
                    throw new InvalidOperationException(
                        "PooledPayloadRegistry.SiloId not yet assigned. Ensure PooledPayloadRegistryStartupTask " +
                        "has completed (registered via siloBuilder.AddStartupTask) before any pool operation.");
                return (byte)raw;
            }
        }

        /// <summary>True once <see cref="SetSiloId"/> (or pinned ctor) has bound a SiloId.</summary>
        public bool IsInitialized => Volatile.Read(ref _siloIdRaw) >= 0;

        /// <summary>
        /// Construct a registry with the SiloId left unassigned. Use this overload in DI for
        /// multi-silo deployments — the matching startup task (calling the coordinator grain)
        /// pins the SiloId before any grain code can run. Single-silo / non-Orleans hosts
        /// should use <see cref="CreatePinned"/> instead.
        /// </summary>
        public PooledPayloadRegistry(Microsoft.Extensions.Options.IOptions<PooledPayloadOptions>? options = null)
            : this(siloId: -1, options?.Value ?? new PooledPayloadOptions())
        {
        }

        /// <summary>
        /// Non-DI overload — construct with an explicit options instance. Useful for hosts that
        /// pre-build the registry before <c>IServiceProvider</c> is available (e.g. ClanWars.Server
        /// shares the same singleton between web-host and silo containers).
        /// </summary>
        public PooledPayloadRegistry(PooledPayloadOptions options)
            : this(siloId: -1, options ?? new PooledPayloadOptions())
        {
        }

        // Master ctor — every other ctor funnels into this. siloId<0 means "unbound, set
        // later via SetSiloId from the startup task"; non-negative pins the id immediately.
        private PooledPayloadRegistry(int siloId, PooledPayloadOptions options)
        {
            if (siloId >= PooledPayload.MaxSilos)
                throw new ArgumentOutOfRangeException(nameof(siloId), siloId,
                    $"siloId must be < {PooledPayload.MaxSilos} (or negative for unbound).");
            _options = options ?? new PooledPayloadOptions();
            _siloIdRaw = siloId;
            CommonInit();
        }

        /// <summary>
        /// Construct a registry with the SiloId pinned at construction. For non-Orleans hosts
        /// (benchmarks, unit tests) and single-silo deployments that don't need the coordinator
        /// grain. Production multi-silo must use the parameterless ctor + startup task.
        /// </summary>
        public static PooledPayloadRegistry CreatePinned(byte siloId, PooledPayloadOptions? options = null)
            => new PooledPayloadRegistry(siloId, options ?? new PooledPayloadOptions());

        /// <summary>
        /// Assign the SiloId — invoked by <c>PooledPayloadRegistryStartupTask</c> after the
        /// coordinator grain has allocated a unique id for this silo. Idempotent for the same
        /// id; throws if a different id was previously bound.
        /// </summary>
        public void SetSiloId(byte siloId)
        {
            if (siloId >= PooledPayload.MaxSilos)
                throw new ArgumentOutOfRangeException(nameof(siloId), siloId,
                    $"siloId must be in [0, {PooledPayload.MaxSilos}).");
            int prev = Interlocked.CompareExchange(ref _siloIdRaw, siloId, -1);
            if (prev != -1 && prev != siloId)
                throw new InvalidOperationException(
                    $"PooledPayloadRegistry.SiloId already bound to {prev}, refusing to rebind to {siloId}.");
        }

        private void CommonInit()
        {
            // Skip slot index 0 so a default PooledPayload (Ref==0) stays an unambiguous
            // "no slot to release" sentinel even on the most common single-silo deployment
            // (siloId=0): without this skip the very first allocated slot would collide with
            // the sentinel and receivers would silently drop their Release.
            _highWater = 1;
            // Wire the OpenTelemetry observable gauge to this instance's live slot count.
            // Single-silo deployments overwrite once; multi-silo would need the static field
            // upgraded to a list / per-silo tagging — out of scope here.
            Telemetry.MetricEvents.Pool.RegisterAllocatedProvider(() => AllocatedSlotCount);
        }

        /// <summary>Number of currently-live (refCount &gt; 0) slots. Health metric.</summary>
        public int AllocatedSlotCount => Volatile.Read(ref _allocatedSlotCount);

        /// <summary>
        /// Rent a buffer of at least <paramref name="initialCapacity"/> bytes and a slot,
        /// returning a writer over the slot's buffer. Ref-count starts at 1 (owned by the
        /// returned writer's <see cref="PooledPayloadBufferWriter.Complete"/> result, or
        /// released by its <see cref="PooledPayloadBufferWriter.Dispose"/> if Complete was
        /// never called).
        /// </summary>
        public PooledPayloadBufferWriter AcquireWriter(int initialCapacity = 256)
        {
            if (initialCapacity <= 0) initialCapacity = 256;

            int slotIndex = AcquireSlotIndex();
            ref var slot = ref SlotRef(slotIndex);
            slot.Buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
            slot.Length = 0;
            slot.RefCount = 1;
            slot.AcquiredAtUtcTicks = DateTime.UtcNow.Ticks;
            slot.History?.Clear();
            Interlocked.Increment(ref _allocatedSlotCount);
            Telemetry.MetricEvents.Pool.Acquired();
            RecordHistory(ref slot, HistoryOp.Acquire, 1);

            return new PooledPayloadBufferWriter(this, slotIndex);
        }

        /// <summary>
        /// Wrap an already-rented byte[] (from <see cref="ArrayPool{T}.Shared"/>) into a
        /// fresh slot with ref-count=1. The caller transfers ownership: when the slot's
        /// final <see cref="Release"/> fires, the buffer is returned to <see cref="ArrayPool{T}.Shared"/>
        /// just like a normally-acquired slot. Use this to adopt an external pooled buffer
        /// without an extra copy — e.g. when a serializer's internal <c>IBufferWriter&lt;byte&gt;</c>
        /// already uses ArrayPool and we want to surface the result as a <see cref="PooledPayload"/>
        /// without re-renting + memcpy.
        /// </summary>
        public PooledPayload AcquireExisting(byte[] buffer, int length)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if ((uint)length > (uint)buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(length), length,
                    $"length must be in [0, buffer.Length={buffer.Length}].");

            int slotIndex = AcquireSlotIndex();
            ref var slot = ref SlotRef(slotIndex);
            slot.Buffer = buffer;
            slot.Length = length;
            slot.RefCount = 1;
            slot.AcquiredAtUtcTicks = DateTime.UtcNow.Ticks;
            slot.History?.Clear();
            Interlocked.Increment(ref _allocatedSlotCount);
            Telemetry.MetricEvents.Pool.Acquired();
            var refId = PooledPayload.EncodeRef(SiloId, slotIndex);
            RecordHistory(ref slot, HistoryOp.Acquire, 1);

            return new PooledPayload(buffer.AsMemory(0, length), refId);
        }

        /// <summary>Increase the slot's ref-count by <paramref name="delta"/>. No-op when delta &lt;= 0.</summary>
        public void IncrementRef(PooledPayload payload, int delta)
        {
            if (delta <= 0 || payload.SiloId != SiloId)
                return;
            ref var slot = ref SlotRef(payload.SlotIndex);
            int current = Volatile.Read(ref slot.RefCount);
            if (current <= 0)
                throw new InvalidOperationException(
                    $"IncrementRef on freed or unallocated slot (silo={payload.SiloId}, slot={payload.SlotIndex}).\n"
                    + FormatHistory(payload.Ref));
            Interlocked.Add(ref slot.RefCount, delta);
            RecordHistory(ref slot, HistoryOp.IncrementRef, delta);
        }

        /// <summary>Decrement the slot's ref-count by 1. When it reaches zero the buffer is recycled.</summary>
        public void Release(PooledPayload payload)
        {
            if (payload.SiloId != SiloId) {
                // todo: cross silo call
                return;
            }

            var refId = payload.Ref;
            ref var slot = ref SlotRefByRef(refId);
            int newCount = Interlocked.Decrement(ref slot.RefCount);
            if (newCount < 0)
            {
                // Capture the OFFENDING caller's stack alongside the prior lifecycle so the
                // throw message names both who already released the slot and who's trying to
                // release it again.
                var offender = new StackTrace(skipFrames: 1, fNeedFileInfo: true).ToString();
                throw new InvalidOperationException(
                    $"Double-release on slot (silo={PooledPayload.DecodeSiloId(refId)}, slot={PooledPayload.DecodeSlotIndex(refId)}).\n"
                    + FormatHistory(refId)
                    + "\nOffending Release caller:\n" + offender);
            }
            RecordHistory(ref slot, HistoryOp.Release, newCount);
            if (newCount == 0)
                FreeSlot(refId);
        }

        /// <summary>Get the raw underlying byte[] for a slot. Caller must hold a live reference.</summary>
        public byte[]? GetBuffer(uint refId)
        {
            EnsureLocal(refId);
            return SlotRefByRef(refId).Buffer;
        }

        /// <summary>Current length (bytes written so far) for a slot. Caller must hold a live reference.</summary>
        public int GetLength(uint refId)
        {
            EnsureLocal(refId);
            return SlotRefByRef(refId).Length;
        }

        /// <summary>Current ref-count for diagnostics/tests. Caller must hold a live reference.</summary>
        public int GetRefCount(PooledPayload payload)
        {
            if (payload.SiloId != SiloId)
                return 0;
            return Volatile.Read(ref SlotRef(payload.SlotIndex).RefCount);
        }

        // ── internals used by PooledPayloadBufferWriter ──

        internal ref Slot SlotRef(int slotIndex)
        {
            int chunk = slotIndex >> ChunkBits;
            int offset = slotIndex & ChunkMask;
            var arr = _chunks[chunk]!;
            return ref arr[offset];
        }

        // Grow the slot's buffer to fit at least `minNewSize` bytes total. `bytesToPreserve` is
        // the number of bytes from the start of the old buffer that the caller has written and
        // wants copied into the new buffer — the writer is the source of truth (slot.Length is
        // only finalized in Complete, so we cannot read it from the slot mid-write).
        internal void GrowSlotBuffer(int slotIndex, int minNewSize, int bytesToPreserve)
        {
            ref var slot = ref SlotRef(slotIndex);
            var oldBuf = slot.Buffer;
            int newSize = oldBuf is null ? minNewSize : Math.Max(oldBuf.Length * 2, minNewSize);
            var newBuf = ArrayPool<byte>.Shared.Rent(newSize);
            if (oldBuf is not null && bytesToPreserve > 0)
                Buffer.BlockCopy(oldBuf, 0, newBuf, 0, bytesToPreserve);
            slot.Buffer = newBuf;
            if (oldBuf is not null)
                ArrayPool<byte>.Shared.Return(oldBuf, clearArray: false);
        }

        internal void SetSlotLength(int slotIndex, int length)
        {
            ref var slot = ref SlotRef(slotIndex);
            slot.Length = length;
        }

        internal uint MakeRef(int slotIndex) => PooledPayload.EncodeRef(SiloId, slotIndex);

        // ── private helpers ──

        private void EnsureLocal(uint refId)
        {
            var silo = PooledPayload.DecodeSiloId(refId);
            if (silo != SiloId)
                throw new InvalidOperationException(
                    $"Ref belongs to silo {silo}, but this registry serves silo {SiloId}. " +
                    "Cross-silo decrement is not implemented in this iteration.");
        }

        private ref Slot SlotRefByRef(uint refId) => ref SlotRef(PooledPayload.DecodeSlotIndex(refId));

        private int AcquireSlotIndex()
        {
            if (_free.TryDequeue(out var reused))
                return reused;

            int next = Interlocked.Increment(ref _highWater) - 1;
            if (next >= PooledPayload.MaxSlotsPerSilo)
                throw new InvalidOperationException(
                    $"PooledPayloadRegistry exhausted ({PooledPayload.MaxSlotsPerSilo} slots).");
            EnsureChunk(next >> ChunkBits);
            return next;
        }

        private void EnsureChunk(int chunkIndex)
        {
            if (_chunks[chunkIndex] is not null) return;
            lock (_growLock)
            {
                _chunks[chunkIndex] ??= new Slot[ChunkSize];
            }
        }

        private void FreeSlot(uint refId)
        {
            ref var slot = ref SlotRefByRef(refId);
            // Record before mutating so the history snapshot reflects "what state was at free".
            RecordHistory(ref slot, HistoryOp.Free, 0);
            var buf = slot.Buffer;
            slot.Buffer = null;
            slot.Length = 0;
            slot.AcquiredAtUtcTicks = 0;
            // slot.RefCount is already 0 here.
            // History is intentionally retained until the next Acquire clears it — that way a
            // post-Free IncrementRef/Release error can dump the entire prior lifecycle.
            if (buf is not null)
                ArrayPool<byte>.Shared.Return(buf, clearArray: false);
            _free.Enqueue(PooledPayload.DecodeSlotIndex(refId));
            Interlocked.Decrement(ref _allocatedSlotCount);
            Telemetry.MetricEvents.Pool.Released();
        }

        internal struct Slot
        {
            public byte[]? Buffer;
            public int Length;
            public int RefCount;
            /// <summary>
            /// UTC tick count at the moment the slot was last Acquired. Cleared (reset to 0)
            /// in <c>FreeSlot</c>. Used by <see cref="DumpAllocatedSlots"/> to surface "this
            /// slot has been alive for N seconds" — works even when <see cref="EnableHistory"/>
            /// is off, since it's a single long and costs nothing on the hot path.
            /// </summary>
            public long AcquiredAtUtcTicks;
            /// <summary>
            /// Lifecycle audit trail, populated only when <see cref="EnableHistory"/> is on.
            /// Allocated lazily on first record so production paths don't pay even the
            /// null-check cost when the flag stays off.
            /// </summary>
            public List<HistoryEntry>? History;
        }

        /// <summary>
        /// Snapshot of one live pool slot, returned by <see cref="DumpAllocatedSlots"/>.
        /// Captured under a non-locking walk — values are point-in-time consistent per slot,
        /// not across the whole registry.
        /// </summary>
        public sealed class PooledSlotSnapshot
        {
            public byte SiloId { get; init; }
            public int SlotIndex { get; init; }
            public uint Ref { get; init; }
            public int RefCount { get; init; }
            public int Length { get; init; }
            public DateTime AcquiredAtUtc { get; init; }
            public TimeSpan Age => DateTime.UtcNow - AcquiredAtUtc;
            /// <summary>
            /// Pre-formatted lifecycle history dump when <see cref="EnableHistory"/> was on at
            /// Acquire time; null otherwise.
            /// </summary>
            public string? HistoryDump { get; init; }
        }

        /// <summary>
        /// Walk every chunk and return a snapshot of every slot with <c>RefCount &gt; 0</c>.
        /// Use to diagnose "why is AllocatedSlotCount stuck at N hours after the load test
        /// stopped?" — each entry shows ref-count, byte length, acquire-timestamp, and (if
        /// <see cref="EnableHistory"/> was on) the full Acquire/IncrementRef/Release call-stack
        /// chain. Heavy enough to be debug-only.
        /// </summary>
        public IReadOnlyList<PooledSlotSnapshot> DumpAllocatedSlots()
        {
            var result = new List<PooledSlotSnapshot>();
            int high = Volatile.Read(ref _highWater);
            // Slot 0 is the sentinel — _highWater starts at 1, so this loop naturally skips it.
            for (int i = 1; i < high; i++)
            {
                int chunkIdx = i >> ChunkBits;
                var chunk = _chunks[chunkIdx];
                if (chunk == null) continue;
                ref var slot = ref chunk[i & ChunkMask];
                int rc = Volatile.Read(ref slot.RefCount);
                if (rc <= 0) continue;
                var refId = MakeRef(i);
                result.Add(new PooledSlotSnapshot
                {
                    SiloId = SiloId,
                    SlotIndex = i,
                    Ref = refId,
                    RefCount = rc,
                    Length = slot.Length,
                    AcquiredAtUtc = slot.AcquiredAtUtcTicks > 0
                        ? new DateTime(slot.AcquiredAtUtcTicks, DateTimeKind.Utc)
                        : DateTime.MinValue,
                    HistoryDump = slot.History != null ? FormatHistory(refId) : null,
                });
            }
            return result;
        }

        private static string CaptureStack()
        {
            // Skip frames inside CaptureStack itself + the immediate caller (Record* helper).
            // fNeedFileInfo=true so the dump shows source paths when PDBs are available.
            return new StackTrace(skipFrames: 2, fNeedFileInfo: true).ToString();
        }

        private void RecordHistory(ref Slot slot, HistoryOp op, int deltaOrCount)
        {
            if (!EnableHistory)
                return;
            // History list mutation is not threadsafe; locking the slot's history list itself
            // is enough — slot-level operations already serialize at the Interlocked layer for
            // RefCount, this just protects the diagnostic side-channel.
            var history = slot.History ??= new List<HistoryEntry>(8);
            lock (history)
            {
                history.Add(new HistoryEntry(op, deltaOrCount, CaptureStack()));
            }
        }

        private string FormatHistory(uint refId)
        {
            ref var slot = ref SlotRefByRef(refId);
            var history = slot.History;
            if (history == null || history.Count == 0) return "(no history — EnableHistory was off)";
            var sb = new StringBuilder();
            sb.Append("Slot history (silo=").Append(PooledPayload.DecodeSiloId(refId))
              .Append(", slot=").Append(PooledPayload.DecodeSlotIndex(refId))
              .Append("), ").Append(history.Count).Append(" entries:\n");
            lock (history)
            {
                for (int i = 0; i < history.Count; i++)
                {
                    var e = history[i];
                    sb.Append("  #").Append(i).Append(' ')
                      .Append(e.Op).Append(" delta/count=").Append(e.DeltaOrRefCountAfter)
                      .Append(" tid=").Append(e.ThreadId)
                      .Append(" at=").Append(e.TimestampUtc.ToString("HH:mm:ss.fffZ")).Append('\n')
                      .Append(e.Stack).Append('\n');
                }
            }
            return sb.ToString();
        }
    }
}
