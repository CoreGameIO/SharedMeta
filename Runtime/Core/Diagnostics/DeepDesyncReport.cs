using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;
using SharedMeta.Core.Patch;

namespace SharedMeta.Core.Diagnostics
{
    /// <summary>
    /// Detailed report of a state-level desync detected by deep desync mode.
    /// Contains server and client patches with field-level diff.
    /// Persisted in DesyncReportGrain.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class DeepDesyncReport
    {
        [Id(0), Key(0)] public string PlayerId { get; set; } = "";
        [Id(1), Key(1)] public string EntityId { get; set; } = "";
        [Id(2), Key(2)] public string ServiceName { get; set; } = "";
        [Id(3), Key(3)] public string MethodName { get; set; } = "";
        [Id(4), Key(4)] public DateTime Timestamp { get; set; }
        [Id(5), Key(5)] public uint ServerPatchCrc { get; set; }
        [Id(6), Key(6)] public uint ClientPatchCrc { get; set; }
        [Id(7), Key(7)] public byte[]? ServerPatchBytes { get; set; }
        [Id(8), Key(8)] public byte[]? ClientPatchBytes { get; set; }
        [Id(9), Key(9)] public byte[]? ArgsBytes { get; set; }
        [Id(10), Key(10)] public List<PatchDiffEntry>? Diff { get; set; }
        /// <summary>Human-readable text description of diverged fields.</summary>
        [Id(11), Key(11)] public string TextDiff { get; set; } = "";
        /// <summary>Bitmask of <see cref="SharedMeta.Core.Transport.DesyncMismatchKind"/>.</summary>
        [Id(12), Key(12)] public int MismatchKind { get; set; }
        /// <summary>Server-returned result bytes (Result mismatch only).</summary>
        [Id(13), Key(13)] public byte[]? ServerResultBytes { get; set; }
        /// <summary>Locally re-executed result bytes (Result mismatch only).</summary>
        [Id(14), Key(14)] public byte[]? LocalResultBytes { get; set; }
        /// <summary>Server random scroll delta (Random mismatch only).</summary>
        [Id(15), Key(15)] public long ServerRandomDelta { get; set; }
        /// <summary>Local random scroll delta (Random mismatch only).</summary>
        [Id(16), Key(16)] public long LocalRandomDelta { get; set; }
    }

    /// <summary>
    /// Sink for deep desync reports. Implement to store reports in a database,
    /// grain, logger, or external analytics service.
    /// </summary>
    public interface IDesyncReportSink
    {
        Task StoreAsync(DeepDesyncReport report);
    }
}
