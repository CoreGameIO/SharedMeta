using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

// Allocation analyzer with per-callstack rollup for .nettrace files captured via:
//   dotnet-trace collect --process-id <PID> --providers Microsoft-Windows-DotNETRuntime:0x1:5
//
// EventPipe captures call stacks for GCAllocationTick by default (no extra flag needed in
// dotnet-trace ≥ 6 SDK). The CLR fires a tick every ~100 KB of objects of the same TYPE
// allocated since the last tick. Bytes are sampling-corrected; counts are sample counts.
//
// Two output modes:
//   • default — top-N allocators by managed type
//   • --stacks [--type FullName] [--frames N] [--top N]
//       Aggregates by (managed-type, leaf-frame) or just by stack-leaf globally.
//       --type narrows to one type, --frames controls how deep the rollup key is (default 6),
//       --top sets the number of unique stacks printed per type (default 8).
//
// Usage:
//   dotnet run --project tools/AllocationAnalyzer -- trace.nettrace                       # type-only
//   dotnet run --project tools/AllocationAnalyzer -- trace.nettrace --stacks               # per-stack global
//   dotnet run --project tools/AllocationAnalyzer -- trace.nettrace --stacks --type System.String

if (args.Length < 1)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  AllocationAnalyzer <trace.nettrace> [topN=30]");
    Console.WriteLine("  AllocationAnalyzer <trace.nettrace> --stacks [--type <FullName>] [--frames N=6] [--top N=8]");
    return 1;
}

var tracePath = args[0];
if (!File.Exists(tracePath)) { Console.Error.WriteLine($"File not found: {tracePath}"); return 2; }

bool stacksMode = args.Any(a => a == "--stacks");
string? typeFilter = ArgValue(args, "--type");
int frameDepth = int.TryParse(ArgValue(args, "--frames"), out var fd) ? fd : 6;
int perTypeTop = int.TryParse(ArgValue(args, "--top"), out var tt) ? tt : 8;
int topN = (!stacksMode && args.Length > 1 && int.TryParse(args[1], out var n)) ? n : 30;

Console.WriteLine($"Reading {tracePath}…");
var etlxPath = TraceLog.CreateFromEventPipeDataFile(tracePath);
using var traceLog = TraceLog.OpenOrConvert(etlxPath);
double dur = traceLog.SessionDuration.TotalSeconds;

if (!stacksMode)
{
    RunTypeMode(traceLog, dur, topN);
}
else
{
    RunStackMode(traceLog, dur, typeFilter, frameDepth, perTypeTop);
}
return 0;

static void RunTypeMode(TraceLog log, double dur, int topN)
{
    var byType = new Dictionary<string, (long SampleCount, long Bytes)>(StringComparer.Ordinal);
    long totalBytes = 0, totalSamples = 0;
    foreach (var evt in log.Events)
    {
        if (evt is GCAllocationTickTraceData t)
        {
            var key = t.TypeName ?? "<unknown>";
            var cur = byType.GetValueOrDefault(key);
            byType[key] = (cur.SampleCount + 1, cur.Bytes + t.AllocationAmount64);
            totalBytes += t.AllocationAmount64;
            totalSamples++;
        }
    }
    if (byType.Count == 0) { Console.WriteLine("No GCAllocationTick events."); return; }
    Console.WriteLine();
    Console.WriteLine($"Trace duration: {dur:F1}s  |  Ticks: {totalSamples:N0}  |  Bytes: {totalBytes:N0}  ({totalBytes/dur/1024/1024:F1} MB/s)");
    Console.WriteLine();
    Console.WriteLine($"{"Bytes",16}  {"Share",6}  {"Ticks",8}  {"MB/s",8}  Type");
    Console.WriteLine(new string('-', 100));
    double cum = 0;
    foreach (var r in byType
        .Select(kv => new { Type = kv.Key, Bytes = kv.Value.Bytes, Samples = kv.Value.SampleCount, Share = (double)kv.Value.Bytes / totalBytes })
        .OrderByDescending(x => x.Bytes).Take(topN))
    {
        cum += r.Share;
        Console.WriteLine($"{r.Bytes,16:N0}  {r.Share,5:P1}  {r.Samples,8:N0}  {r.Bytes/dur/1024/1024,7:F1}M  {r.Type}");
    }
    Console.WriteLine(new string('-', 100));
    Console.WriteLine($"Top-{topN} cumulative share: {cum:P1}");
}

static void RunStackMode(TraceLog log, double dur, string? typeFilter, int frameDepth, int perTypeTop)
{
    // (TypeName, StackKey) -> (samples, bytes); StackKey is "frame0\nframe1\n..."
    var byTypeStack = new Dictionary<(string Type, string Stack), (long Samples, long Bytes)>();
    long total = 0;
    int withStack = 0, withoutStack = 0;
    var stackBuf = new List<string>(frameDepth);

    foreach (var evt in log.Events)
    {
        if (evt is not GCAllocationTickTraceData tick) continue;
        var type = tick.TypeName ?? "<unknown>";
        if (typeFilter != null && type != typeFilter) continue;

        stackBuf.Clear();
        var cs = evt.CallStack();
        if (cs == null) { withoutStack++; }
        else
        {
            withStack++;
            var node = cs;
            int depth = 0;
            while (node != null && depth < frameDepth)
            {
                var mthd = node.CodeAddress.Method;
                string frame;
                if (mthd != null)
                {
                    var modName = mthd.MethodModuleFile?.Name ?? "?";
                    frame = $"{modName}!{mthd.FullMethodName}";
                }
                else
                {
                    var modName = node.CodeAddress.ModuleName ?? "?";
                    frame = $"{modName}!0x{node.CodeAddress.Address:x}";
                }
                stackBuf.Add(frame);
                node = node.Caller;
                depth++;
            }
        }
        var stackKey = stackBuf.Count == 0 ? "<no-stack>" : string.Join("\n", stackBuf);
        var key = (type, stackKey);
        var cur = byTypeStack.GetValueOrDefault(key);
        byTypeStack[key] = (cur.Samples + 1, cur.Bytes + tick.AllocationAmount64);
        total += tick.AllocationAmount64;
    }

    Console.WriteLine();
    Console.WriteLine($"Trace duration: {dur:F1}s  |  Bytes: {total:N0}  ({total/dur/1024/1024:F1} MB/s)");
    Console.WriteLine($"Ticks with stacks: {withStack:N0}  |  without: {withoutStack:N0}");
    if (withStack == 0) { Console.WriteLine("No stacks captured — recapture with dotnet-trace (stacks are on by default in recent SDKs)."); return; }
    Console.WriteLine();

    if (typeFilter != null)
    {
        PrintTypeStacks(byTypeStack, typeFilter, dur, perTypeTop, total);
        return;
    }

    // Global view: top types, and for each — top stacks.
    var typeTotals = byTypeStack
        .GroupBy(kv => kv.Key.Type)
        .Select(g => (Type: g.Key, Bytes: g.Sum(x => x.Value.Bytes), Samples: g.Sum(x => x.Value.Samples)))
        .OrderByDescending(x => x.Bytes)
        .Take(12)
        .ToList();

    foreach (var t in typeTotals)
    {
        Console.WriteLine(new string('=', 110));
        Console.WriteLine($"{t.Type}   total: {t.Bytes/dur/1024/1024:F1} MB/s  ({(double)t.Bytes/total:P1} of trace)");
        Console.WriteLine(new string('=', 110));
        PrintTypeStacks(byTypeStack, t.Type, dur, perTypeTop, total);
        Console.WriteLine();
    }
}

static void PrintTypeStacks(
    Dictionary<(string Type, string Stack), (long Samples, long Bytes)> byTypeStack,
    string type, double dur, int top, long traceTotal)
{
    var stacks = byTypeStack
        .Where(kv => kv.Key.Type == type)
        .Select(kv => (Stack: kv.Key.Stack, Samples: kv.Value.Samples, Bytes: kv.Value.Bytes))
        .OrderByDescending(x => x.Bytes)
        .Take(top)
        .ToList();
    if (stacks.Count == 0) { Console.WriteLine($"No stacks for type {type}"); return; }
    long typeTotal = byTypeStack.Where(kv => kv.Key.Type == type).Sum(kv => kv.Value.Bytes);
    foreach (var s in stacks)
    {
        var sharePct = (double)s.Bytes / typeTotal;
        Console.WriteLine($"  {s.Bytes/dur/1024/1024,6:F2} MB/s  {sharePct,5:P1}  ({s.Samples,5} samples)");
        var frames = s.Stack.Split('\n');
        for (int i = 0; i < frames.Length; i++)
            Console.WriteLine($"      {(i == 0 ? "->" : "  ")} {frames[i]}");
    }
}

static string? ArgValue(string[] args, string flag)
{
    var i = Array.IndexOf(args, flag);
    return (i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : null;
}
