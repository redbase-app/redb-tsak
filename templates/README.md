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
- REST management API on `http://0.0.0.0:9090`
- Serilog with console + rolling file sinks
- Hot-reload module loading from `Libs/`
- Quartz scheduler
- Optional cluster mode (set `Tsak:Cluster:Enabled = true`)
- Optional Prometheus exporter on `:9464`

## Parameters

| Parameter       | Default     | Description                                |
|-----------------|-------------|--------------------------------------------|
| `--db`          | `postgres`  | `postgres` or `mssql`                      |
| `--storage`     | `redb`      | `redb` (persistent) or `inmemory`          |
| `--pro`         | `false`     | Enable Pro features (cluster, EAV store)   |

## Documentation

- [redb.Tsak on NuGet](https://www.nuget.org/packages/redb.Tsak.Core)
- [redb.Route](https://www.nuget.org/packages/redb.Route)
- [redbase.app](https://redbase.app)

## License

Apache-2.0
