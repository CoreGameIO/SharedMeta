using System;
using System.Threading.Tasks;

namespace SharedMeta.Core
{
    /// <summary>
    /// Zero-allocation no-op <see cref="IPayloadWriter"/>. Writes are silently dropped;
    /// <see cref="Complete"/> returns <see cref="Array.Empty{T}"/> (CLR-cached).
    /// Use when callers need to satisfy the writer contract but no output is consumed —
    /// e.g. <see cref="NullServerRecordContext"/> for signal-mode bridge calls.
    /// </summary>
    public sealed class NullPayloadWriter : IPayloadWriter
    {
        public static readonly NullPayloadWriter Instance = new();

        private NullPayloadWriter() { }

        public void Write<T>(T value) { }

        public ReadOnlyMemory<byte> Complete() => ReadOnlyMemory<byte>.Empty;

        public void Reset() { }

        public void Dispose() { }
    }

    /// <summary>
    /// Singleton <see cref="IServerRecordContext"/> used inside signal-mode method bodies.
    /// Signal calls do not produce a replay payload (the client never executes the signal body),
    /// so any <c>[ServerMetaService]</c> bridge called from a signal is wrapped by its normal
    /// <c>{Service}Recorder</c>, but the Recorder writes into this null-sink context —
    /// real side-effects on the server still happen, recording is a no-op.
    /// <para>
    /// Zero allocations per invocation: all members return constants or do nothing.
    /// <see cref="CallEntityAsync"/> throws — cross-entity calls from signal methods are
    /// not supported (would require a full dispatch path through SessionManager).
    /// </para>
    /// </summary>
    public sealed class NullServerRecordContext : IServerRecordContext
    {
        public static readonly NullServerRecordContext Instance = new();

        private NullServerRecordContext() { }

        public IMetaSerializer Serializer => throw new InvalidOperationException(
            "NullServerRecordContext.Serializer is not available — signal-mode bridges must not require the serializer.");

        public IPayloadWriter Writer => NullPayloadWriter.Instance;

        public bool DebugEnabled => false;

        public void WriteDebugInfo(string info) { }

        public void BeginOperation() { }

        public ReadOnlyMemory<byte> EndOperation() => ReadOnlyMemory<byte>.Empty;

        public PayloadDebug? GetAndClearDebug() => null;

        public string? EntityId => null;

        public Task<byte[]> CallEntityAsync(string targetEntityId, ushort methodId, ReadOnlyMemory<byte> argsBytes)
            => throw new NotSupportedException(
                "Cross-entity calls are not supported from signal methods. Use a regular MetaMethod for cross-entity routing.");
    }
}
