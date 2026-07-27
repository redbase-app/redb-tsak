#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds Route connector (and optionally framework) projects and copies their DLLs
    into the shared assembly layer that SharedAssemblyLoader reads at runtime.

    Single source of truth for the connector/framework list: scripts/shared-manifest.psd1.
    This one script covers BOTH modes (the old build-shared-multitfm.ps1 is gone):

      * DEV mode (default, no -OutRoot):
          - output: src/redb.Tsak.Worker/Libs/shared
          - single TFM (net9.0), Configuration=Debug
          - filters out DLLs already present in Worker bin AND in the .NET shared framework
          - hard-fails (exit 1) if any connector fails to build

      * PUBLISH mode (-OutRoot given):
          - output: <OutRoot>/shared-<tfm-without-dots>   (e.g. shared-net90) per TFM
          - one dir per -Tfms entry, Configuration=Release (pass -Configuration Release)
          - filters out DLLs already present in the .NET shared framework only
            (no single Worker bin exists at multi-TFM publish time)
          - tolerates per-TFM build failures (tracked as "incompatible"), never hard-fails

.PARAMETER Configuration
    Build configuration. Default: Debug (dev). Publish pipeline passes Release.

.PARAMETER Tfms
    Target framework monikers. Default: net9.0. Publish may pass e.g. net8.0,net9.0.

.PARAMETER OutRoot
    When set, switches to PUBLISH mode and writes shared-<tfm> dirs under this root
    (e.g. publish/staging). When empty, DEV mode writes to Worker/Libs/shared.

.PARAMETER IncludeFramework
    Also build the Framework section from the manifest (redb.Core/Route.Core/providers).
    Dormant for stage A (composition unchanged) — used from stage B onward when framework
    moves out of bin into shared.

.PARAMETER Only
    Build only the specified project(s). Matched case-insensitively against the full id
    or its tail (e.g. "IbmMq" matches "redb.Route.IbmMq").

.PARAMETER Clean
    Clean the output directory/directories before copying.

.EXAMPLE
    ./scripts/build-shared.ps1
    ./scripts/build-shared.ps1 -Clean -Only IbmMq,RabbitMQ
    ./scripts/build-shared.ps1 -Configuration Release -Tfms net8.0,net9.0 -OutRoot publish/staging
#>
param(
    [string]$Configuration = "Debug",
    [string[]]$Tfms = @('net9.0'),
    [string]$OutRoot = "",
    [switch]$IncludeFramework,
    [switch]$Clean,
    [string[]]$Only = @(),
    # Where the redb.Route / redb core sources live. Empty = monorepo layout (../../redb.Route/src).
    # The public redb-tsak repository is standalone, so there this points at a separate clone of
    # https://github.com/redbase-app/redb-route — e.g. -RouteSrc ..\redb-route
    # This parameter is why there is no second, "public" copy of this script: a copy drifted for
    # three weeks and shipped a version that could not stage the framework at all.
    [string]$RouteSrc = ""
)

$ErrorActionPreference = "Stop"

# pwsh -File passes "-Tfms net8.0,net9.0" / "-Only a,b" as a single element — normalize.
$Tfms = @($Tfms | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$scriptDir = $PSScriptRoot                              # redb.Tsak/scripts
$tsakRoot  = Split-Path -Parent $scriptDir             # redb.Tsak
$repoRoot  = Split-Path -Parent $tsakRoot              # csharp/redb

# Route sources: monorepo layout by default; -RouteSrc for a standalone checkout. Accept either
# the repo root of redb-route or its src/ directly — telling a user which one to pass is a support
# question nobody should have to ask.
if ([string]::IsNullOrWhiteSpace($RouteSrc)) {
    $routeRoot = Join-Path (Join-Path $repoRoot "redb.Route") "src"
} else {
    $routeRoot = (Resolve-Path $RouteSrc).Path
    if (-not (Test-Path (Join-Path $routeRoot "redb.Route"))) {
        $nested = Join-Path $routeRoot "src"
        if (Test-Path (Join-Path $nested "redb.Route")) { $routeRoot = $nested }
    }
}
if (-not (Test-Path $routeRoot)) {
    throw "Route sources not found: $routeRoot. Pass -RouteSrc <path to a redb-route checkout>."
}

$publishMode = -not [string]::IsNullOrWhiteSpace($OutRoot)

# ---- Load the manifest (single source of truth) ----
$manifestPath = Join-Path $scriptDir "shared-manifest.psd1"
if (-not (Test-Path $manifestPath)) { throw "Manifest not found: $manifestPath" }
$manifest = Import-PowerShellDataFile -Path $manifestPath

$projects = @($manifest.Connectors)
if ($IncludeFramework) {
    # Additive, de-duplicated (a name can appear only once even if both sections list it).
    $projects = @($projects + $manifest.Framework) | Select-Object -Unique
}

# ---- Apply -Only filter ----
if ($Only.Count -gt 0) {
    $filtered = @()
    foreach ($pat in $Only) {
        $m = $projects | Where-Object { $_ -ieq $pat -or $_ -ieq "redb.Route.$pat" -or $_ -like "*$pat*" }
        if (-not $m) { Write-Host "WARNING: -Only '$pat' did not match any project" -ForegroundColor Yellow }
        else { $filtered += $m }
    }
    $projects = @($filtered | Select-Object -Unique)
    if ($projects.Count -eq 0) { Write-Host "Nothing matched -Only; done." -ForegroundColor Yellow; exit 0 }
}

$dotnetRoot = Split-Path (Get-Command dotnet).Source

function Get-RuntimeDir([string]$tfm) {
    $tfmVersion = $tfm -replace '^net', ''            # net9.0 -> 9.0
    $dir = Get-ChildItem (Join-Path (Join-Path $dotnetRoot "shared") "Microsoft.NETCore.App") -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "^$([regex]::Escape($tfmVersion))\." } |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $dir) {
        Write-Host "WARNING: no .NET $tfmVersion runtime dir, framework filter disabled for $tfm" -ForegroundColor Yellow
        return "__nonexistent__"
    }
    return $dir
}

# In DEV mode, also filter against the Worker bin (framework/host deps already there).
$workerBin = $null
if (-not $publishMode) {
    $workerBin = Join-Path (Join-Path (Join-Path (Join-Path (Join-Path $tsakRoot "src") "redb.Tsak.Worker") "bin") $Configuration) $Tfms[0]
}

Write-Host "=== build-shared.ps1 ===" -ForegroundColor Cyan
Write-Host "Mode:          $(if ($publishMode) { 'PUBLISH' } else { 'DEV' })"
Write-Host "Configuration: $Configuration"
Write-Host "Tfms:          $($Tfms -join ', ')"
Write-Host "Route root:    $routeRoot"
Write-Host "IncludeFramework: $IncludeFramework"
Write-Host ""

if (-not (Test-Path $routeRoot)) { throw "redb.Route source not found at $routeRoot" }

# Builds one TFM into $sharedDir. Returns @{ Copied=<int>; Incompatible=@(...) }.
function Build-Tfm([string]$tfm, [string]$sharedDir, [string]$runtimeDir) {
    if ($Clean -and (Test-Path $sharedDir)) {
        Write-Host "Cleaning $sharedDir..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $sharedDir
    }
    if (-not (Test-Path $sharedDir)) { New-Item -ItemType Directory -Path $sharedDir -Force | Out-Null }

    $copied = 0
    $incompatible = @()
    foreach ($proj in $projects) {
        # Connectors live under redb.Route/src/<name>; framework/providers (redb.Core[.Pro],
        # redb.Postgres[.Pro], redb.MSSql[.Pro], redb.SQLite[.Pro]) live at the repo root.
        $projPath = Join-Path (Join-Path $routeRoot $proj) "$proj.csproj"
        if (-not (Test-Path $projPath)) {
            $projPath = Join-Path (Join-Path $repoRoot $proj) "$proj.csproj"
        }
        if (-not (Test-Path $projPath)) {
            Write-Host "  SKIP $proj (project not found)" -ForegroundColor Yellow
            continue
        }

        $tmpPub = Join-Path (Join-Path $env:TEMP "tsak_shared_$tfm") $proj
        Write-Host "  $proj ($tfm)..." -NoNewline
        $logFile = Join-Path $env:TEMP "tsak_shared_$tfm`_$proj.log"
        & dotnet publish $projPath -c $Configuration -f $tfm --nologo -v q -o $tmpPub 2>&1 |
            Out-File -FilePath $logFile -Encoding UTF8
        if ($LASTEXITCODE -ne 0) {
            if ($publishMode) {
                Write-Host " INCOMPATIBLE" -ForegroundColor Yellow
                $incompatible += $proj
                continue
            } else {
                Write-Host " FAILED (log: $logFile)" -ForegroundColor Red
                throw "Build failed for $proj"
            }
        }
        Write-Host " OK" -ForegroundColor Green

        if (Test-Path $tmpPub) {
            $dlls = Get-ChildItem -Path $tmpPub -Filter "*.dll" | Where-Object {
                (-not (Test-Path (Join-Path $runtimeDir $_.Name))) -and
                ($publishMode -or -not (Test-Path (Join-Path $workerBin $_.Name)))
            }
            foreach ($dll in $dlls) {
                Copy-Item $dll.FullName -Destination $sharedDir -Force
                $copied++
            }
            $runtimes = Join-Path $tmpPub "runtimes"
            if (Test-Path $runtimes) {
                Copy-Item -Path $runtimes -Destination $sharedDir -Recurse -Force
            }
        }
    }
    return @{ Copied = $copied; Incompatible = $incompatible }
}

$summary = @{}
foreach ($tfm in $Tfms) {
    if ($publishMode) {
        Write-Host "===== TFM: $tfm =====" -ForegroundColor Magenta
        $sharedDir = Join-Path $OutRoot "shared-$($tfm -replace '\.', '')"
    } else {
        # Shared DLLs go into the source tree — MSBuild copies them to output via CopyToOutputDirectory.
        $sharedDir = Join-Path (Join-Path (Join-Path (Join-Path $tsakRoot "src") "redb.Tsak.Worker") "Libs") "shared"
    }
    $runtimeDir = Get-RuntimeDir $tfm
    Write-Host "Shared dir:    $sharedDir"
    Write-Host "Runtime dir:   $runtimeDir"
    $summary[$tfm] = Build-Tfm $tfm $sharedDir $runtimeDir
    Write-Host "  -> $($summary[$tfm].Copied) DLLs in $sharedDir" -ForegroundColor Cyan
    Write-Host ""
}

Write-Host "=== Summary ===" -ForegroundColor Cyan
$anyIncompatible = $false
foreach ($tfm in $Tfms) {
    $inc = $summary[$tfm].Incompatible
    if ($inc.Count -gt 0) {
        $anyIncompatible = $true
        Write-Host "$tfm incompatible: $($inc -join ', ')" -ForegroundColor Yellow
    } else {
        Write-Host "$tfm OK ($($summary[$tfm].Copied) DLLs)" -ForegroundColor Green
    }
}

# In DEV mode a failing build already threw. In PUBLISH mode incompatibility is tolerated
# (matches the old build-shared-multitfm.ps1 behavior) — exit 0 so the pipeline continues.
Write-Host "Done." -ForegroundColor Green
