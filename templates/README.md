# redb.Tsak Templates

Project templates for [redb.Tsak](https://www.nuget.org/packages/redb.Tsak.Core) —
runtime container for `redb.Route` contexts.

## Install

```bash
dotnet new install redb.Tsak.Templates
```

## Available templates

| Short name      | Description                                                  |
|-----------------|--------------------------------------------------------------|
| `tsak-worker`   | Worker Service host with hot-reload, REST API, scheduler     |

## Quick start

```bash
dotnet new tsak-worker -n MyTsakHost
cd MyTsakHost
dotnet run
```

Generated host:

- Uses `services.AddTsak(configuration)` from `redb.Tsak.Core`
- REST management API on `http://127.0.0.1:9090` (loopback: it is unauthenticated until `Tsak:Auth:Enabled`)
- Serilog with console + rolling file sinks
- Hot-reload module loading from `Libs/`
- Quartz scheduler
- Optional cluster mode (set `Tsak:Cluster:Enabled = true`)
- Optional Prometheus exporter on `:9464` (off by default)

The default provider is SQLite, so the host starts on a machine with nothing installed: it creates
`tsak.db` next to itself on the first run.

## Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `--db` | `sqlite` | `sqlite`, `postgres` or `mssql`. A database is always configured, and modules read their data through it |
| `--storage` | `redb` | `redb` (persistent state) or `inmemory` (Tsak's own state and the Quartz job store in RAM; a database is still required) |
| `--pro` | `true` | `true` is the Pro tier (free for the whole 4.x line); `false` switches the redb provider to Free, where the save strategy becomes `DeleteInsert`, because batch `ChangeTracking` is not implemented there |

## Modules

Drop a `.tpkg` (or a folder with module DLLs) into `Libs/`: the hot-reload service picks it up within
`Tsak:HotReload:ScanIntervalSeconds`. The packages come from the `redb.Templates` module templates
(`redb-worker`, `redb-chat`, `redb-app`, `redb-bff`), each of which packs itself with its own
`deploy/pack-tpkg.ps1`.

## Documentation

- [redb.Tsak on NuGet](https://www.nuget.org/packages/redb.Tsak.Core)
- [redb.Route](https://www.nuget.org/packages/redb.Route)
- [redbase.app](https://redbase.app)

## License

Apache-2.0
