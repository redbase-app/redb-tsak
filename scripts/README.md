# scripts/

Utility scripts for working with `redb.Tsak` from source.

## `build-shared.ps1` — populate `Libs/shared/`

`redb.Tsak.Worker` uses a `SharedAssemblyLoader` that scans
`src/redb.Tsak.Worker/Libs/shared/` at start-up and loads every assembly it
finds into the **default** `AssemblyLoadContext`. This is how transport
drivers (RabbitMQ, Kafka, Elasticsearch, ...) become visible to every
hot-loaded module without each module shipping its own copy of the driver.

Without this layer:

- every `.tpkg` module would have to ship its own `RabbitMQ.Client.dll`,
  `Confluent.Kafka.dll`, etc.;
- per-module `AssemblyLoadContext` isolation would fragment connection
  pools, native handles (librdkafka) and static state.

`build-shared.ps1` automates the staging step: it runs `dotnet publish`
on each `redb.Route.*` connector project, then copies the resulting
DLLs (and any native `runtimes/` payloads) into `Libs/shared/`, skipping
anything that is already in the Worker output or that belongs to the
.NET shared framework.

### Prerequisites

- A local checkout of [redb-route](https://github.com/redbase-app/redb-route).
- .NET 9 SDK (`dotnet --version` ≥ `9.0.x`).

### Usage

```powershell
# 1. Clone the Route repo next to redb-tsak (the default path)
#    parent/
#      ├── redb-tsak/
#      └── redb-route/
git clone https://github.com/redbase-app/redb-route.git ../redb-route

# 2. Build & stage all stock connectors
./scripts/build-shared.ps1

# 3. Or pick a specific subset
./scripts/build-shared.ps1 -Connectors redb.Route.RabbitMQ, redb.Route.Kafka

# 4. Custom Route checkout location
./scripts/build-shared.ps1 -RouteSrc D:\src\redb-route\src -Configuration Release

# 5. Wipe the layer first
./scripts/build-shared.ps1 -Clean
```

### Adding your own connectors

Anything that exposes `IRouteEndpointFactory` / `IRouteProcessorFactory`
qualifies as a shared assembly. Pass its project name through
`-Connectors` (the script accepts arbitrary names as long as the
`<name>/<name>.csproj` layout matches the one used by `redb.Route.*`).

For connectors hosted in a different layout, run `dotnet publish`
yourself and drop the resulting DLLs into `src/redb.Tsak.Worker/Libs/shared/`.
