# redb.Tsak changelog: 4.6.x

Releases of the 4.6 line, moved here unchanged from CHANGELOG.md, which keeps the latest released line only.
Newer releases: [CHANGELOG.md](../CHANGELOG.md).

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
