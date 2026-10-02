<#
.SYNOPSIS
    Generates a host from the tsak-worker template, one case per option that changes the configuration,
    builds it and starts it.

.DESCRIPTION
    Packs the template pack (or takes -PackagePath), installs it, runs `dotnet new tsak-worker` for every
    case, builds the host and, for the cases that run without a server, starts it against SQLite and
    checks what the host says about itself: the provider and tier from the log, the save strategy the
    runtime applied, and that the database file appeared. The postgres/mssql cases are built only: their
    providers need a server.

    Why these checks: the template's defaults once disagreed with the runtime's (Free tier with batch
    ChangeTracking, Postgres with nothing installed) and every release shipped that.

.EXAMPLE
    ./scripts/smoke-template.ps1
    ./scripts/smoke-template.ps1 -PackagePath templates/bin/Release/redb.Tsak.Templates.4.2.1.nupkg
#>
param(
    [string] $PackagePath = '',

    [string] $WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'redb-tsak-template-smoke')
)

$ErrorActionPreference = 'Stop'

# Build nodes keep output files locked between runs, which breaks the cleanup of the work directory.
$env:MSBUILDDISABLENODEREUSE = '1'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..')

if (-not $PackagePath) {
    dotnet pack (Join-Path $repo 'templates/redb.Tsak.Templates.csproj') -c Release -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed' }
    $PackagePath = (Get-ChildItem (Join-Path $repo 'templates/bin/Release') -Filter 'redb.Tsak.Templates.*.nupkg' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    if (-not $PackagePath) { throw 'dotnet pack produced no nupkg' }
}
$PackagePath = (Resolve-Path $PackagePath).Path

# Run = $true: the host starts on SQLite and must report the provider, tier and strategy below.
$cases = @(
    @{ Id = 'default';  Args = @();                 Run = $true;  Provider = 'sqlite'; Tier = 'Pro';  Strategy = 'ChangeTracking' }
    @{ Id = 'inmemory'; Args = @('-s', 'inmemory'); Run = $true;  Provider = 'sqlite'; Tier = 'Pro';  Strategy = 'ChangeTracking' }
    @{ Id = 'free';     Args = @('--pro', 'false'); Run = $true;  Provider = 'sqlite'; Tier = 'Free'; Strategy = 'DeleteInsert' }
    @{ Id = 'postgres'; Args = @('-db', 'postgres') }
    @{ Id = 'mssql';    Args = @('-db', 'mssql') }
)

function Stop-Hosts {
    # `dotnet run` starts the apphost, not dotnet itself, so kill that name too.
    Get-Process -Name 'TsakHost' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
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

Stop-Hosts
# A leftover host or build node can hold files in the previous work directory: retry, then say so.
Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
for ($attempt = 0; $attempt -lt 3 -and (Test-Path $WorkDir); $attempt++) {
    Stop-Hosts
    Start-Sleep -Seconds 1
    Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
}
if (Test-Path $WorkDir) { throw "the previous work directory is locked: $WorkDir (stop the hosts and run again)" }
New-Item -ItemType Directory -Force $WorkDir | Out-Null

# Installing over an already installed pack of the same id registers a second copy, and `dotnet new`
# then fails with "Sequence contains more than one matching element".
Stop-Hosts
dotnet new uninstall redb.Tsak.Templates 2>&1 | Out-Null
dotnet new install $PackagePath 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet new install failed' }

$failed = @()
foreach ($case in $cases) {
    $id = $case.Id
    $out = Join-Path $WorkDir $id
    Write-Host "=== $id ===" -ForegroundColor Cyan
    try {
        $newArgs = @('tsak-worker', '-n', 'TsakHost', '-o', $out) + $case.Args
        dotnet new @newArgs
        if ($LASTEXITCODE -ne 0) { throw 'dotnet new tsak-worker failed' }

        dotnet build $out -c Release -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'the host did not build' }

        $settings = Get-Content (Join-Path $out 'appsettings.json') -Raw
        $expected = if ($case.Provider) { $case.Provider } else { $id }
        $found = ([regex]::Match($settings, '"Provider":\s*"([a-z]+)"')).Groups[1].Value
        if ($found -ne $expected) { throw "appsettings.json has provider '$found', expected '$expected'" }

        if (-not $case.Run) {
            Write-Host "     provider: $found (built only: the provider needs a server to run)" -ForegroundColor DarkGray
        }
        else {
            $stdout = Join-Path $out 'smoke.out.log'
            $stderr = Join-Path $out 'smoke.err.log'
            $process = Start-Process dotnet -ArgumentList @('run', '-c', 'Release', '--no-build', '--project', $out) -PassThru `
                -RedirectStandardOutput $stdout -RedirectStandardError $stderr
            try {
                $answer = Wait-Api
                if ($answer.StatusCode -ne 200 -or $answer.Content -notmatch '_system') {
                    throw 'the system context is not running'
                }

                $log = (Get-Content $stderr -Raw -ErrorAction SilentlyContinue) + (Get-Content $stdout -Raw -ErrorAction SilentlyContinue)
                if ($log -notmatch "provider=$($case.Provider), tier=$($case.Tier)") {
                    throw "the log does not say 'provider=$($case.Provider), tier=$($case.Tier)'"
                }
                if ($log -notmatch "applied PropsSaveStrategy=$($case.Strategy)") {
                    throw "the runtime did not apply 'PropsSaveStrategy=$($case.Strategy)'"
                }
                if (-not (Test-Path (Join-Path $out 'tsak.db'))) { throw 'tsak.db was not created' }

                Write-Host "     provider=$($case.Provider), tier=$($case.Tier), strategy=$($case.Strategy)" -ForegroundColor DarkGray
            }
            finally { Stop-Hosts }
        }

        Write-Host "OK   $id" -ForegroundColor Green
    }
    catch {
        Stop-Hosts
        Write-Host "FAIL $id : $($_.Exception.Message)" -ForegroundColor Red
        $failed += $id
    }
}

if ($failed.Count -gt 0) { throw "Failed cases: $($failed -join ', ')" }
Write-Host 'All cases passed.' -ForegroundColor Green
