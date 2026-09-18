#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds Route connector (and optionally framework) projects and copies their DLLs
    into the shared assembly layer that SharedAssemblyLoader reads at runtime.

    Single source of truth for the connector/framework list: scripts/shared-manifest.psd1.
    This one script covers BOTH modes (the old build-shared-multitfm.ps1 is gone):

      * DEV mode (default, no -OutRoot):
          - output: src/redb.Tsak.Worker/Libs/shared
          - single TFM (net10.0), Configuration=Debug
          - filters out DLLs already present in Worker bin AND in the .NET shared frameworks
          - hard-fails (exit 1) if any connector fails to build
          - then MIRRORS the layer into bin/<cfg>/<tfm>/Libs/shared, which is where the worker
            reads it from (AppContext.BaseDirectory). The csproj's PreserveNewest copy cannot do
            it: it keeps files the layer dropped and skips sources with an older timestamp.
            So after this script the worker starts on the new layer without a rebuild.

      * PUBLISH mode (-OutRoot given):
          - output: <OutRoot>/shared-<tfm-without-dots>   (e.g. shared-net100) per TFM
          - one dir per -Tfms entry, Configuration=Release (pass -Configuration Release)
          - filters out DLLs already present in the .NET shared frameworks only
            (no single Worker bin exists at multi-TFM publish time)
          - tolerates per-TFM build failures (tracked as "incompatible"), never hard-fails

    Both modes filter against the two shared frameworks the worker runs on, Microsoft.NETCore.App
    and Microsoft.AspNetCore.App, and place one version of each file: a file two projects publish
    in different versions fails the run (see -TakeHighest).

.PARAMETER Configuration
    Build configuration. Default: Debug (dev). Publish pipeline passes Release.

.PARAMETER Tfms
    Target framework monikers. Default: net10.0. Publish may pass e.g. net8.0,net10.0.

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

.PARAMETER TakeHighest
    When two projects publish the same file in different versions, keep the newest and warn
    instead of failing the run. The failure is the default on purpose: the fix is to pin the
    package to one version in the projects listed, as the layer loads exactly one copy.

.PARAMETER SelfTest
    Prove the version policy on two builds of one assembly from the local NuGet cache and exit.
    Publishes nothing; run it after touching Add-LayerFile or Compare-LayerVersion.

.EXAMPLE
    ./scripts/build-shared.ps1
    ./scripts/build-shared.ps1 -Clean -Only IbmMq,RabbitMQ
    ./scripts/build-shared.ps1 -Configuration Release -Tfms net8.0,net10.0 -OutRoot publish/staging
    ./scripts/build-shared.ps1 -SelfTest
#>
param(
    [string]$Configuration = "Debug",
    [string[]]$Tfms = @('net10.0'),
    [string]$OutRoot = "",
    [switch]$IncludeFramework,
    [switch]$Clean,
    [switch]$TakeHighest,
    [switch]$SelfTest,
    [string[]]$Only = @(),
    # Where the redb.Route / redb core sources live. Empty = monorepo layout (../../redb.Route/src).
    # The public redb-tsak repository is standalone, so there this points at a separate clone of
    # https://github.com/redbase-app/redb-route — e.g. -RouteSrc ..\redb-route
    # This parameter is why there is no second, "public" copy of this script: a copy drifted for
    # three weeks and shipped a version that could not stage the framework at all.
    [string]$RouteSrc = ""
)

$ErrorActionPreference = "Stop"

# pwsh -File passes "-Tfms net8.0,net10.0" / "-Only a,b" as a single element — normalize.
$Tfms = @($Tfms | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# ---- One version of each file in the layer ----
# Every project publishes its own copy of a shared dependency. Copying them over each other made
# the last project in the manifest win regardless of version (caught 2026-09-16: Microsoft.IdentityModel
# 8.0.0 from redb.Licensing overwrote 8.16.0 from redb.MSSql in the publish layer, and the worker
# loaded the 8.0.0 next to a host on 8.16.0). A duplicate is compared by version instead: an equal
# version is skipped, a different one is a conflict — the run fails with the list, or -TakeHighest
# keeps the newest and warns.

function Get-LayerFileVersion([string]$path) {
    # The assembly version is what binds at load time; the file version tells package builds apart
    # that share an assembly version. Native files (the SNI runtime) carry a file version only;
    # a file with neither is compared by its bytes.
    $asm = $null
    try {
        $asm = [System.Reflection.AssemblyName]::GetAssemblyName($path).Version
    } catch [System.BadImageFormatException] {
        $asm = $null   # not a managed assembly
    }
    $file = $null
    $fvi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    if ($fvi.FileVersion) {
        [version]$parsed = $null
        if ([version]::TryParse((($fvi.FileVersion -split '[ +-]')[0]), [ref]$parsed)) { $file = $parsed }
    }
    # Without a file version two copies of one assembly version can only be told apart by their bytes
    # (System.Diagnostics.EventLog.Messages.dll ships that way: an assembly version, no file version).
    $hash = $null
    if (-not $file) { $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash }
    $display = if ($asm) { "assembly $asm" } else { "native" }
    if ($file) { $display += ", file $file" }
    if ($hash) { $display += ", sha256 $($hash.Substring(0, 12))" }
    return @{ Assembly = $asm; File = $file; Hash = $hash; Display = $display }
}

function Compare-LayerVersion($a, $b) {
    # Positive when $a is newer, negative when older, 0 when equal, $null when the two cannot be ordered.
    if ($a.Assembly -and $b.Assembly -and $a.Assembly -ne $b.Assembly) { return $a.Assembly.CompareTo($b.Assembly) }
    if ($a.File -and $b.File) { return $a.File.CompareTo($b.File) }
    if ($a.Hash -and $b.Hash) { if ($a.Hash -eq $b.Hash) { return 0 } else { return $null } }
    return $null
}

# Places one file into the layer under $relative. Returns $true when the file was copied, $false when
# an equal copy is already there or a conflict was recorded. $placed and $mismatches are per layer.
function Add-LayerFile([hashtable]$placed, [System.Collections.ArrayList]$mismatches,
                       [string]$source, [string]$sharedDir, [string]$relative, [string]$project) {
    $key = $relative.ToLowerInvariant()
    $version = Get-LayerFileVersion $source
    $dest = Join-Path $sharedDir $relative
    if (-not $placed.ContainsKey($key)) {
        $destDir = Split-Path -Parent $dest
        if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
        Copy-Item $source -Destination $dest -Force
        $placed[$key] = @{ Version = $version; Project = $project }
        return $true
    }

    $first = $placed[$key]
    $cmp = Compare-LayerVersion $version $first.Version
    if ($null -ne $cmp -and $cmp -eq 0) { return $false }   # same version from another project: the first copy stays

    $entry = @{ File = $relative; Orderable = ($null -ne $cmp) }
    if ($TakeHighest -and $null -ne $cmp -and $cmp -gt 0) {
        Copy-Item $source -Destination $dest -Force
        $entry.Kept = "$($version.Display) from $project"
        $entry.Dropped = "$($first.Version.Display) from $($first.Project)"
        $placed[$key] = @{ Version = $version; Project = $project }
    } else {
        $entry.Kept = "$($first.Version.Display) from $($first.Project)"
        $entry.Dropped = "$($version.Display) from $project"
    }
    [void]$mismatches.Add($entry)
    return $false
}

# Prints the conflicts of one layer; throws unless -TakeHighest resolved every one of them.
function Assert-LayerConsistent([string]$tfm, [System.Collections.ArrayList]$mismatches) {
    if ($mismatches.Count -eq 0) { return }
    $color = if ($TakeHighest) { 'Yellow' } else { 'Red' }
    Write-Host "Version conflicts in the $tfm layer:" -ForegroundColor $color
    foreach ($m in $mismatches) {
        $note = if ($m.Orderable) { '' } else { ' (versions cannot be ordered)' }
        Write-Host "  $($m.File): kept $($m.Kept); dropped $($m.Dropped)$note" -ForegroundColor $color
    }
    if (-not $TakeHighest) {
        throw "Shared layer for $tfm carries $($mismatches.Count) file(s) in different versions across projects. " +
              "Pin the package to one version in the projects listed, or pass -TakeHighest to keep the newest."
    }
    $unordered = @($mismatches | Where-Object { -not $_.Orderable })
    if ($unordered.Count -gt 0) {
        throw "Shared layer for ${tfm}: $($unordered.Count) file(s) differ between projects and carry no comparable " +
              "version, so -TakeHighest cannot choose. Fix the projects listed."
    }
}

if ($SelfTest) {
    # Two builds of one assembly from the NuGet cache stand in for two projects. Nothing is published.
    $cache = Join-Path $HOME ".nuget/packages/microsoft.identitymodel.tokens"
    $builds = @(Get-ChildItem $cache -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        [version]$v = $null
        $dll = Get-ChildItem (Join-Path $_.FullName "lib") -Recurse -Filter "Microsoft.IdentityModel.Tokens.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($dll -and [version]::TryParse($_.Name, [ref]$v)) { @{ Version = $v; Dll = $dll } }
    } | Sort-Object { $_.Version })
    if ($builds.Count -lt 2) { throw "Self-test needs two builds of Microsoft.IdentityModel.Tokens in $cache; found $($builds.Count)." }
    $low = $builds[0]; $high = $builds[-1]
    $tmp = Join-Path $env:TEMP ("tsak_shared_selftest_" + [guid]::NewGuid().ToString('N'))
    $layer = Join-Path $tmp "layer"
    New-Item -ItemType Directory -Path $layer -Force | Out-Null
    try {
        $name = $high.Dll.Name
        $placedVersion = { (Get-LayerFileVersion (Join-Path $layer $name)).Display }

        # 1. Default policy: a second, different version is a conflict and the first copy stays.
        $placed = @{}; $mismatches = [System.Collections.ArrayList]::new()
        [void](Add-LayerFile $placed $mismatches $high.Dll.FullName $layer $name "proj-high")
        [void](Add-LayerFile $placed $mismatches $low.Dll.FullName $layer $name "proj-low")
        if ($mismatches.Count -ne 1) { throw "Self-test: expected one conflict, got $($mismatches.Count)." }
        if ((& $placedVersion) -ne (Get-LayerFileVersion $high.Dll.FullName).Display) { throw "Self-test: the default policy must keep the first copy." }
        $failed = $false
        try { Assert-LayerConsistent "selftest" $mismatches } catch { $failed = $true }
        if (-not $failed) { throw "Self-test: a conflict must fail the run without -TakeHighest." }

        # 2. The same version from two projects is not a conflict.
        $placed = @{}; $mismatches = [System.Collections.ArrayList]::new()
        [void](Add-LayerFile $placed $mismatches $high.Dll.FullName $layer $name "proj-a")
        [void](Add-LayerFile $placed $mismatches $high.Dll.FullName $layer $name "proj-b")
        if ($mismatches.Count -ne 0) { throw "Self-test: equal versions must not conflict." }

        # 3. -TakeHighest keeps the newest whichever project comes first, and does not fail.
        $TakeHighest = $true
        $placed = @{}; $mismatches = [System.Collections.ArrayList]::new()
        [void](Add-LayerFile $placed $mismatches $low.Dll.FullName $layer $name "proj-low")
        [void](Add-LayerFile $placed $mismatches $high.Dll.FullName $layer $name "proj-high")
        if ($mismatches.Count -ne 1) { throw "Self-test: -TakeHighest must still record the conflict." }
        if ((& $placedVersion) -ne (Get-LayerFileVersion $high.Dll.FullName).Display) { throw "Self-test: -TakeHighest must keep the newest." }
        Assert-LayerConsistent "selftest" $mismatches
        $TakeHighest = $false

        # 4. An assembly without a file version (the EventLog messages resource assembly) placed twice with
        #    the same bytes is not a conflict — caught on the first real run, where redb.MSSql and
        #    redb.MSSql.Pro both publish it under runtimes/ and the bytes were never compared.
        $messages = Get-ChildItem (Join-Path $HOME ".nuget/packages/system.diagnostics.eventlog") -Recurse -Filter "System.Diagnostics.EventLog.Messages.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $messages) { throw "Self-test needs System.Diagnostics.EventLog.Messages.dll in the NuGet cache." }
        $messagesVersion = Get-LayerFileVersion $messages.FullName
        if ($messagesVersion.File -or -not $messagesVersion.Hash) { throw "Self-test: $($messages.Name) was expected to carry no file version and a hash, got '$($messagesVersion.Display)'." }
        $placed = @{}; $mismatches = [System.Collections.ArrayList]::new()
        [void](Add-LayerFile $placed $mismatches $messages.FullName $layer $messages.Name "proj-a")
        [void](Add-LayerFile $placed $mismatches $messages.FullName $layer $messages.Name "proj-b")
        if ($mismatches.Count -ne 0) { throw "Self-test: equal bytes without a file version must not conflict." }

        Write-Host "Self-test OK: $name $($low.Version) vs $($high.Version) — conflict fails by default, equal versions pass, -TakeHighest keeps $($high.Version); $($messages.Name) without a file version compares by bytes." -ForegroundColor Green
    } finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
    exit 0
}

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

# The worker runs on two shared frameworks (redb.Tsak.Worker.runtimeconfig.json lists both). Filtering
# against Microsoft.NETCore.App alone let ASP.NET framework assemblies (System.Security.Cryptography.Pkcs
# among them) into the layer, in whichever version each project happened to publish; at runtime the
# framework copy wins over them, so they were dead weight that made the layer differ from run to run.
$sharedFrameworks = @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')

function Get-RuntimeDirs([string]$tfm) {
    $tfmVersion = $tfm -replace '^net', ''            # net10.0 -> 10.0
    $dirs = @()
    foreach ($framework in $sharedFrameworks) {
        $dir = Get-ChildItem (Join-Path (Join-Path $dotnetRoot "shared") $framework) -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match "^$([regex]::Escape($tfmVersion))\." } |
            Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
        if ($dir) {
            $dirs += $dir
        } else {
            Write-Host "WARNING: no $framework $tfmVersion runtime dir, its framework filter is disabled for $tfm" -ForegroundColor Yellow
        }
    }
    return $dirs
}

function Test-InFramework([string[]]$runtimeDirs, [string]$name) {
    foreach ($dir in $runtimeDirs) {
        if (Test-Path (Join-Path $dir $name)) { return $true }
    }
    return $false
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

# Builds one TFM into $sharedDir. Returns @{ Copied=<int>; Incompatible=@(...); Conflicts=<int> }.
function Build-Tfm([string]$tfm, [string]$sharedDir, [string[]]$runtimeDirs) {
    if ($Clean -and (Test-Path $sharedDir)) {
        Write-Host "Cleaning $sharedDir..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $sharedDir
    }
    if (-not (Test-Path $sharedDir)) { New-Item -ItemType Directory -Path $sharedDir -Force | Out-Null }

    $copied = 0
    $incompatible = @()
    $placed = @{}                                        # relative path -> version and project of the copy in the layer
    $mismatches = [System.Collections.ArrayList]::new()
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
                # The shared layer OWNS every redb.* except the two the Worker keeps in its bin on
                # purpose (redb.Tsak.*, redb.Licensing - see PruneRedbFromBuild in the Worker csproj).
                # Without this exemption the bin filter below drops the whole framework out of the
                # layer whenever a Worker build left redb.* in bin, and SharedRuntimeBootstrap then
                # refuses to start the worker: its fail-fast requires those assemblies IN the layer.
                # The filter stays in force for third-party deps, which is what it was meant for.
                $ownedByShared = $_.Name -like "redb.*" -and $_.Name -notlike "redb.Tsak.*" -and $_.Name -notlike "redb.Licensing.*"
                (-not (Test-InFramework $runtimeDirs $_.Name)) -and
                ($publishMode -or $ownedByShared -or -not (Test-Path (Join-Path $workerBin $_.Name)))
            }
            foreach ($dll in $dlls) {
                if (Add-LayerFile $placed $mismatches $dll.FullName $sharedDir $dll.Name $proj) { $copied++ }
            }
            # RID-specific builds (runtimes/<rid>/lib/<tfm>/*.dll, native SNI) follow the same rule.
            $runtimes = Join-Path $tmpPub "runtimes"
            if (Test-Path $runtimes) {
                foreach ($file in Get-ChildItem -Path $runtimes -Recurse -File) {
                    $relative = $file.FullName.Substring($tmpPub.Length).TrimStart('\', '/')
                    [void](Add-LayerFile $placed $mismatches $file.FullName $sharedDir $relative $proj)
                }
            }
        }
    }
    Assert-LayerConsistent $tfm $mismatches
    # Every project in the manifest must have produced its own DLL in the layer. Without this the
    # script can report success on a layer that cannot start a worker at all: SharedRuntimeBootstrap
    # byte-preloads the framework set from here and fail-fasts when one is missing. Caught on
    # 2026-09-09, when the Worker-bin filter silently removed all 14 framework assemblies and the
    # run still ended with "Done" and exit 0.
    $missing = @($projects | Where-Object {
        ($_ -notin $incompatible) -and -not (Test-Path (Join-Path $sharedDir "$_.dll"))
    })
    if ($missing.Count -gt 0) {
        Write-Host "MISSING from ${sharedDir}: $($missing -join ', ')" -ForegroundColor Red
        throw "Shared layer incomplete for $tfm - $($missing.Count) assembly(ies) did not land in the layer."
    }

    return @{ Copied = $copied; Incompatible = $incompatible; Conflicts = $mismatches.Count }
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
    $runtimeDirs = Get-RuntimeDirs $tfm
    Write-Host "Shared dir:    $sharedDir"
    Write-Host "Runtime dirs:  $($runtimeDirs -join '; ')"
    $summary[$tfm] = Build-Tfm $tfm $sharedDir $runtimeDirs
    Write-Host "  -> $($summary[$tfm].Copied) DLLs in $sharedDir" -ForegroundColor Cyan

    if (-not $publishMode) {
        # Deliver the layer where the worker actually reads it. SharedAssemblyLoader resolves
        # Libs/shared against AppContext.BaseDirectory - bin/<cfg>/<tfm> - not the project folder
        # this script writes, and the csproj brings it there with CopyToOutputDirectory="PreserveNewest".
        # That copy never deletes what has left the layer, and it skips a file whose source is OLDER:
        # this script keeps the NuGet file dates, so a newer package can carry an older timestamp and
        # never replace its predecessor. On 2026-09-17 the bin copy was therefore holding redb.* of two
        # older commits plus 22 ASP.NET framework assemblies the layer had stopped shipping, and the
        # worker was running on that. Mirroring here ends the "wipe bin, rebuild the worker" ritual:
        # after this script the worker can simply be started.
        $binShared = Join-Path (Join-Path $workerBin "Libs") "shared"
        if (Test-Path $workerBin) {
            if (Test-Path $binShared) {
                try { Remove-Item $binShared -Recurse -Force -ErrorAction Stop }
                catch { throw "Cannot refresh ${binShared}: $($_.Exception.Message). A running worker holds these files - stop it and rerun." }
            }
            New-Item -ItemType Directory -Path $binShared -Force | Out-Null
            Copy-Item (Join-Path $sharedDir '*') $binShared -Recurse -Force
            Write-Host "  -> mirrored into $binShared" -ForegroundColor Cyan
        } else {
            Write-Host "  worker bin not built yet ($workerBin) - the first Worker build copies the layer itself." -ForegroundColor DarkGray
        }
    }
    Write-Host ""
}

Write-Host "=== Summary ===" -ForegroundColor Cyan
$anyIncompatible = $false
foreach ($tfm in $Tfms) {
    $inc = $summary[$tfm].Incompatible
    if ($inc.Count -gt 0) {
        $anyIncompatible = $true
        Write-Host "$tfm incompatible: $($inc -join ', ')" -ForegroundColor Yellow
    } elseif ($summary[$tfm].Conflicts -gt 0) {
        Write-Host "$tfm OK ($($summary[$tfm].Copied) DLLs, $($summary[$tfm].Conflicts) version conflict(s) resolved by -TakeHighest — see above)" -ForegroundColor Yellow
    } else {
        Write-Host "$tfm OK ($($summary[$tfm].Copied) DLLs)" -ForegroundColor Green
    }
}

# In DEV mode a failing build already threw. In PUBLISH mode incompatibility is tolerated
# (matches the old build-shared-multitfm.ps1 behavior) — exit 0 so the pipeline continues.
Write-Host "Done." -ForegroundColor Green
