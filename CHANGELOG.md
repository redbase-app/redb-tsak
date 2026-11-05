# Changelog — redb.Tsak

All notable changes to redb.Tsak are documented in this file.  
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).  
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## Projects

| Project | Description |
|---------|-------------|
| `redb.Tsak.Core` | Kernel: contracts, services, security, cluster, hot-reload, monitoring |
| `redb.Tsak.Core.Pro` | Pro features: redb-backed key store, cluster topology |
| `redb.Tsak.Worker` | Hosted process: DI wiring, Serilog, Quartz, Dockerfile |
| `redb.Tsak.Contracts` | Shared wire DTOs |
| `redb.Tsak.Client` | `ITsakApiClient` / `TsakApiClient` — HTTP client |
| `redb.Tsak.CLI` | `tsak` command-line tool — 30 commands |
| `redb.Tsak.Web` | Blazor Server dashboard — 10 pages |
| `redb.Tsak.Web.Pro` | Pro web extensions |
| `redb.Tsak.Templates` | `dotnet new` template pack — the `tsak-worker` host |

---

> **Note on version history:** redb.Tsak has been running in production since version 1.0.0.
> Versions 1.0.0 – 1.0.3 were not published to NuGet (internal deployments only).
> The first public NuGet release is **1.0.4**.

---

## [Unreleased]

Pending changes live in `changelog/unreleased/` until the release; `pwsh scripts/changelog.ps1 -Preview` shows them.

## [4.6.2] — 2026-11-05

### Changed

- **Built on redb 4.6.2 and redb.Route 4.6.2.** No change in Tsak itself: the worker, its images and archives move to
  4.6.2 with the rest of the ecosystem. Nothing to do on your side.

## [4.6.1] — 2026-11-04

### Changed

- **Built on redb 4.6.1 and redb.Route 4.6.1.** The worker, its images and archives carry the SQL Server integer
  average fix of redb and the AS4 fixes of redb.Route (issuer-serial key references, malformed base64, the receipt of
  a duplicate, the refusal codes the eDelivery tests expect). No change in Tsak itself; nothing to do on your side.

## [4.6.0] — 2026-11-03

### Added

- **A pipeline can ask a node how to deploy a package.** `GET /api/health/deploy` answers `200` `hot-swap` (copy it into
  `modules/`) or `409` `cold-deploy` with reason codes (stop, replace the packages, start); `/api/health/deploy/cluster`
  asks every node, and `tsak system deploy-advice [--cluster]` exits `0` or `3`. No API key needed, like the probes.
  DEPLOYMENT.md, "Deploy advice in a pipeline", has the steps for Kubernetes and for compose with TeamCity. Packages: `redb.Tsak.Core`, `redb.Tsak.Core.Pro`, `redb.Tsak.Contracts`, `redb.Tsak.Client`, `redb.Tsak.CLI`.
- **A node shows how many old module versions stay in memory after hot swaps.** `GET /api/system/info`, the
  diagnostic dump, the dashboard node page and `tsak system info` show `ModuleContextsAlive` and `OrphanedModuleContexts`.
  The new `hotreload` health check turns Degraded above `Tsak:HotReload:MaxOrphanedContexts` (default `KeepVersions + 3`)
  and says to restart the node; DEPLOYMENT.md, "After a series of hot swaps", has the procedure. Packages: `redb.Tsak.Core`, `redb.Tsak.Contracts`, `redb.Tsak.CLI`.

### Changed

- **With `Tsak:HotReload:Collectible=true` Tsak checks that a replaced module version really leaves memory.** One still
  alive after the check is logged as a warning (`module context ... still alive after 10 collections`) and counted.
  `HotReloadService.LeakedAlcCount` now counts the old versions still in memory in both modes, the same number as
  `OrphanedModuleContexts`; it used to count only swaps with `Collectible=false`. The default stays `false`. The cluster
  record `_tsak_metrics` gains both counts; a node of an older version does not write them, its record reads 0. Packages: `redb.Tsak.Core`.

### Fixed

- **A hot swap no longer keeps old module versions in memory that it should release.** The first swap of a package
  loaded at startup kept its old version, with `Collectible=true` as well, and each reload of a package with companion
  DLLs kept the throwaway context of its pre-load check, in both modes. Nothing to change on your side. Packages: `redb.Tsak.Core`.

## Earlier versions

Earlier releases are archived by line in [changelog/](changelog/): [4.5.x](changelog/CHANGELOG-4.5.x.md), [4.4.x](changelog/CHANGELOG-4.4.x.md), [4.3.x](changelog/CHANGELOG-4.3.x.md), [4.2.x](changelog/CHANGELOG-4.2.x.md), [4.1.x](changelog/CHANGELOG-4.1.x.md), [4.0.x](changelog/CHANGELOG-4.0.x.md), [3.x](changelog/CHANGELOG-3.x.md), [2.x](changelog/CHANGELOG-2.x.md), [1.x](changelog/CHANGELOG-1.x.md).
The worker's development log of March 2026, before the versioned releases, is
[changelog/worker-devlog-2026-03.md](changelog/worker-devlog-2026-03.md).
