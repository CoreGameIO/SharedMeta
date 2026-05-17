# ClanWars observability stack

Prometheus + Grafana via Docker, pre-configured to scrape the **host-running** `ClanWars.Server`
on `:5050/metrics` and render a SharedMeta-specific dashboard.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  Host machine (Windows 11 + Docker Desktop)                                 │
│                                                                              │
│  ┌────────────────────────────┐     ┌──────────────────────────────────┐   │
│  │ ClanWars.Server            │     │ docker compose                   │   │
│  │ (dotnet, native on host)   │     │ ┌──────────────────────────┐    │   │
│  │                            │     │ │ prometheus               │    │   │
│  │  http://*:5050/metrics  ◄──┼─────┼─┤ scrape :host.docker      │    │   │
│  │                            │     │ │   .internal:5050/metrics │    │   │
│  └────────────────────────────┘     │ │  :9090 → host           │    │   │
│                                      │ └──────────┬───────────────┘    │   │
│                                      │            │                     │   │
│                                      │            ▼                     │   │
│                                      │ ┌──────────────────────────┐    │   │
│  Browser → http://localhost:3000 ────┼─┤ grafana                  │    │   │
│         (admin / admin)              │ │  :3000 → host           │    │   │
│                                      │ │  auto-provisioned       │    │   │
│                                      │ │  datasource + dashboard │    │   │
│                                      │ └──────────────────────────┘    │   │
│                                      └──────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────────┘
```

- **ClanWars.Server runs natively on the host** — no Docker container, no recompile.
- Prometheus inside Docker scrapes the host via the magic `host.docker.internal` DNS name.
  - Docker Desktop on **Windows/macOS** injects this automatically.
  - On **vanilla Linux Docker**, the `extra_hosts: host-gateway` line in `docker-compose.yml`
    maps it manually — no extra setup needed there either.
- Grafana auto-provisions the Prometheus datasource and the dashboard JSON at startup.

## Quick start

```powershell
# 1) In one terminal — start the SharedMeta server natively (no Docker)
dotnet run --project examples/ClanWars/ClanWars.Server/ClanWars.Server.csproj

# 2) In another terminal — start Prometheus + Grafana
cd examples/ClanWars/observability
docker compose up -d

# 3) Drive some load (another terminal)
dotnet run --project examples/ClanWars/ClanWars.Client.V1 -- `
  --players 1000 --duration 60 --max-clans 20 --delay 100 `
  --mux-channels 10 --prefix plv1
# (optionally start V2 in parallel)

# 4) Open Grafana → Dashboards → SharedMeta → "SharedMeta / ClanWars"
start http://localhost:3000
```

Default Grafana login: `admin` / `admin` (changeable on first login). Anonymous viewer
access is also enabled — anyone on the host can hit http://localhost:3000 without login
and see the read-only dashboard.

## What the dashboard shows

| Row | Panels | What you learn |
|---|---|---|
| **Live overview** | Active sessions / grains / RPC rate / cross-entity rate | Current CCU + load shape at a glance |
| **RPC latency & throughput** | p95 by method, rate by method, request bytes p95, session connect p50/p95/p99 | Which methods are slow; which are jumbo on the wire |
| **Cross-entity & Notification** | call rate by `kind` (normal/notification stacked), p95 by kind | How much of cross-entity is fire-and-forget (`Notification` mode); whether OneWay is shaving latency |
| **Broadcast machinery** | fan-out size, payload bytes/sec by `kind` (replay/patch/state), tailored count by `path` | Bandwidth distribution; **the `patch` path proves force-patch from 0.22 boundary is actually firing** |
| **Persistence, subscribe, grain lifecycle** | WriteStateAsync duration, subscribe (cold-start) duration, activation rate | Real cost of state I/O; how much your TTL/grain-pinning is helping |
| **.NET runtime** | Allocation rate, GC pause %, working set, lock contentions/sec | When framework is GC-bound or contention-bound |

## Tuning the scrape interval

`prometheus.yml` defaults to `scrape_interval: 5s` — sufficient for stress tests up to a
few thousand req/sec. For higher rates drop to `2s` (more storage, more accurate rate
curves at the cost of higher Prometheus CPU). For long observation drop to `15s`.

## Stop / cleanup

```powershell
docker compose down              # stop containers, keep data
docker compose down -v           # also delete prom-data + grafana-data volumes
```

## Adding new metrics

Define them on the static `SharedMetaMeters` class in `src/SharedMeta.Server.Core/Telemetry/`
(or the client-side equivalent in `com.coregame.sharedmeta/Runtime/Client/Telemetry/`). 

Restart the server, then either:

- Modify `grafana/dashboards/sharedmeta-clanwars.json` (dashboard re-loads automatically every
  10s — Grafana watches `/var/lib/grafana/dashboards/`)
- Or open Grafana, edit the dashboard in the UI, then "Settings → JSON Model → Save to file"
  back to the repo

Prometheus picks up new metric names without any config change — they appear on next scrape.

## Troubleshooting

**`Prometheus says target "clanwars-server" is DOWN` (red dot in Prometheus → Status → Targets):**
- Check `ClanWars.Server` is running on the host (`netstat -an | findstr 5050`)
- Check `host.docker.internal` resolves from inside the Prometheus container:
  `docker exec sharedmeta-prom wget -qO- http://host.docker.internal:5050/metrics | head -5`
- If on vanilla Linux Docker (no Docker Desktop), check `extra_hosts` line in
  `docker-compose.yml` mapped `host.docker.internal` via `host-gateway`.

**Dashboard shows "No data":**
- Run a stress test first — metrics only appear once instruments fire.
- Time range in the top-right: set to "Last 5 minutes" or "Last 15 minutes" not days.
- Check Prometheus has data: open http://localhost:9090 → run query
  `sharedmeta_session_active` — should return at least one series.

**Wrong port (something else on :3000 or :9090):**
- Edit `docker-compose.yml` `ports:` — left side is host (e.g. change `3000:3000` to
  `13000:3000` to expose Grafana on host port 13000).
