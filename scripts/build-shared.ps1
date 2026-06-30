#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds redb.Route connector projects and stages their DLLs into
    src/redb.Tsak.Worker/Libs/shared/ — the shared assembly layer that
    SharedAssemblyLoader reads at Worker start-up.

.DESCRIPTION
    For each connector listed in $connectors this script runs
    `dotnet publish` to resolve all transitive dependencies (including
    NuGet packages and native runtimes such as librdkafka), then copies
    the resulting DLLs into Libs/shared/, skipping any assembly that:

      - is already present in the Worker output (avoids version conflicts)
      - belongs to the .NET shared framework (system assemblies must not
        be overridden inside an AssemblyLoadContext)

    Any native libraries under runtimes/ (e.g. librdkafka) are copied
    verbatim so platform-specific binaries are preserved.

.PARAMETER RouteSrc
    Path to a checkout of https://github.com/redbase-app/redb-route
    pointing at its src/ directory. Default: ..\redb-route\src
    (assumes redb-route is cloned next to redb-tsak).

.PARAMETER Configuration
    Build configuration (Debug/Release). Default: Debug.

.PARAMETER Connectors
    Override the list of Route connector projects to build.
    Default: all stock connectors (RabbitMQ, Kafka, Sql, ...).

.PARAMETER Clean
    Remove the existing Libs/shared/ directory before staging.

.EXAMPLE
    ./scripts/build-shared.ps1
    ./scripts/build-shared.ps1 -RouteSrc D:\src\redb-route\src -Configuration Release
    ./scripts/build-shared.ps1 -Connectors redb.Route.RabbitMQ,redb.Route.Kafka
#>
param(
    [string]$RouteSrc = "",
    [string]$Configuration = "Debug",
    [string[]]$Connectors = @(),
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Repository root = parent of scripts/
$repoRoot = Split-Path -Parent $PSScriptRoot

# Resolve Route source directory
if (-not $RouteSrc) {
    $RouteSrc = Join-Path (Split-Path $repoRoot -Parent) "redb-route\src"
}
if (-not (Test-Path $RouteSrc)) {
    Write-Host "ERROR: Route source directory not found: $RouteSrc" -ForegroundColor Red
    Write-Host "Clone https://github.com/redbase-app/redb-route next to redb-tsak," -ForegroundColor Yellow
    Write-Host "or pass -RouteSrc <path-to-redb-route\src>." -ForegroundColor Yellow
    exit 1
}

if ($Connectors.Count -eq 0) {
    $Connectors = @(
        "redb.Route.RabbitMQ"
        "redb.Route.Amqp"
        "redb.Route.AzureServiceBus"
        "redb.Route.Controllers"
        "redb.Route.Elasticsearch"
        "redb.Route.Grpc"
        "redb.Route.Kafka"
        "redb.Route.Sql"
        "redb.Route.File"
        "redb.Route.Ftp"
        "redb.Route.GenericFile"
        "redb.Route.Redis"
        "redb.Route.S3"
        "redb.Route.SignalR"
        "redb.Route.Tcp"
        "redb.Route.WebSocket"
        "redb.Route.MqttNet"
        "redb.Route.Mail"
        "redb.Route.Sftp"
        "redb.Route.IbmMq"
    )
}

$tfm        = "net9.0"
$tfmVersion = $tfm -replace '^net',''
$workerProj = Join-Path $repoRoot "src\redb.Tsak.Worker\redb.Tsak.Worker.csproj"
$workerBin  = Join-Path $repoRoot "src\redb.Tsak.Worker\bin\$Configuration\$tfm"
$sharedDir  = Join-Path $repoRoot "src\redb.Tsak.Worker\Libs\shared"

# Locate the actual .NET shared framework so we never override system DLLs
$dotnetRoot = Split-Path (Get-Command dotnet).Source
$runtimeDir = Get-ChildItem (Join-Path $dotnetRoot "shared\Microsoft.NETCore.App") -Directory |
    Where-Object { $_.Name -match "^$([regex]::Escape($tfmVersion))\." } |
    Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $runtimeDir) {
    Write-Host "WARNING: .NET $tfmVersion runtime directory not found; framework filter disabled" -ForegroundColor Yellow
    $runtimeDir = "__nonexistent__"
}

Write-Host "=== build-shared.ps1 ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration"
Write-Host "Route src    : $RouteSrc"
Write-Host "Runtime dir  : $runtimeDir"
Write-Host "Shared dir   : $sharedDir"
Write-Host ""

if ($Clean -and (Test-Path $sharedDir)) {
    Write-Host "Cleaning $sharedDir..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $sharedDir
}
if (-not (Test-Path $sharedDir)) {
    New-Item -ItemType Directory -Path $sharedDir -Force | Out-Null
}

$failed = @()
$copied = 0

foreach ($connector in $Connectors) {
    $projPath = Join-Path $RouteSrc "$connector\$connector.csproj"
    if (-not (Test-Path $projPath)) {
        Write-Host "  SKIP $connector (project not found at $projPath)" -ForegroundColor Yellow
        continue
    }

    $publishDir = Join-Path (Join-Path $env:TEMP "tsak_shared_publish") $connector
    Write-Host "  Building $connector..." -NoNewline
    dotnet publish $projPath -c $Configuration -f $tfm --nologo -v q -o $publishDir | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host " FAILED" -ForegroundColor Red
        $failed += $connector
        continue
    }
    Write-Host " OK" -ForegroundColor Green

    $dlls = Get-ChildItem -Path $publishDir -Filter "*.dll" | Where-Object {
        -not (Test-Path (Join-Path $workerBin $_.Name)) -and
        -not (Test-Path (Join-Path $runtimeDir $_.Name))
    }
    foreach ($dll in $dlls) {
        Copy-Item $dll.FullName -Destination $sharedDir -Force
        $copied++
    }

    $runtimesDir = Join-Path $publishDir "runtimes"
    if (Test-Path $runtimesDir) {
        Copy-Item -Path $runtimesDir -Destination $sharedDir -Recurse -Force
        Write-Host "    + runtimes/ copied" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host "Copied $copied DLLs to $sharedDir"
if ($failed.Count -gt 0) {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "All connectors built successfully" -ForegroundColor Green
