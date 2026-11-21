param(
    [int]$GrafanaPort = 3100,
    [int]$PromPort = 9091,
    [switch]$Open
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    $env:GRAFANA_PORT = "$GrafanaPort"
    $env:PROM_PORT = "$PromPort"

    docker compose up -d
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    Write-Host ""
    Write-Host "Unity Expedition observability stack is up:" -ForegroundColor Green
    Write-Host "  Grafana:    http://localhost:$GrafanaPort/d/sharedmeta-unity-expedition (admin / admin)"
    Write-Host "  Prometheus: http://localhost:$PromPort/targets"
    Write-Host "  Server:     http://localhost:5000/metrics (start ServerSolution/Expedition.Server)"
    Write-Host ""

    if ($Open) {
        Start-Process "http://localhost:$GrafanaPort/d/sharedmeta-unity-expedition"
    }
}
finally {
    Pop-Location
}
