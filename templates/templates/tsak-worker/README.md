# TsakHost

Generated from `dotnet new tsak-worker`.

A minimal host process for running [redb.Route](https://www.nuget.org/packages/redb.Route.Core/) integration contexts inside a [redb.Tsak](https://www.nuget.org/packages/redb.Tsak.Core/) runtime container.

## What you get

- Hot-reload module loading from `Libs/`
- REST management API on `http://0.0.0.0:9090`
- Quartz scheduler (RAM or AdoJobStore depending on storage choice)
- OpenTelemetry / Prometheus metrics endpoint (off by default)
- Structured Serilog logging to console + rolling file in `Logs/`

## Run

```bash
dotnet run
```

The API key store, REDB connection and cluster settings live in [`appsettings.json`](./appsettings.json). Override via environment variables, user-secrets or a mounted `appsettings.Production.json`.

## Add a module

Drop a `.tpkg` (or a folder with module DLLs) into the `Libs/` directory. The hot-reload service picks it up within `Tsak:HotReload:ScanIntervalSeconds`.

## Docker

```bash
docker build -t my-tsak-host .
docker run --rm -p 9090:9090 -v $(pwd)/Libs:/app/Libs my-tsak-host
```

## Documentation

- redb.Tsak: <https://github.com/redbase-app/redb-tsak>
- redb.Route: <https://github.com/redbase-app/redb-route>
- REDB: <https://redbase.app>
