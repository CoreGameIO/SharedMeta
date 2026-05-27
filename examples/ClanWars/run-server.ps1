#requires -Version 5.1
<#
.SYNOPSIS
    Launches the ClanWars Orleans server for load testing.

.DESCRIPTION
    Builds (if needed) and runs ClanWars.Server. The server hosts:
      - SignalR MetaHub at        http://localhost:{Port}/meta
      - SignalR Mux hub at        http://localhost:{Port}/meta-mux  (for shared-socket stress)
      - HTTP polling endpoint at  http://localhost:{Port}/meta-http
      - Prometheus metrics at     http://localhost:{Port}/metrics
      - /stop and /stop/{seconds} for graceful shutdown via HTTP

    Silo port  = 11111 + (Port - 5050)
    Gateway    = 30000 + (Port - 5050)

.PARAMETER Port
    HTTP listen port. Silo / gateway ports shift in lockstep. Default 5050.

.PARAMETER Framework
    Target .NET framework to build / run. ClanWars projects target net8.0;net10.0;net11.0 —
    this picks one. Default net10.0. Required so `dotnet run` doesn't bail on multi-TFM csproj.

.PARAMETER Configuration
    Build configuration. Default Release for benchmarks (Debug skews allocation profile).

.PARAMETER NoBuild
    Skip dotnet build and run from existing bin/. Useful for repeat runs.

.EXAMPLE
    .\run-server.ps1
    # Defaults: net10.0 / Release, port 5050.

.EXAMPLE
    .\run-server.ps1 -Port 5060 -Framework net8.0 -Configuration Debug -NoBuild
    # Run a second silo on 5060, net8.0 Debug, from previously-built bits.
#>
param(
    [int]    $Port = 5050,
    [ValidateSet('net8.0', 'net10.0', 'net11.0')]
    [string] $Framework = 'net10.0',
    [string] $Configuration = 'Release',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ServerProj = Join-Path $ScriptDir 'ClanWars.Server\ClanWars.Server.csproj'

if (-not (Test-Path $ServerProj))
{
    throw "ClanWars.Server.csproj not found at $ServerProj"
}

if (-not $NoBuild)
{
    Write-Host "[run-server] Building ClanWars.Server ($Framework / $Configuration)..." -ForegroundColor Cyan
    & dotnet build $ServerProj -c $Configuration -f $Framework --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
}

Write-Host "[run-server] Starting on http://localhost:$Port ($Framework / $Configuration; silo=$($Port - 5050 + 11111), gw=$($Port - 5050 + 30000))" -ForegroundColor Green
Write-Host "[run-server] Endpoints:"
Write-Host "  /meta            SignalR MetaHub"
Write-Host "  /meta-mux        SignalR Mux hub (multi-tag shared sockets)"
Write-Host "  /meta-http       HTTP long-polling"
Write-Host "  /metrics         Prometheus scrape"
Write-Host "  /stop[/{N}]      Graceful shutdown (optional N-second grace)"
Write-Host ""

# `--` separator tells `dotnet run` to forward subsequent args to the app
& dotnet run --project $ServerProj -c $Configuration -f $Framework --no-build:$($NoBuild.IsPresent) -- $Port
