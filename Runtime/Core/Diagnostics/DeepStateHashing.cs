using System;
using System.Runtime.CompilerServices;

namespace SharedMeta.Core.Diagnostics
{
    /// <summary>
    /// 0.26.6+ FNV-1a 32-bit hashing for deep-state-check CRC computation.
    /// Used by generated <c>*ApiClient</c> code (client side) and <c>MetaProviderBase</c>
    /// (server side) to fingerprint serialized state at the snapshot moment requested by
    /// <c>[MetaMethod(DeepStateCheck = SnapshotTiming.X)]</c>.
    /// <para>
    /// FNV-1a is chosen because it's deterministic, has no dependencies, fits in a single
    /// register on every platform we target, and the 32-bit variant has acceptable
    /// collision probability for fingerprinting purposes. It's NOT cryptographic.
    /// </para>
    /// </summary>
    public static class DeepStateHashing
    {
        private const uint Fnv1aOffsetBasis = 2166136261u;
        private const uint Fnv1aPrime = 16777619u;

        /// <summary>
        /// FNV-1a 32-bit hash. Returns the well-known empty-input value
        /// (<c>2166136261</c> / <c>0x811C9DC5</c>) when <paramref name="bytes"/> is empty —
        /// safe to compare directly against another empty-input hash.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Fnv1a32(ReadOnlySpan<byte> bytes)
        {
            uint hash = Fnv1aOffsetBasis;
            foreach (var b in bytes)
            {
                hash ^= b;
                hash *= Fnv1aPrime;
            }
            return hash;
        }

        /// <summary>
        /// 0.26.6+ Server-side deep-state-check helper — serializes <paramref name="state"/>,
        /// computes FNV-1a CRC, compares with <paramref name="clientDebug"/>'s
        /// <see cref="PayloadDebug.PreStateCrc"/>. Returns a <see cref="PayloadDebug"/> carrying
        /// the server's bytes and <see cref="SnapshotTiming.Before"/> on mismatch; null when
        /// matching OR when <paramref name="clientDebug"/> is null. This is the breakpoint-friendly
        /// surface called by generated <c>*Dispatcher.g.cs</c> for methods annotated
        /// <c>[MetaMethod(DeepStateCheck = SnapshotTiming.Before|Both)]</c>.
        /// </summary>
        public static PayloadDebug? CheckBefore<TState>(IMetaSerializer serializer, TState state, PayloadDebug? clientDebug)
            where TState : class
        {
            if (clientDebug == null) return null;
            var bytes = serializer.PackForExternalUsage(state);
            if (Fnv1a32(bytes) == clientDebug.PreStateCrc) return null;
            return new PayloadDebug { DesyncStateBytes = bytes, DesyncTiming = SnapshotTiming.Before };
        }

        /// <summary>
        /// 0.26.6+ Server-side deep-state-check helper for the <c>After</c> timing. Same
        /// contract as <see cref="CheckBefore{TState}"/>, compares against
        /// <see cref="PayloadDebug.PostStateCrc"/>. Generated dispatchers short-circuit this
        /// call when a Before-mismatch was already captured (<c>_dsDebug != null</c>) so the
        /// returned <see cref="PayloadDebug"/> is always the first failing timing.
        /// </summary>
        public static PayloadDebug? CheckAfter<TState>(IMetaSerializer serializer, TState state, PayloadDebug? clientDebug)
            where TState : class
        {
            if (clientDebug == null) return null;
            var bytes = serializer.PackForExternalUsage(state);
            if (Fnv1a32(bytes) == clientDebug.PostStateCrc) return null;
            return new PayloadDebug { DesyncStateBytes = bytes, DesyncTiming = SnapshotTiming.After };
        }
    }
}
