#requires -Version 5.1
<#
.SYNOPSIS
    Runs a ClanWars stress / load test against a running server.

.DESCRIPTION
    Spawns N simulated players that hammer the IClanService surface
    (CreateClan / Apply / Promote / GetSummary / ...) for D seconds. Per-action
    p50/p95/p99 latency + ok/err counts print at end of run. Choose between:

      - Plain mode  (default, MuxChannels=0):  one SignalR socket per player.
      - Mux   mode  (-MuxChannels N):          N physical SignalR sockets, all
                                               players multiplexed via /meta-mux.
                                               Each socket carries ~Players/N tagged
                                               sessions. Use this to drive thousands
                                               of simulated players without exhausting
                                               OS socket / threadpool resources.

    Switch -Variant to pick the underlying client (V1 = ClientAppVersion 1.0.0,
    V2 = ClientAppVersion 2.0.0). V1 talks to clans pinned at config 1.x; V2 to
    clans pinned at config 2.x — server-side force-patches the IClanService surface
    for V1 sessions joining a 2.x-pinned clan (exercises the
    [MetaConfigStructureBoundary("2.0")] codepath).

.PARAMETER Variant
    'v1' or 'v2'. Default v1.

.PARAMETER Url
    Server SignalR URL. Default http://localhost:5050/meta.

.PARAMETER Players
    Number of simulated player connections. Default 100.

.PARAMETER MaxClans
    Cap on total clans cluster-wide; above the cap "create clan" simulators
    switch to "apply to existing clan". Default 20.

.PARAMETER Duration
    Run duration in seconds. Default 30.

.PARAMETER Delay
    Mean inter-action sleep per player (ms). Actual sleep is Uniform(0, 2*Delay).
    Default 500.

.PARAMETER Prefix
    Player-id prefix — distinguishes parallel runs / variants on the same server.
    Default 'v1' or 'v2' depending on -Variant.

.PARAMETER MuxChannels
    > 0 ⇒ enable Mux transport, with this many physical SignalR sockets shared by
    all simulated players. Default 0 (plain mode, one socket per player).

.PARAMETER MuxUrl
    Mux endpoint URL. Ignored unless MuxChannels > 0. Default http://localhost:5050/meta-mux.

.PARAMETER MuxBatch
    Pack multiple RpcCallRequests into one BatchRpcCall frame per Mux channel.
    Reduces SignalR per-frame overhead at the cost of small batching latency.

.PARAMETER MuxBatchSize
    Max entries per batch frame when -MuxBatch is set. Default 64.

.PARAMETER MuxBatchFlush
    Flush interval (ms) for batched RPCs when -MuxBatch is set. Default 1.

.PARAMETER Configuration
    Build configuration. Default Release.

.PARAMETER NoBuild
    Skip dotnet build, run from existing bin/.

.EXAMPLE
    .\run-stress.ps1
    # 100 plain-mode v1 players, 30 seconds, against localhost:5050.

.EXAMPLE
    .\run-stress.ps1 -Variant v2 -Players 500 -Duration 60
    # 500 v2 players for 1 minute.

.EXAMPLE
    .\run-stress.ps1 -Players 5000 -MuxChannels 50 -Duration 120
    # 5000 simulated players multiplexed over 50 real SignalR sockets, 2 minutes.

.EXAMPLE
    .\run-stress.ps1 -Players 10000 -MuxChannels 100 -MuxBatch -MuxBatchSize 128 -Duration 180
    # Stress mode: 10k players, 100 sockets, batched RPCs to minimize per-frame overhead.

.EXAMPLE
    .\run-stress.ps1 -Processes 4 -Players 2500 -MuxChannels 25 -Duration 120
    # Spawns 4 parallel client processes, each with 2500 players over 25 mux channels.
    # Total simulated load: 10,000 players, 100 sockets. Useful when one .NET process
    # caps out CPU / threadpool before saturating the server.

.PARAMETER Processes
    Number of parallel client processes to spawn. Each process gets its own auto-suffixed
    Prefix (e.g. v1-p0, v1-p1) so player ids stay disjoint. Default 1 (single process,
    backward-compatible behaviour).
#>
param(
    [ValidateSet('v1', 'v2')]
    [string] $Variant = 'v1',

    [string] $Url = 'http://localhost:5050/meta',
    [int]    $Players = 100,
    [int]    $MaxClans = 20,
    [int]    $Duration = 30,
    [int]    $Delay = 500,
    [string] $Prefix,

    [int]    $MuxChannels = 0,
    [string] $MuxUrl = 'http://localhost:5050/meta-mux',
    [switch] $MuxBatch,
    [int]    $MuxBatchSize = 64,
    [int]    $MuxBatchFlush = 1,

    [int]    $Processes = 1,

    [ValidateSet('net8.0', 'net10.0', 'net11.0')]
    [string] $Framework = 'net10.0',

    [string] $Configuration = 'Release',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$ClientProj = if ($Variant -eq 'v1')
{
    Join-Path $ScriptDir 'ClanWars.Client.V1\ClanWars.Client.V1.csproj'
}
else
{
    Join-Path $ScriptDir 'ClanWars.Client.V2\ClanWars.Client.V2.csproj'
}

if (-not (Test-Path $ClientProj))
{
    throw "Client project not found: $ClientProj"
}

if (-not $PSBoundParameters.ContainsKey('Prefix'))
{
    # Default prefix includes a 6-char random suffix so independent script invocations
    # don't collide on player ids (which caused "session superseded" everywhere when two
    # runs both used "-v1-{0..N}" against the same server grain set).
    $randomSuffix = -join ((48..57) + (97..122) | Get-Random -Count 6 | ForEach-Object { [char]$_ })
    $Prefix = "$Variant-$randomSuffix"
}

if ($Processes -lt 1) { throw "-Processes must be >= 1 (got $Processes)" }

if (-not $NoBuild)
{
    Write-Host "[run-stress] Building $Variant client ($Framework / $Configuration)..." -ForegroundColor Cyan
    & dotnet build $ClientProj -c $Configuration -f $Framework --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
}

# Multi-process orchestration. Spawns N parallel `dotnet run` invocations directly —
# no nested PowerShell wrapper, no Start-Process arg-quoting quirks (the previous PS-wrapper
# approach lost the per-process Prefix suffix in some PS hosts → "session superseded"
# everywhere from playerId collisions). Each child uses --prefix "{Prefix}-pN" so player
# ids stay disjoint. Parent built (or not) above; children all -no-build.
if ($Processes -gt 1)
{
    Write-Host "[run-stress] Spawning $Processes parallel client processes (prefix base = $Prefix)" -ForegroundColor Green
    Write-Host ""

    $childProcesses = @()
    for ($i = 0; $i -lt $Processes; $i++)
    {
        $childPrefix = "$Prefix-p$i"
        $childClientArgs = @(
            '--url', $Url,
            '--players', $Players,
            '--max-clans', $MaxClans,
            '--duration', $Duration,
            '--delay', $Delay,
            '--prefix', $childPrefix
        )
        if ($MuxChannels -gt 0)
        {
            $childClientArgs += @(
                '--mux-channels', $MuxChannels,
                '--mux-url', $MuxUrl,
                '--mux-batch', $MuxBatch.IsPresent.ToString().ToLowerInvariant(),
                '--mux-batch-size', $MuxBatchSize,
                '--mux-batch-flush', $MuxBatchFlush
            )
        }
        $dotnetArgs = @(
            'run', '--project', $ClientProj,
            '-c', $Configuration,
            '-f', $Framework,
            '--no-build',
            '--'
        ) + $childClientArgs

        Write-Host "[run-stress]   #$i prefix=$childPrefix" -ForegroundColor DarkGray
        # -NoNewWindow keeps the child's stdout/stderr attached to the parent's console.
        # All children's output interleaves into the same console; per-process Prefix in each
        # error/log line keeps attribution clear.
        $proc = Start-Process dotnet -ArgumentList $dotnetArgs -NoNewWindow -PassThru
        $childProcesses += $proc
    }

    Write-Host "[run-stress] All $Processes processes launched (PIDs: $(($childProcesses | ForEach-Object Id) -join ', ')). Waiting..."
    $childProcesses | Wait-Process
    Write-Host "[run-stress] All $Processes processes exited." -ForegroundColor Green
    return
}

# Assemble client args. Format matches ParseArgs in Client.V*/Program.cs.
$clientArgs = @(
    '--url', $Url,
    '--players', $Players,
    '--max-clans', $MaxClans,
    '--duration', $Duration,
    '--delay', $Delay,
    '--prefix', $Prefix
)

if ($MuxChannels -gt 0)
{
    $clientArgs += @(
        '--mux-channels', $MuxChannels,
        '--mux-url', $MuxUrl,
        '--mux-batch', $MuxBatch.IsPresent.ToString().ToLowerInvariant(),
        '--mux-batch-size', $MuxBatchSize,
        '--mux-batch-flush', $MuxBatchFlush
    )
}

# Banner
$transport = if ($MuxChannels -gt 0)
{
    $b = if ($MuxBatch) { ", batched (size=$MuxBatchSize, flush=$MuxBatchFlush ms)" } else { '' }
    "mux@$MuxUrl ($MuxChannels channels$b, ~$([Math]::Max(1, [Math]::Floor($Players / $MuxChannels))) players/socket)"
}
else
{
    "plain@$Url (1 socket per player)"
}

Write-Host "[run-stress] $Variant client, $Players players, ${Duration}s ($Framework / $Configuration)" -ForegroundColor Green
Write-Host "[run-stress] Transport: $transport"
Write-Host "[run-stress] Args:      $($clientArgs -join ' ')"
Write-Host ""

& dotnet run --project $ClientProj -c $Configuration -f $Framework --no-build:$($NoBuild.IsPresent) -- @clientArgs
