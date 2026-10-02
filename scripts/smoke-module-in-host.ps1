<#
.SYNOPSIS
    End-to-end check of the module chain: a module from redb-worker runs inside a tsak-worker host.

.DESCRIPTION
    The two template packs are separate packages, and a module only works when the host can resolve its
    dependencies. This script proves the chain instead of assuming it:

      1. packs both template packs (unless -TemplatesPackage / -TsakTemplatesPackage are given; outside
         the monorepo — in a checkout of the public redb-tsak repository — the module templates are
         installed from nuget.org instead),
      2. generates a RedbWorker module, packs it with its own deploy/pack-tpkg.ps1,
      3. generates the tsak-worker host (defaults: SQLite, Pro) and builds it,
      4. puts the assemblies the worker does not ship into the host's Libs/shared with the existing
         scripts/refresh-shared.ps1 (the whole layer, if you need it, is build-shared.ps1 -Clean),
      5. drops the module package into the host's Libs/,
      6. starts the host, feeds the module's sample order file and waits for the receipt.

    A receipt with <Status>Accepted</Status> means: the host started, the module was discovered,
    initialized and its file route ran a transacted write through RedBase.

.EXAMPLE
    ./scripts/smoke-module-in-host.ps1
    ./scripts/smoke-module-in-host.ps1 -TemplatesPackage ../nupkg/redb.Templates.4.2.1.nupkg
#>
param(
    [string] $TemplatesPackage = '',

    [string] $TsakTemplatesPackage = '',

    [string] $WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'redb-tsak-module-in-host'),

    # Skip filling Libs/shared: use it when the host already has the layer you want to test.
    [switch] $SkipSharedRefresh
)

$ErrorActionPreference = 'Stop'

# Build nodes keep output files locked between runs, which breaks the cleanup of the work directory.
$env:MSBUILDDISABLENODEREUSE = '1'

$scriptDir = $PSScriptRoot                                  # redb.Tsak/scripts
$tsakRoot  = Split-Path -Parent $scriptDir                  # redb.Tsak
$repoRoot  = Split-Path -Parent $tsakRoot                   # csharp/redb

function Pack-Template([string] $Project, [string] $Filter, [string] $Given) {
    if ($Given) { return (Resolve-Path $Given).Path }
    # Not a monorepo checkout (the public redb-tsak repository has no redb.Templates next to it): the
    # published pack is used instead, see below.
    if (-not (Test-Path $Project)) { return '' }
    dotnet pack $Project -c Release -o $packOutput -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack of $Project failed" }
    $packed = Get-ChildItem $packOutput -Filter $Filter |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $packed) { throw "no $Filter in $packOutput" }
    return $packed.FullName
}

function Stop-Hosts {
    # The apphost is TsakHost.exe; started as `dotnet TsakHost.dll` the process is dotnet instead.
    Get-Process -Name 'TsakHost' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*TsakHost.dll*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
}

function Wait-Api([int] $TimeoutSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try { return Invoke-WebRequest 'http://127.0.0.1:9090/api/contexts' -UseBasicParsing -TimeoutSec 3 }
        catch { }
        Start-Sleep -Milliseconds 500
    }
    throw 'the management API did not answer on http://127.0.0.1:9090'
}

function Wait-Receipt([string] $Outbox, [int] $TimeoutSeconds = 90) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $receipt = Get-ChildItem $Outbox -Filter '*.receipt.xml' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($receipt) { return $receipt }
        Start-Sleep -Milliseconds 500
    }
    throw "no receipt in $Outbox"
}

Stop-Hosts
# A leftover host or build node can still hold files in the previous work directory: retry, and give up
# with a clear message rather than deleting something half-way.
Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
for ($attempt = 0; $attempt -lt 3 -and (Test-Path $WorkDir); $attempt++) {
    Stop-Hosts
    Start-Sleep -Seconds 1
    Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
}
if (Test-Path $WorkDir) { throw "the previous work directory is locked: $WorkDir (stop the hosts and run again)" }
New-Item -ItemType Directory -Force $WorkDir | Out-Null

$packOutput = Join-Path $WorkDir 'pkg'
New-Item -ItemType Directory -Force $packOutput | Out-Null

# The module templates live next to redb.Tsak in the monorepo; a standalone checkout (the public
# redb-tsak repository) has no redb.Templates project, so the published pack is installed instead.
$templatesPack = Pack-Template (Join-Path $repoRoot 'redb.Templates/redb.Templates.csproj') 'redb.Templates.*.nupkg' $TemplatesPackage
$tsakPack      = Pack-Template (Join-Path $tsakRoot 'templates/redb.Tsak.Templates.csproj') 'redb.Tsak.Templates.*.nupkg' $TsakTemplatesPackage
if (-not $tsakPack) { throw 'the host template pack was not produced' }
Write-Host "module templates: $(if ($templatesPack) { $templatesPack } else { 'the published redb.Templates from nuget.org' })" -ForegroundColor DarkGray
Write-Host "host template:    $tsakPack" -ForegroundColor DarkGray

# Installing over an already installed pack of the same id registers a second copy, and `dotnet new`
# then fails with "Sequence contains more than one matching element".
foreach ($id in @('redb.Templates', 'redb.Tsak.Templates')) { dotnet new uninstall $id 2>&1 | Out-Null }
if ($templatesPack) { dotnet new install $templatesPack 2>&1 | Out-Null } else { dotnet new install redb.Templates 2>&1 | Out-Null }
dotnet new install $tsakPack 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet new install failed' }

$failed = @()
try {
    # ---------- 1. the module ----------
    $moduleDir = Join-Path $WorkDir 'module'
    Write-Host '=== module: dotnet new redb-worker + deploy/pack-tpkg.ps1 ===' -ForegroundColor Cyan
    dotnet new redb-worker -n SmokeModule -o $moduleDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet new redb-worker failed' }

    $templates = Join-Path $moduleDir 'deploy\pack-tpkg.ps1'
    & $templates
    if ($LASTEXITCODE -ne 0) { throw 'pack-tpkg.ps1 failed' }

    $package = Join-Path $moduleDir 'deploy\output\SmokeModule.tpkg'
    if (-not (Test-Path $package)) { throw 'the module package was not produced' }
    Write-Host ("     package: {0} bytes" -f (Get-Item $package).Length) -ForegroundColor DarkGray

    # ---------- 2. the host ----------
    $hostDir = Join-Path $WorkDir 'host'
    Write-Host '=== host: dotnet new tsak-worker + build ===' -ForegroundColor Cyan
    dotnet new tsak-worker -n TsakHost -o $hostDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet new tsak-worker failed' }
    dotnet build $hostDir -c Release -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'the host did not build' }

    # Two folders, two resolution rules (both checked against the runtime):
    #   Libs/*.tpkg       - the module scan resolves Tsak:Modules:AssemblyPaths against the WORKING DIRECTORY
    #   Libs/shared/*.dll - the shared layer is resolved against the APPLICATION directory (AppContext.BaseDirectory)
    # The template copies Libs/** to the output at build, so filling the project folder and mirroring it into
    # the output is exactly what a user does.
    $appDir = Join-Path $hostDir 'bin\Release\net10.0'
    $appLibs = Join-Path $appDir 'Libs'
    $libs = Join-Path $hostDir 'Libs'
    $shared = Join-Path $libs 'shared'
    New-Item -ItemType Directory -Force $shared, $appLibs | Out-Null
    Copy-Item $package $libs -Force

    # ---------- 3. the shared layer ----------
    if (-not $SkipSharedRefresh) {
        Write-Host '=== Libs/shared: scripts/refresh-shared.ps1 ===' -ForegroundColor Cyan
        # The worker image ships these in /app/Libs/shared; a host built here has to be given them.
        # (The whole connector layer at once: scripts/build-shared.ps1 -Clean.)
        foreach ($lib in @('redb.Route.File', 'redb.Route.GenericFile')) {
            & (Join-Path $scriptDir 'refresh-shared.ps1') -Lib $lib -SharedDir $shared -Configuration Release -Tfm net10.0
            if ($LASTEXITCODE -ne 0) { throw "refresh-shared.ps1 failed for $lib" }
        }
        Write-Host ("     shared: {0} assemblies" -f @(Get-ChildItem $shared -Filter *.dll).Count) -ForegroundColor DarkGray
    }

    # What the build's Libs/** copy does: the same files next to the application.
    Copy-Item (Join-Path $libs '*') $appLibs -Recurse -Force

    # ---------- 4. run the module in the host ----------
    Write-Host '=== host run: module discovers, initializes, processes a file ===' -ForegroundColor Cyan
    $stdout = Join-Path $WorkDir 'host.out.log'
    $stderr = Join-Path $WorkDir 'host.err.log'
    $process = Start-Process (Join-Path $appDir 'TsakHost.exe') -WorkingDirectory $hostDir -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        $answer = Wait-Api
        if ($answer.Content -notmatch 'Running') { throw 'no running context in the management API' }

        $inbox = Join-Path $hostDir 'data\inbox'
        New-Item -ItemType Directory -Force $inbox | Out-Null
        Copy-Item (Join-Path $moduleDir 'samples\order-1001.xml') (Join-Path $inbox 'order-1001.xml') -Force

        $outbox = Join-Path $hostDir 'data\outbox'
        $receipt = Wait-Receipt $outbox
        $text = Get-Content $receipt.FullName -Raw
        if ($text -notmatch '<Status>Accepted</Status>') {
            throw "the receipt says: $($text.Trim())"
        }
        Write-Host "     receipt: $($receipt.Name) -> <Status>Accepted</Status>" -ForegroundColor DarkGray

        $log = (Get-Content $stderr -Raw -ErrorAction SilentlyContinue) + (Get-Content $stdout -Raw -ErrorAction SilentlyContinue)
        if ($log -notmatch 'Initialized module') { throw 'the module was not initialized' }
        Write-Host 'OK   module-in-host' -ForegroundColor Green
    }
    catch {
        Write-Host "FAIL module-in-host : $($_.Exception.Message)" -ForegroundColor Red
        $failed += 'module-in-host'
        Write-Host '--- host stdout tail ---' -ForegroundColor DarkGray
        Get-Content $stdout -Tail 25 -ErrorAction SilentlyContinue | Out-String | Write-Host
        Write-Host '--- host stderr tail ---' -ForegroundColor DarkGray
        Get-Content $stderr -Tail 25 -ErrorAction SilentlyContinue | Out-String | Write-Host
    }
    finally { Stop-Hosts }
}
finally {
    Stop-Hosts
}

if ($failed.Count -gt 0) { throw "Failed: $($failed -join ', ')" }

