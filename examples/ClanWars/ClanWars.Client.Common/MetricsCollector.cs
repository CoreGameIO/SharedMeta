using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ClanWars.Client.Common
{
    /// <summary>
    /// Thread-safe metrics aggregator. Records per-action sample (latency in ms) and
    /// outcome (ok/error). Renders a flat summary at end-of-run with p50/p95/p99
    /// computed from the captured samples per action name.
    /// <para>
    /// No external deps — uses ConcurrentBag for samples and Interlocked counters for the
    /// hot path. Reservoir-style sampling caps memory at <see cref="MaxSamplesPerAction"/>
    /// entries per action so a long stress run doesn't OOM.
    /// </para>
    /// </summary>
    public class MetricsCollector
    {
        private const int MaxSamplesPerAction = 4096;
        private readonly ConcurrentDictionary<string, Counter> _counters = new();
        private readonly DateTime _start = DateTime.UtcNow;

        public void Record(string action, double latencyMs, bool ok)
        {
            var c = _counters.GetOrAdd(action, _ => new Counter());
            if (ok) Interlocked.Increment(ref c.OkCount);
            else Interlocked.Increment(ref c.ErrCount);
            // Reservoir cap — drop oldest when full. Simple FIFO via Queue + lock.
            lock (c.Lock)
            {
                if (c.Samples.Count >= MaxSamplesPerAction) c.Samples.Dequeue();
                c.Samples.Enqueue(latencyMs);
            }
        }

        public void Render(System.IO.TextWriter writer)
        {
            var elapsed = DateTime.UtcNow - _start;
            writer.WriteLine($"=== Stress-test summary ({elapsed.TotalSeconds:F1}s elapsed) ===");
            writer.WriteLine($"{"Action",-30} {"OK",8} {"Err",6} {"RPS",8} {"p50",8} {"p95",8} {"p99",8}");
            foreach (var kv in _counters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var c = kv.Value;
                double[] sortedSamples;
                lock (c.Lock) sortedSamples = c.Samples.ToArray();
                Array.Sort(sortedSamples);
                var p50 = Percentile(sortedSamples, 0.50);
                var p95 = Percentile(sortedSamples, 0.95);
                var p99 = Percentile(sortedSamples, 0.99);
                var rps = elapsed.TotalSeconds > 0 ? (c.OkCount + c.ErrCount) / elapsed.TotalSeconds : 0;
                writer.WriteLine($"{kv.Key,-30} {c.OkCount,8} {c.ErrCount,6} {rps,8:F1} {p50,8:F1} {p95,8:F1} {p99,8:F1}");
            }
            writer.WriteLine();
        }

        private static double Percentile(double[] sortedAsc, double pct)
        {
            if (sortedAsc.Length == 0) return 0;
            var idx = (int)Math.Floor(pct * (sortedAsc.Length - 1));
            return sortedAsc[idx];
        }

        private sealed class Counter
        {
            public long OkCount;
            public long ErrCount;
            public readonly object Lock = new();
            public readonly Queue<double> Samples = new();
        }
    }
}
