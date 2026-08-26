#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Refresh ONE redb.* library in an existing shared layer — WITHOUT rebuilding Tsak/Identity.

.DESCRIPTION
    This is the payoff of the shared-layer move (docs/SHARED_RUNTIME_PLAN.md): a binary-compatible
    patch of a leaf/provider/connector (e.g. redb.Route.Http 3.4.0 -> 3.4.1, or a beta connector)
    ships by swapping its DLL in Libs/shared. The framework is byte-loaded from there at startup, so
    no host rebuild, no giant archive re-spin.

    The script:
      1. resolves the project (redb.Route/src/<name> OR <repoRoot>/<name>),
      2. `dotnet publish`es it to a temp dir,
      3. copies <Lib>.dll (+ its runtimes/ native, if any) into the target shared directory,
      4. warns about any NEW transitive dependency the publish produced that is missing from the
         target — a hint that this is more than a compatible patch (may need a fuller rebuild).

    It does NOT re-zip or re-sign an archive — that is packaging-specific. After running against an
    archive's Libs/shared, re-zip and re-cosign the archive as usual (see publish/HOW_TO_PUBLISH.md).

    COMPAT NOTE: the running host enforces a compat-gate (redb minor must match Tsak's). Only swap in
    a DLL of the SAME minor as the archive's Tsak — patches (3rd number) are fine, a new minor is not.

.PARAMETER Lib
    Assembly/project simple name, e.g. redb.Route.Http, redb.SQLite.Pro, redb.Route.Kafka.

.PARAMETER SharedDir
    Target shared directory to update. Default: the dev Worker Libs/shared.
    For an archive, pass its <archive>/worker/Libs/shared.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER Tfm
    Target framework. Default: net10.0.

.EXAMPLE
    ./scripts/refresh-shared.ps1 -Lib redb.Route.Http
    ./scripts/refresh-shared.ps1 -Lib redb.SQLite.Pro -SharedDir D:\dist\redb-tsak-3.3.3-win-x64\worker\Libs\shared
#>
param(
    [Parameter(Mandatory)][string]$Lib,
    [string]$SharedDir = "",
    [string]$Configuration = "Release",
    [string]$Tfm = "net10.0",
    # Same as in build-shared.ps1: empty = monorepo layout, otherwise a standalone redb-route checkout.
    [string]$RouteSrc = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot                          # redb.Tsak/scripts
$tsakRoot  = Split-Path -Parent $scriptDir          # redb.Tsak
$repoRoot  = Split-Path -Parent $tsakRoot           # csharp/redb
if ([string]::IsNullOrWhiteSpace($RouteSrc)) {
    $routeRoot = Join-Path (Join-Path $repoRoot "redb.Route") "src"
} else {
    $routeRoot = (Resolve-Path $RouteSrc).Path
    if (-not (Test-Path (Join-Path $routeRoot "redb.Route"))) {
        $nested = Join-Path $routeRoot "src"
        if (Test-Path (Join-Path $nested "redb.Route")) { $routeRoot = $nested }
    }
}

if ([string]::IsNullOrWhiteSpace($SharedDir)) {
    $SharedDir = Join-Path (Join-Path (Join-Path (Join-Path $tsakRoot "src") "redb.Tsak.Worker") "Libs") "shared"
}

# Resolve project path: connectors under redb.Route/src, framework/providers at repo root.
$projPath = Join-Path (Join-Path $routeRoot $Lib) "$Lib.csproj"
if (-not (Test-Path $projPath)) { $projPath = Join-Path (Join-Path $repoRoot $Lib) "$Lib.csproj" }
if (-not (Test-Path $projPath)) { throw "Project for '$Lib' not found under redb.Route/src or repo root." }

if (-not (Test-Path $SharedDir)) { throw "Target shared dir not found: $SharedDir" }

Write-Host "=== refresh-shared.ps1 ===" -ForegroundColor Cyan
Write-Host "Lib:        $Lib"
Write-Host "Project:    $projPath"
Write-Host "SharedDir:  $SharedDir"
Write-Host "Config/Tfm: $Configuration / $Tfm"
Write-Host ""

$pubDir = Join-Path (Join-Path $env:TEMP "tsak_refresh_shared") $Lib
if (Test-Path $pubDir) { Remove-Item -Recurse -Force $pubDir }

Write-Host "Publishing $Lib..." -NoNewline
$log = Join-Path $env:TEMP "tsak_refresh_$Lib.log"
& dotnet publish $projPath -c $Configuration -f $Tfm --nologo -v q -o $pubDir 2>&1 | Out-File -FilePath $log -Encoding UTF8
if ($LASTEXITCODE -ne 0) { Write-Host " FAILED (log: $log)" -ForegroundColor Red; throw "publish failed for $Lib" }
Write-Host " OK" -ForegroundColor Green

$srcDll = Join-Path $pubDir "$Lib.dll"
if (-not (Test-Path $srcDll)) { throw "Published output missing $Lib.dll" }

$oldVer = if (Test-Path (Join-Path $SharedDir "$Lib.dll")) {
    [Reflection.AssemblyName]::GetAssemblyName((Join-Path $SharedDir "$Lib.dll")).Version.ToString()
} else { "(absent)" }
$newVer = [Reflection.AssemblyName]::GetAssemblyName($srcDll).Version.ToString()

# 1. The library DLL itself.
Copy-Item $srcDll -Destination $SharedDir -Force
Write-Host "  updated $Lib.dll : $oldVer -> $newVer" -ForegroundColor Green

# 2. Native runtimes it may carry (e.g. Kafka librdkafka, SQLite e_sqlite3).
$srcRuntimes = Join-Path $pubDir "runtimes"
if (Test-Path $srcRuntimes) {
    Copy-Item -Path $srcRuntimes -Destination $SharedDir -Recurse -Force
    Write-Host "  + runtimes/ refreshed" -ForegroundColor DarkGray
}

# 3. Warn about NEW managed deps the publish produced that are absent from the target — a sign this
#    is more than a drop-in patch (a new/updated transitive dependency). Host-provided BCL/extension
#    assemblies (Microsoft.*, System.*) are normally served from the app bin, not the shared layer,
#    so they are expected-absent here and would only be noise — skip them.
$newDeps = @()
foreach ($dll in Get-ChildItem -Path $pubDir -Filter "*.dll") {
    if ($dll.Name -ieq "$Lib.dll") { continue }
    if ($dll.Name -like "Microsoft.*" -or $dll.Name -like "System.*") { continue }
    if (-not (Test-Path (Join-Path $SharedDir $dll.Name))) { $newDeps += $dll.Name }
}
if ($newDeps.Count -gt 0) {
    Write-Host ""
    Write-Host "  NOTE: publish produced deps not present in the shared layer (host bin may still" -ForegroundColor Yellow
    Write-Host "  provide them). Review before shipping — if any is genuinely new, add it to shared:" -ForegroundColor Yellow
    $newDeps | ForEach-Object { Write-Host "    - $_" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "Done. For an archive: re-zip and re-cosign it (see publish/HOW_TO_PUBLISH.md)." -ForegroundColor Cyan
Write-Host "The host compat-gate requires the SAME redb minor as its Tsak build (patches ok)." -ForegroundColor DarkGray
