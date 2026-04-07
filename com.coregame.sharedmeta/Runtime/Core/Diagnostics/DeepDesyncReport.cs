using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core.Patch;

namespace SharedMeta.Core.Diagnostics
{
    /// <summary>
    /// Detailed report of a state-level desync detected by deep desync mode.
    /// Contains server and client patches with field-level diff.
    /// </summary>
    public class DeepDesyncReport
    {
        public string EntityId { get; set; } = "";
        public string ServiceName { get; set; } = "";
        public string MethodName { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public uint ServerPatchCrc { get; set; }
        public uint ClientPatchCrc { get; set; }
        public byte[]? ServerPatchBytes { get; set; }
        public byte[]? ClientPatchBytes { get; set; }
        public byte[]? ArgsBytes { get; set; }
        public List<PatchDiffEntry>? Diff { get; set; }
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
