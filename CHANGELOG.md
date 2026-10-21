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

## [4.4.1] — 2026-10-21

### Fixed

- **A context with a `seda:` hop works again after a restart.** From 4.2.2 to 4.4.0, after `/stop` then `/start`,
  `/restart` or the watchdog's automatic restart, the context reported its routes Started, but nothing behind a seda hop
  was processed (`ChannelClosedException` on every send). The fix is in redb.Route 4.4.1, which this release uses. On an
  older version, recreate the context instead of restarting it: stop it and touch its `.tpkg`. Packages: `redb.Tsak.Core`.

## [4.4.0] — 2026-10-21

> **Withdrawn on the day of release because of the seda restart bug in redb.Route; use 4.4.1.**

### Breaking

- **A second live worker with the same node id is refused at start.** A record of the id that keeps its heartbeat
  stops the start; a previous process of the node is waited out (`DeadNodeTimeoutSeconds`). Give each worker its own id. Packages: `redb.Tsak.Core.Pro`. Upgrade: [UPGRADING.md](UPGRADING.md#node-id-and-quartz-instance-id).

### Added

- **A module subscribes redb save interceptors through its context**:
  `context.GetService<ITsakSaveInterceptors>()?.Add(interceptor)` in `InitializeAsync`. Removing the context removes
  them. The README, "A module's save interceptors", has the details. Packages: `redb.Tsak.Core`, `redb.Tsak.Contracts`.

### Changed

- **`tsak` CLI on System.CommandLine 2.0.13.** Commands, options, defaults and exit codes are unchanged; help and
  errors are in English whatever the language of the operating system. Packages: `redb.Tsak.CLI`.
- **`HotReload:Collectible=true` releases an unloaded module**: its resolvers and save interceptors go with its
  context, and the caches of redb and redb.Route let its types go. The option stays `false` by default. Packages: `redb.Tsak.Core`.
- **Quartz 3.22** for the scheduler of every context, with its corrections to when a job fires; the trigger of a
  running non-concurrent job reads `Blocked`, not `Complete`. Serilog.AspNetCore 10, Serilog.Sinks.File 7,
  OpenTelemetry 1.20, and everything else to its latest stable version. Packages: `redb.Tsak.Core`. Upgrade: [UPGRADING.md](UPGRADING.md#quartz-tables).
- **A module's redb schemes are in the cache before it starts**: the host reads the schemes of the module's
  `[RedbScheme]` types, so the first query no longer reads them on a blocked thread with a Warning. Nothing is synced. Packages: `redb.Tsak.Core`.
- **The type names of a module's routes resolve to the module's own classes first** (`bean:Type`, XML `<bean type>`,
  SQL `outputClass`), then the process as before. It changes only where two loaded classes share a name. Packages: `redb.Tsak.Core`.
- **The worker adds the Quartz 3.17 to 3.19 columns to a clustered job store at start**, under a lock, so the
  "column not found" warnings stop. A role that may not alter the tables gets a warning and the administrator's script. Packages: `redb.Tsak.Core`. Upgrade: [UPGRADING.md](UPGRADING.md#quartz-tables).
- **`quartz.scheduler.instanceId = NODE`, the shipped value, gives every context scheduler the node id.** It stays
  across restarts, so a crashed node takes its own fired triggers back at its first check-in. `AUTO` and explicit ids
  keep their meaning. Packages: `redb.Tsak.Core`. Upgrade: [UPGRADING.md](UPGRADING.md#node-id-and-quartz-instance-id).
- **Contexts start in the order the packages declare.** At start of the worker the `Dependencies` of a `.tpkg` manifest
  and `ITsakModule.Dependencies` order the contexts, besides `Tsak:Contexts:{name}:Dependencies`: the contexts that run
  modules of a named package or module start first, as a hot reload already ordered packages. Packages: `redb.Tsak.Core`.

## Earlier versions

Earlier releases are archived by line in [changelog/](changelog/): [4.3.x](changelog/CHANGELOG-4.3.x.md), [4.2.x](changelog/CHANGELOG-4.2.x.md), [4.1.x](changelog/CHANGELOG-4.1.x.md), [4.0.x](changelog/CHANGELOG-4.0.x.md), [3.x](changelog/CHANGELOG-3.x.md), [2.x](changelog/CHANGELOG-2.x.md), [1.x](changelog/CHANGELOG-1.x.md).
The worker's development log of March 2026, before the versioned releases, is
[changelog/worker-devlog-2026-03.md](changelog/worker-devlog-2026-03.md).
