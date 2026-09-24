using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SharedMeta.Core.Diagnostics
{
    /// <summary>
    /// Which services in this build can report a deep desync — that is, which ones were compiled
    /// with <c>[MetaServiceImpl(..., DeepDesync = true)]</c> and therefore carry the client-side
    /// CRC comparison.
    /// <para>
    /// Populated by generated <c>RegisterAllServices()</c>, which runs before the first connect.
    /// Capability is a build-time fact; whether it is switched on is decided per session by the
    /// server, so both halves are needed to say anything useful about a session.
    /// </para>
    /// </summary>
    public static class DeepDesyncCapabilities
    {
        // Concurrent because this is process-wide state written from whichever thread ran
        // RegisterAllServices(). Two clients bootstrapped in parallel — two backends in one app, or
        // a test suite running classes side by side — corrupted a plain Dictionary mid-insert and
        // surfaced as IndexOutOfRangeException from inside TryInsert, nowhere near the cause.
        private static readonly ConcurrentDictionary<string, bool> _byService = new ConcurrentDictionary<string, bool>();

        /// <summary>Declare one service's compile-time capability. Idempotent — re-registration
        /// from a second <c>RegisterAllServices()</c> call overwrites with the same value.</summary>
        public static void Register(string serviceName, bool capable) => _byService[serviceName] = capable;

        /// <summary>Services that will report a divergence when the analysis is on.</summary>
        public static IReadOnlyList<string> Reporting =>
            _byService.Where(kv => kv.Value).Select(kv => kv.Key).OrderBy(n => n).ToList();

        /// <summary>Services that stay silent whatever the server decides — no attribute, no
        /// generated comparison. Note this is not the same as "not patch-tracked": a service can
        /// own a <c>_PatchTracked</c> copy because its state is force-patch-able and still appear
        /// here.</summary>
        public static IReadOnlyList<string> Silent =>
            _byService.Where(kv => !kv.Value).Select(kv => kv.Key).OrderBy(n => n).ToList();

        /// <summary>
        /// One line describing what a session with the analysis switched on will actually cover.
        /// Collapsed to a single verdict when the answer is uniform, because the common cases are
        /// "all of them" and "none of them" and a full listing there is noise.
        /// </summary>
        public static string DescribeCoverage()
        {
            if (_byService.Count == 0) return "no services registered";

            var reporting = Reporting;
            if (reporting.Count == 0) return "no service in this build reports desyncs";

            var silent = Silent;
            if (silent.Count == 0) return "every service reports desyncs";

            return $"reporting: {string.Join(", ", reporting)}; silent: {string.Join(", ", silent)}";
        }
    }
}
