param(
    [switch]$Purge
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    if ($Purge) {
        docker compose down -v
    } else {
        docker compose down
    }
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}
