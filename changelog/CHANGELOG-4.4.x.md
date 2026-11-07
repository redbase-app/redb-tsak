# redb.Tsak changelog: 4.4.x

Releases of the 4.4 line, moved here unchanged from CHANGELOG.md, which keeps the latest released line only.
Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [4.4.2] — 2026-10-23

### Added

- **The `redb` health check**: `/api/system/health` reports `Degraded` while a redb database of the worker (the host's
  own or a named instance of a context) has schema problems that need a person, with the problem and a link to redb's
  UPGRADING.md in the new `details` field; the dashboard's node page and `tsak system health` show it. Readiness stays
  `200` on Degraded unless `Tsak:Health:DegradedNotReady=true`. Packages: `redb.Tsak.Core`, `redb.Tsak.Contracts`, `redb.Tsak.CLI`, `redb.Tsak.Web`.

### Changed

- **`tsak module keygen` needs `--out` and never replaces a key by itself.** Without `--out` it wrote a new private key
  as `tsak-module.key` into the current directory, over one already there. It now refuses without `--out`, refuses to
  overwrite an existing pair unless `--force` is given, and prints the full paths it wrote. A script that called it
  without `--out` passes `--out tsak-module` to keep the old file names. Packages: `redb.Tsak.CLI`.

### Fixed

- **The CLI names the arguments left out.** A command with two required arguments (`route get <context> <routeId>` and
  the like) printed "Required argument missing for command: 'get'." twice; it now prints one line per missing argument
  with its name, then the help of the command as before. Other parse errors keep their "Did you mean" suggestions. Packages: `redb.Tsak.CLI`.

## [4.4.1] — 2026-10-21

### Fixed

- **A context with a `seda:` hop works again after a restart** (redb-route issue #12). From 4.2.2 to 4.4.0, after `/stop` then `/start`,
  `/restart` or the watchdog's automatic restart, the context reported its routes Started, but nothing behind a seda hop
  was processed (`ChannelClosedException` on every send). The fix is in redb.Route 4.4.1, which this release uses. On an
  older version, recreate the context instead of restarting it: stop it and touch its `.tpkg`. Packages: `redb.Tsak.Core`.

## [4.4.0] — 2026-10-21

> **Withdrawn on the day of release because of the seda restart bug in redb.Route; use 4.4.1.**

### Breaking

- **A second live worker with the same node id is refused at start.** A record of the id that keeps its heartbeat
  stops the start; a previous process of the node is waited out (`DeadNodeTimeoutSeconds`). Give each worker its own id. Packages: `redb.Tsak.Core.Pro`. Upgrade: [UPGRADING.md](../UPGRADING.md#node-id-and-quartz-instance-id).

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
  OpenTelemetry 1.20, and everything else to its latest stable version. Packages: `redb.Tsak.Core`. Upgrade: [UPGRADING.md](../UPGRADING.md#quartz-tables).
- **A module's redb schemes are in the cache before it starts**: the host reads the schemes of the module's
  `[RedbScheme]` types, so the first query no longer reads them on a blocked thread with a Warning. Nothing is synced. Packages: `redb.Tsak.Core`.
- **The type names of a module's routes resolve to the module's own classes first** (`bean:Type`, XML `<bean type>`,
  SQL `outputClass`), then the process as before. It changes only where two loaded classes share a name. Packages: `redb.Tsak.Core`.
- **The worker adds the Quartz 3.17 to 3.19 columns to a clustered job store at start**, under a lock, so the
  "column not found" warnings stop. A role that may not alter the tables gets a warning and the administrator's script. Packages: `redb.Tsak.Core`. Upgrade: [UPGRADING.md](../UPGRADING.md#quartz-tables).
- **`quartz.scheduler.instanceId = NODE`, the shipped value, gives every context scheduler the node id.** It stays
  across restarts, so a crashed node takes its own fired triggers back at its first check-in. `AUTO` and explicit ids
  keep their meaning. Packages: `redb.Tsak.Core`. Upgrade: [UPGRADING.md](../UPGRADING.md#node-id-and-quartz-instance-id).
- **Contexts start in the order the packages declare.** At start of the worker the `Dependencies` of a `.tpkg` manifest
  and `ITsakModule.Dependencies` order the contexts, besides `Tsak:Contexts:{name}:Dependencies`: the contexts that run
  modules of a named package or module start first, as a hot reload already ordered packages. Packages: `redb.Tsak.Core`.
