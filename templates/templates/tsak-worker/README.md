# TsakHost

Generated from `dotnet new tsak-worker`.

A minimal host process for running [redb.Route](https://www.nuget.org/packages/redb.Route.Core/) integration contexts inside a [redb.Tsak](https://www.nuget.org/packages/redb.Tsak.Core/) runtime container.

## What you get

- Hot-reload module loading from `Libs/`
- REST management API on `http://127.0.0.1:9090` (loopback: it is unauthenticated until `Tsak:Auth:Enabled`)
- Quartz scheduler (RAM or AdoJobStore depending on storage choice)
- OpenTelemetry / Prometheus metrics endpoint (off by default)
- Structured Serilog logging to console + rolling file in `Logs/`

## Run

```bash
dotnet run
```

The default provider is SQLite, so the host starts on a machine with nothing installed: it creates
`tsak.db` next to itself on the first run. `--db postgres|mssql` switches the provider (the connection
string sits in `ConnectionStrings`), and `--pro false` runs the Free redb tier: the host then uses the
`DeleteInsert` save strategy, because batch `ChangeTracking` is not implemented there.

The API key store, REDB connection and cluster settings live in [`appsettings.json`](./appsettings.json). Override via environment variables, user-secrets or a mounted `appsettings.Production.json`.

## Database and storage

`Tsak:Redb:Provider` is always configured, and the modules hosted on Tsak read their data through it:
a database is required even with `--storage inmemory`. InMemory only means that Tsak's own state store
and the Quartz job store live in RAM, so that state is lost on restart. To run without any database,
remove the whole `Tsak:Redb` section: the host then starts with no RedBase at all and a module that
asks for `IRedbService` fails to load.

## Add a module

Drop a `.tpkg` (or a folder with module DLLs) into the `Libs/` directory. The hot-reload service picks it up within `Tsak:HotReload:ScanIntervalSeconds`.

A module is loaded into its own assembly load context, and its dependencies are resolved from the
worker's own assembly set and from `Libs/shared` **next to the application**
(`AppContext.BaseDirectory/Libs/shared`, not the current directory - the template copies `Libs/**` to the
output at build, so a file in this project's `Libs/shared` ends up where the loader looks). The packages
produced by the redb.Templates module templates already carry every dependency the worker does not ship
(`redb.Route.File`, `redb.Route.GenericFile`, ...): fill `Libs/shared` only if a module still fails with
`FileNotFoundException`. The redb-tsak repository ships the tools for exactly that:

```powershell
# scripts/ of the redb-tsak checkout; point -SharedDir at the host you are running
scripts/refresh-shared.ps1 -Lib redb.Route.File -SharedDir C:\apps\worker\Libs\shared
scripts/build-shared.ps1 -Clean     # the whole connector layer at once
```

## Management API

The API is bound to `127.0.0.1:9090` and has no authentication by default. Before exposing it, set
`Tsak:Api:Host` and turn `Tsak:Auth` on with a long random `Secret`. On a Free provider the worker also
logs a warning about built-in retention routes that declare `.Cluster(true)` while no route policy
factory is registered: that is expected single-node behaviour, not a failure.

## Docker

```bash
docker build -t my-tsak-host .
docker run --rm -p 9090:9090 -v $(pwd)/Libs:/app/Libs my-tsak-host
```

## Documentation

- redb.Tsak: <https://github.com/redbase-app/redb-tsak>
- redb.Route: <https://github.com/redbase-app/redb-route>
- REDB: <https://redbase.app>
