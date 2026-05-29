#requires -Version 5.1
<#
.SYNOPSIS
    Interactive dev-toggle for the Expedition example.

.DESCRIPTION
    Flips four independent switches for the Expedition example (Unity client +
    .NET server solution) without changing any other state:

      1) Unity SharedMeta source -- git URL <-> local file:../../../../../com.coregame.sharedmeta
      2) Unity Backend.Local plugin -- absent <-> local file path to Plugins/SharedMeta.Backend.Local
      3) Server NuGet local feed -- nuget.org only <-> nuget.org + local nupkgs feeds
      4) Full refresh -- purge NuGet cache for CoreGame.SharedMeta.*, pack SharedMeta locally,
         restore the ServerSolution. Implemented fully in-process (no bash dependency).

    Current state is derived from the on-disk files, not stored anywhere.

.PARAMETER Nuget
    Skip the interactive menu and run the full refresh immediately, then exit.

.PARAMETER Version
    Override the SharedMeta version for the refresh (defaults to com.coregame.sharedmeta/package.json).
#>
param(
    [switch] $Nuget,
    [string] $Version
)

$ErrorActionPreference = 'Stop'

# ---------- Paths --------------------------------------------------------------
$ScriptDir       = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot        = Split-Path -Parent $ScriptDir                                  # D:\coregame\SharedMeta
$CoreGameRoot    = Split-Path -Parent $RepoRoot                                   # D:\coregame
$UnityPkgRoot    = Join-Path $RepoRoot 'com.coregame.sharedmeta'
$PackageJson     = Join-Path $UnityPkgRoot 'package.json'
$ManifestJson    = Join-Path $ScriptDir 'Unity\Expedition\Client\Packages\manifest.json'
$NuGetConfig     = Join-Path $ScriptDir 'Unity\Expedition\ServerSolution\NuGet.Config'
$ServerSolutionDir = Join-Path $ScriptDir 'Unity\Expedition\ServerSolution'
$ServerPackagesProps = Join-Path $ServerSolutionDir 'Directory.Packages.props'
$NupkgsDir       = Join-Path $RepoRoot 'nupkgs'
$SolutionFile    = Join-Path $RepoRoot 'SharedMeta.slnx'
$GeneratorDll    = Join-Path $RepoRoot 'src\SharedMeta.Generator\bin\Release\netstandard2.0\SharedMeta.Generator.dll'
$UpmAnalyzerDll  = Join-Path $UnityPkgRoot 'Runtime\Analyzers\SharedMeta.Generator.dll'

# Relative paths embedded into the JSON / XML files (forward slashes for Unity & NuGet).
$UnitySharedMetaLocal   = 'file:../../../../../com.coregame.sharedmeta'
$UnityBackendLocal      = 'file:../../../../../../Plugins/SharedMeta.Backend.Local/com.coregame.sharedmeta.backend.local'
$ServerLocalFeedValue   = '../../../../nupkgs'
$ServerBackendFeedValue = '../../../../../Plugins/SharedMeta.Backend.Local/nupkgs'

# ---------- Helpers ------------------------------------------------------------
function Get-PackageVersion {
    if (-not (Test-Path $PackageJson)) { return $null }
    $raw = Get-Content -Raw -LiteralPath $PackageJson
    if ($raw -match '"version"\s*:\s*"([^"]+)"') { return $Matches[1] }
    return $null
}

function Get-GitUrl {
    $ver = Get-PackageVersion
    if (-not $ver) { $ver = '0.0.0' }
    return "https://github.com/CoreGameIO/SharedMeta.git?path=com.coregame.sharedmeta#v$ver"
}

function Read-Manifest { Get-Content -Raw -LiteralPath $ManifestJson }
function Write-Manifest($text) { Set-Content -LiteralPath $ManifestJson -Value $text -NoNewline }

function Read-NuGetConfig { Get-Content -Raw -LiteralPath $NuGetConfig }
function Write-NuGetConfig($text) { Set-Content -LiteralPath $NuGetConfig -Value $text -NoNewline }

function Get-UnitySharedMetaState {
    $text = Read-Manifest
    if ($text -match '"com.coregame.sharedmeta"\s*:\s*"file:[^"]+"') { return 'local' }
    if ($text -match '"com.coregame.sharedmeta"\s*:\s*"https://[^"]+"') { return 'git' }
    return 'unknown'
}

function Get-UnityBackendLocalState {
    $text = Read-Manifest
    if ($text -match '"com.coregame.sharedmeta.backend.local"\s*:') { return 'present' }
    return 'absent'
}

function Get-ServerLocalFeedState {
    if (-not (Test-Path $NuGetConfig)) { return 'unknown' }
    $text = Read-NuGetConfig
    $hasShared  = $text -match [regex]::Escape($ServerLocalFeedValue)
    $hasBackend = $text -match [regex]::Escape($ServerBackendFeedValue)
    if ($hasShared -and $hasBackend) { return 'both' }
    if ($hasShared)  { return 'sharedmeta' }
    if ($hasBackend) { return 'backend' }
    return 'off'
}

# ---------- manifest.json mutations -------------------------------------------
function Set-UnitySharedMeta($mode) {
    $text = Read-Manifest
    $replacement = if ($mode -eq 'local') { $UnitySharedMetaLocal } else { Get-GitUrl }
    $new = [regex]::Replace(
        $text,
        '("com\.coregame\.sharedmeta"\s*:\s*")[^"]+(")',
        { param($m) $m.Groups[1].Value + $replacement + $m.Groups[2].Value })
    if ($new -eq $text) {
        Write-Host "  (no change -- com.coregame.sharedmeta entry not found in manifest)" -ForegroundColor Yellow
        return
    }
    Write-Manifest $new
    Write-Host "  OK Unity SharedMeta -> $mode" -ForegroundColor Green
}

function Set-UnityBackendLocal($mode) {
    $text = Read-Manifest
    $present = $text -match '"com.coregame.sharedmeta.backend.local"\s*:'
    if ($mode -eq 'present') {
        if ($present) { Write-Host "  (already present)" -ForegroundColor Yellow; return }
        # Insert immediately after com.coregame.sharedmeta line, preserving indentation + trailing comma.
        $pattern = '("com\.coregame\.sharedmeta"\s*:\s*"[^"]+",?)(\r?\n)(\s*)'
        $m = [regex]::Match($text, $pattern)
        if (-not $m.Success) {
            Write-Host "  ERR Could not locate com.coregame.sharedmeta entry to anchor insertion" -ForegroundColor Red
            return
        }
        $indent = $m.Groups[3].Value
        $newline = $m.Groups[2].Value
        $insert = "$indent`"com.coregame.sharedmeta.backend.local`": `"$UnityBackendLocal`",$newline"
        $new = $text.Substring(0, $m.Index + $m.Length) + $insert + $text.Substring($m.Index + $m.Length)
        Write-Manifest $new
        Write-Host "  OK Unity Backend.Local -> present" -ForegroundColor Green
    }
    else {
        if (-not $present) { Write-Host "  (already absent)" -ForegroundColor Yellow; return }
        $new = [regex]::Replace(
            $text,
            '\s*"com\.coregame\.sharedmeta\.backend\.local"\s*:\s*"[^"]+",?\s*(\r?\n)',
            "`r`n")
        # Collapse any duplicate blank lines introduced.
        $new = $new -replace '(\r?\n)\r?\n(\s*")', '$1$2'
        Write-Manifest $new
        Write-Host "  OK Unity Backend.Local -> absent" -ForegroundColor Green
    }
}

# ---------- NuGet.Config mutations --------------------------------------------
function Set-ServerLocalFeed($mode) {
    if (-not (Test-Path $NuGetConfig)) {
        Write-Host "  ERR $NuGetConfig not found" -ForegroundColor Red
        return
    }
    $text = Read-NuGetConfig

    # Strip any existing local feed entries; we re-add based on $mode.
    # Match the entire line incl. its own indent + trailing newline (multiline mode).
    # value="[^"]+" rather than [^/]* because our paths contain '/'.
    $stripped = $text
    $stripped = [regex]::Replace($stripped, '(?m)^[ \t]*<add\s+key="local-sharedmeta"\s+value="[^"]+"\s*/>[ \t]*\r?\n', '')
    $stripped = [regex]::Replace($stripped, '(?m)^[ \t]*<add\s+key="local-backend-local"\s+value="[^"]+"\s*/>[ \t]*\r?\n', '')

    $addLines = New-Object System.Collections.Generic.List[string]
    if ($mode -eq 'sharedmeta' -or $mode -eq 'both') {
        $addLines.Add('    <add key="local-sharedmeta" value="' + $ServerLocalFeedValue + '" />')
    }
    if ($mode -eq 'backend' -or $mode -eq 'both') {
        $addLines.Add('    <add key="local-backend-local" value="' + $ServerBackendFeedValue + '" />')
    }

    if ($addLines.Count -gt 0) {
        # Insert immediately before </packageSources>, preserving its existing indent.
        $insertion = ($addLines -join "`r`n") + "`r`n"
        $new = [regex]::Replace($stripped, '(?m)^([ \t]*)(</packageSources>)', { param($m) $insertion + $m.Groups[1].Value + $m.Groups[2].Value })
    }
    else {
        $new = $stripped
    }

    if ($new -eq $text) {
        Write-Host "  (no change -- already $mode)" -ForegroundColor Yellow
        return
    }
    Write-NuGetConfig $new
    Write-Host "  OK Server local feed -> $mode" -ForegroundColor Green
}

# ---------- Status display ----------------------------------------------------
function Show-Status {
    $unity   = Get-UnitySharedMetaState
    $backend = Get-UnityBackendLocalState
    $feed    = Get-ServerLocalFeedState
    $ver     = Get-PackageVersion
    Write-Host ""
    Write-Host "===============================================================" -ForegroundColor Cyan
    Write-Host "  Expedition dev toggles" -ForegroundColor Cyan
    Write-Host "  SharedMeta package.json version: $ver" -ForegroundColor DarkGray
    Write-Host "===============================================================" -ForegroundColor Cyan
    Write-Host ("  Unity SharedMeta      : {0}" -f $unity)
    Write-Host ("  Unity Backend.Local   : {0}" -f $backend)
    Write-Host ("  Server local feed     : {0}" -f $feed)
    Write-Host ""
}

# ---------- Action: full refresh ----------------------------------------------
# Port of refresh-expedition-nugets.sh + pack-nugets.sh, fully in-process:
#   1) Purge CoreGame.SharedMeta.* from NuGet global-packages and http-cache
#   2) Build SharedMeta.slnx (Release) and pack to ./nupkgs/ at $versionOverride
#      or whatever's in package.json
#   3) Copy fresh generator DLL into the UPM Analyzers/ folder
#   4) Ensure local-sharedmeta feed is present in NuGet.Config
#   5) dotnet restore --force --no-cache against the ServerSolution
function Invoke-DotnetOrThrow {
    param([string] $what, [string[]] $argv)
    & dotnet @argv
    if ($LASTEXITCODE -ne 0) { throw "${what}: dotnet exited with $LASTEXITCODE" }
}

function Clear-SharedMetaNuGetCache {
    Write-Host "[1/4] Clearing CoreGame.SharedMeta.* from NuGet caches..."
    $localsRaw = & dotnet nuget locals global-packages --list 2>$null
    $globalPackages = $null
    foreach ($line in $localsRaw) {
        if ($line -match '^\s*global-packages:\s*(.+)$') { $globalPackages = $Matches[1].Trim(); break }
    }
    if (-not $globalPackages -or -not (Test-Path -LiteralPath $globalPackages)) {
        Write-Host "  ! Could not resolve NuGet global-packages folder; skipping purge." -ForegroundColor Yellow
    } else {
        Write-Host "  Cache: $globalPackages" -ForegroundColor DarkGray
        $removed = 0
        Get-ChildItem -LiteralPath $globalPackages -Directory -Filter 'coregame.sharedmeta*' -ErrorAction SilentlyContinue | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "    removed $($_.Name)" -ForegroundColor DarkGray
            $removed++
        }
        if ($removed -eq 0) {
            Write-Host "  (nothing to remove)"
        } else {
            Write-Host "  OK Removed $removed cached package directorie(s)" -ForegroundColor Green
        }
    }

    $httpRaw = & dotnet nuget locals http-cache --list 2>$null
    $httpCache = $null
    foreach ($line in $httpRaw) {
        if ($line -match '^\s*http-cache:\s*(.+)$') { $httpCache = $Matches[1].Trim(); break }
    }
    if ($httpCache -and (Test-Path -LiteralPath $httpCache)) {
        Get-ChildItem -LiteralPath $httpCache -Recurse -File -Filter '*coregame.sharedmeta*' -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-PackSharedMeta {
    param([string] $versionOverride)

    if (-not (Test-Path -LiteralPath $SolutionFile)) {
        throw "Solution not found: $SolutionFile"
    }

    if (Test-Path -LiteralPath $NupkgsDir) {
        Remove-Item -LiteralPath $NupkgsDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $NupkgsDir | Out-Null

    $versionArgs = @()
    $versionLabel = '(from Directory.Build.props)'
    if ($versionOverride) {
        $versionArgs = @("-p:Version=$versionOverride")
        $versionLabel = "$versionOverride (override)"
    }

    Write-Host "[2/4] Packing SharedMeta NuGets..."
    Write-Host "  Version: $versionLabel" -ForegroundColor DarkGray
    Write-Host "  Output : $NupkgsDir" -ForegroundColor DarkGray

    Push-Location $RepoRoot
    try {
        Write-Host "  Building solution..."
        Invoke-DotnetOrThrow 'build' (@('build', $SolutionFile, '-c', 'Release', '--nologo', '-v', 'q') + $versionArgs)
        Write-Host "  OK build succeeded" -ForegroundColor Green

        Write-Host "  Packing..."
        Invoke-DotnetOrThrow 'pack'  (@('pack',  $SolutionFile, '-c', 'Release', '--no-build', '--nologo', '-v', 'q', '-o', $NupkgsDir) + $versionArgs)

        if (Test-Path -LiteralPath $GeneratorDll) {
            Copy-Item -LiteralPath $GeneratorDll -Destination $UpmAnalyzerDll -Force
            Write-Host "  OK Updated UPM analyzer: $UpmAnalyzerDll" -ForegroundColor Green
        } else {
            Write-Host "  ! Generator DLL not found at $GeneratorDll (UPM analyzer not refreshed)" -ForegroundColor Yellow
        }

        $pkgs = Get-ChildItem -LiteralPath $NupkgsDir -Filter '*.nupkg' -File | Sort-Object Name
        Write-Host "  Packages:" -ForegroundColor DarkGray
        foreach ($p in $pkgs) { Write-Host "    $($p.Name)" -ForegroundColor DarkGray }
    }
    finally {
        Pop-Location
    }
}

function Invoke-ServerRestore {
    Write-Host "[3/4] Verifying local SharedMeta feed in NuGet.Config..."
    $feed = Get-ServerLocalFeedState
    if ($feed -eq 'off' -or $feed -eq 'backend') {
        Write-Host "  Local SharedMeta feed missing; enabling it now." -ForegroundColor Yellow
        $newMode = if ($feed -eq 'backend') { 'both' } else { 'sharedmeta' }
        Set-ServerLocalFeed $newMode
    } else {
        Write-Host "  OK local feed already present ($feed)" -ForegroundColor Green
    }

    Write-Host "[4/4] dotnet restore --force --no-cache in ServerSolution..."
    Push-Location $ServerSolutionDir
    try {
        Invoke-DotnetOrThrow 'restore' @('restore', '--force', '--no-cache')
        Write-Host "  OK restore succeeded" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}

function Sync-ServerPackageVersions {
    param([string] $targetVersion)

    if (-not (Test-Path -LiteralPath $ServerPackagesProps)) {
        Write-Host "  ! $ServerPackagesProps not found; skipping version sync" -ForegroundColor Yellow
        return
    }
    $text = Get-Content -Raw -LiteralPath $ServerPackagesProps
    # Rewrite Version="..." on every <PackageVersion Include="CoreGame.SharedMeta.*" ...> line.
    # Match the package id first, then the Version attribute, so other PackageVersion lines
    # (Microsoft.Orleans.*, Serilog, MemoryPack) stay untouched.
    # Count via a script-scope tally because a PowerShell MatchEvaluator can't close over a local.
    $script:_bumpCount = 0
    $new = [regex]::Replace(
        $text,
        '(<PackageVersion\s+Include="CoreGame\.SharedMeta\.[^"]+"\s+Version=")([^"]+)(")',
        {
            param($m)
            if ($m.Groups[2].Value -ne $targetVersion) { $script:_bumpCount++ }
            return $m.Groups[1].Value + $targetVersion + $m.Groups[3].Value
        })
    $bumped = $script:_bumpCount

    if ($new -ne $text) {
        # Preserve a UTF-8 BOM if the original had one (MSBuild templates do); Set-Content
        # -NoNewline would silently strip it and leave a cosmetic diff on every refresh.
        $bytes = [System.IO.File]::ReadAllBytes($ServerPackagesProps)
        $hadBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        $enc = New-Object System.Text.UTF8Encoding($hadBom)
        [System.IO.File]::WriteAllText($ServerPackagesProps, $new, $enc)
        Write-Host "  OK Updated $bumped CoreGame.SharedMeta.* PackageVersion entries -> $targetVersion" -ForegroundColor Green
    } else {
        Write-Host "  Directory.Packages.props already at $targetVersion" -ForegroundColor DarkGray
    }
}

function Invoke-FullRefresh {
    param([string] $versionOverride)

    $resolvedVersion = $versionOverride
    if (-not $resolvedVersion) { $resolvedVersion = Get-PackageVersion }
    Write-Host ""
    Write-Host "===============================================================" -ForegroundColor Cyan
    Write-Host "  Refresh Expedition NuGets" -ForegroundColor Cyan
    Write-Host "  Version: $resolvedVersion" -ForegroundColor DarkGray
    Write-Host "===============================================================" -ForegroundColor Cyan

    try {
        Clear-SharedMetaNuGetCache
        # Always pass the resolved version through so build & pack stay aligned
        # with the header ("Version: ..." above), matching the bash refresh script.
        Invoke-PackSharedMeta -versionOverride $resolvedVersion
        # Sync Directory.Packages.props BEFORE restore — otherwise the server keeps pinning the
        # old version and NuGet either fails to find it in the local feed or quietly resolves
        # a stale copy from nuget.org instead of our freshly-packed nupkg.
        Write-Host "[3.5/4] Syncing CoreGame.SharedMeta.* versions in Directory.Packages.props..."
        Sync-ServerPackageVersions -targetVersion $resolvedVersion
        Invoke-ServerRestore
        Write-Host ""
        Write-Host "===============================================================" -ForegroundColor Cyan
        Write-Host "  Done. ServerSolution restored against $resolvedVersion." -ForegroundColor Green
        Write-Host "===============================================================" -ForegroundColor Cyan
    }
    catch {
        Write-Host ""
        Write-Host "ERR Refresh failed: $($_.Exception.Message)" -ForegroundColor Red
        if (-not $Nuget) { return }   # in interactive mode, fall back to the menu
        exit 1
    }
}

# ---------- Toggle helpers ----------------------------------------------------
function Toggle-UnitySharedMeta {
    $cur = Get-UnitySharedMetaState
    $target = if ($cur -eq 'local') { 'git' } else { 'local' }
    Write-Host "Switching Unity SharedMeta: $cur -> $target"
    Set-UnitySharedMeta $target
}

function Toggle-UnityBackendLocal {
    $cur = Get-UnityBackendLocalState
    $target = if ($cur -eq 'present') { 'absent' } else { 'present' }
    Write-Host "Switching Unity Backend.Local: $cur -> $target"
    Set-UnityBackendLocal $target
}

function Toggle-ServerLocalFeed {
    $cur = Get-ServerLocalFeedState
    # Cycle: off -> sharedmeta -> both -> off (skip 'backend' alone -- rarely useful).
    $target = switch ($cur) {
        'off'        { 'sharedmeta' }
        'sharedmeta' { 'both' }
        'both'       { 'off' }
        'backend'    { 'sharedmeta' }
        default      { 'sharedmeta' }
    }
    Write-Host "Cycling server local feed: $cur -> $target"
    Set-ServerLocalFeed $target
}

# ---------- Non-interactive entry point ---------------------------------------
if ($Nuget) {
    Invoke-FullRefresh -versionOverride $Version
    exit 0
}

# ---------- Menu loop ---------------------------------------------------------
$quit = $false
while (-not $quit) {
    Show-Status
    Write-Host "  1) Toggle Unity SharedMeta (git <-> local file:)" -ForegroundColor White
    Write-Host "  2) Toggle Unity Backend.Local plugin (add <-> remove)" -ForegroundColor White
    Write-Host "  3) Cycle server local feed (off -> sharedmeta -> both -> off)" -ForegroundColor White
    Write-Host "  4) Full refresh: purge cache + pack SharedMeta + restore ServerSolution" -ForegroundColor White
    Write-Host "  s) Refresh status" -ForegroundColor DarkGray
    Write-Host "  q) Quit" -ForegroundColor DarkGray
    Write-Host ""
    $choice = Read-Host "Choose"
    Write-Host ""
    switch ($choice.Trim().ToLower()) {
        '1' { Toggle-UnitySharedMeta }
        '2' { Toggle-UnityBackendLocal }
        '3' { Toggle-ServerLocalFeed }
        '4' { Invoke-FullRefresh -versionOverride $Version }
        's' { }
        'q' { $quit = $true }
        ''  { }
        default { Write-Host "Unknown choice: $choice" -ForegroundColor Red }
    }
}

Write-Host "Bye." -ForegroundColor DarkGray
