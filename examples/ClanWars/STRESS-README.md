# ClanWars stress / load testing

Two scripts launch the moving pieces:

| Script | What it does |
|---|---|
| `run-server.ps1` | Builds + launches the ClanWars Orleans server (port 5050 by default). |
| `run-stress.ps1` | Builds + runs the V1 or V2 stress client against a running server. |

## Quick start

```powershell
# Terminal 1
.\run-server.ps1

# Terminal 2
.\run-stress.ps1                          # 100 v1 players for 30 s
.\run-stress.ps1 -Variant v2 -Players 500 -Duration 60
```

End-of-run summary prints per-action ok/err counts, RPS, p50/p95/p99 latency.

## Target framework

Both projects multi-target `net8.0;net10.0;net11.0`. The scripts default to `net10.0` and pass it explicitly to `dotnet build -f` and `dotnet run -f` — `dotnet run` against a multi-TFM csproj would otherwise bail. Override with `-Framework`:

```powershell
.\run-server.ps1 -Framework net8.0
.\run-stress.ps1 -Framework net11.0 -Players 1000
```

Multi-process mode propagates the chosen framework to every child, so the whole pipeline runs on one TFM.

## Multiple client processes

One .NET client process saturates CPU / threadpool well before the server is loaded — beyond ~2k players in plain mode or ~10k in Mux mode, the bottleneck moves to the client. `-Processes N` spawns N parallel client processes **in the same console** (`Start-Process -NoNewWindow`), each with an auto-suffixed `Prefix` (`{base}-p0`, `{base}-p1`, …) so player ids stay disjoint:

```powershell
.\run-stress.ps1 -Processes 4 -Players 2500 -MuxChannels 25 -Duration 120
# 4 processes × 2500 players × 25 mux sockets = 10,000 simulated players, 100 sockets.

.\run-stress.ps1 -Processes 8 -Players 5000 -MuxChannels 50 -MuxBatch -Duration 180
# Aggressive: 8 processes × 5000 players = 40,000 simulated players, 400 mux sockets.
```

All children inherit the parent's stdout/stderr — output interleaves by line in the same console you ran the script from. The per-process player-id prefix (e.g. `v1-ab3f7q-p2-…`) appears in every error/log line so attribution stays clear.

### Avoiding "session superseded" collisions

If you don't pass `-Prefix`, the script generates a unique random suffix per invocation (e.g. `v1-ab3f7q`). This means **running `run-stress.ps1` multiple times in parallel terminals will NOT collide** — each invocation gets its own player-id namespace. If you do pass `-Prefix` explicitly, make sure two parallel runs don't use the same value (same prefix → identical player ids → server sees second connect as a supersede of the first session, and the original disconnects).

Parent builds once, children launch with `-NoBuild`. Parent waits for all children to exit before returning. Each child prints its stress-summary table when it finishes.

## Skipping the build

Pass `-NoBuild` to skip `dotnet build` (handy for repeat runs against the same bits). Both `run-server.ps1` and `run-stress.ps1` accept it. Multi-process mode (`-Processes N > 1`) already passes `-NoBuild` to children automatically — the parent builds once.

## Mux mode (many players, few sockets)

The plain mode opens one SignalR socket per simulated player — OS / threadpool limits cap this around 1–2k players per host. For larger loads use the **Mux transport** which routes N players over K physical sockets:

```powershell
.\run-stress.ps1 -Players 5000 -MuxChannels 50 -Duration 120
.\run-stress.ps1 -Players 10000 -MuxChannels 100 -MuxBatch -MuxBatchSize 128 -Duration 180
```

Each Mux channel pump is one SignalR connection to `/meta-mux`. Players are round-robin sharded by index. `-MuxBatch` packs multiple RPCs into one frame to amortize the SignalR per-frame overhead at a small batching delay cost.

## Mixing v1 + v2

Run both simultaneously to exercise the per-client config-branch routing (`[MetaConfigVersion]` rules) and the force-patch path (`[MetaConfigStructureBoundary("2.0")]` on `IClanService` triggers when a v1 client joins a clan pinned at 2.x):

```powershell
# Two terminals, same server:
.\run-stress.ps1 -Variant v1 -Players 200 -Prefix v1 -Duration 120
.\run-stress.ps1 -Variant v2 -Players 200 -Prefix v2 -Duration 120
```

`-Prefix` keeps the simulated player ids disjoint so the two runs don't collide on the same Orleans grain.

## Graceful shutdown

The server exposes `/stop` and `/stop/{seconds}`. Trigger via curl / browser:

```
http://localhost:5050/stop          # immediate graceful shutdown
http://localhost:5050/stop/5        # 5 s grace before shutdown
```

The endpoint flips a middleware gate first (rejects new connections), pushes `SessionTerminated` to active clients, then calls `lifetime.StopApplication()`. Orleans runs `OnDeactivateAsync` on every grain, including SessionManagerGrain which persists `CurrentSessionId / SequenceNumber / PendingPackets / LastDispatchedRequestId` so a subsequent Resume after restart works without losing in-flight RPCs.

## Profiling allocations

Pair the stress run with `dotnet-counters` for live allocation rate:

```powershell
# Terminal 3, once server is up:
dotnet-counters monitor --counters System.Runtime --process-id (Get-Process ClanWars.Server).Id
```

For per-RPC alloc accounting use the in-process micro-benchmarks at
[tests/SharedMeta.IntegrationTests/AllocationBenchmarks.cs](../../tests/SharedMeta.IntegrationTests/AllocationBenchmarks.cs) (run with the `--filter` shown there).

## Notes

- Both scripts default to `-Configuration Release` — Debug skews allocation numbers significantly because of `[Conditional("DEBUG")]` instrumentation in the framework.
- `-NoBuild` skips the build step for repeated runs against the same bits.
- For Prometheus scraping during a stress run: `http://localhost:5050/metrics`.
