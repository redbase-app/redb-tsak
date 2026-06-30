# redb.Tsak.Worker

.NET Worker Service that hosts and orchestrates **redb.Tsak** modules — route-based integration pipelines built on top of [redb.Route](https://github.com/redberriespro/redb.Route).

The worker manages module lifecycle, execution contexts, hot-reload, health monitoring, and exposes a REST API for management by the [Web dashboard](../redb.Tsak.Web/README.md) or external tooling.

## Quick Start

```bash
dotnet run --project src/redb.Tsak.Worker
```

Default API endpoint: `http://0.0.0.0:9090`

---

## Configuration Reference

All settings are in `appsettings.json`. Sections are listed below with their actual defaults from code.

### `Tsak:Storage` — Storage Backend

Controls where module definitions and runtime state are persisted.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Type` | `string` | `"InMemory"` | `"InMemory"` — volatile, config-only. `"Redb"` — persistent via EAV database. |

- **InMemory**: API keys are read-only from `Tsak:Auth:Keys` config. Module and state data is lost on restart.
- **Redb**: Requires `ConnectionStrings` and `Tsak:Redb` to be configured. API keys are seeded from config and can be managed at runtime.

### `Tsak:Redb` — Database Configuration

Required when `Storage:Type = "Redb"` or `Cluster:Enabled = true`.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Provider` | `string` | — | `"postgres"` or `"mssql"`. |
| `UsePro` | `bool` | `false` | Enable redb.Core.Pro features (EAV change tracking, clustering). |
| `License` | `string \| string[]` | — | License key(s) for redb.Core.Pro / Tsak.Cluster. Required when `UsePro = true` (or for cluster). Accepts a single JWT, a `\|`-separated string, or a JSON array (see below). |
| `PropsSaveStrategy` | `string` | `"DeleteInsert"` | EAV write strategy: `"DeleteInsert"` or `"ChangeTracking"`. |

Connection string is selected automatically based on `Provider`:
- `ConnectionStrings:Postgres` for `"postgres"`
- `ConnectionStrings:MSSql` for `"mssql"`

#### License — multiple keys

A single license JWT covers a fixed feature set (e.g. `core.pro` + `postgres.pro`). To combine
several licenses (e.g. add `tsak.cluster` on top of `core.pro`), use any of:

```jsonc
// Array (recommended)
"License": [
  "<jwt-core.pro+postgres.pro>",
  "<jwt-tsak.cluster>"
]

// Pipe-separated string
"License": "<jwt1>|<jwt2>"
```

Environment variables follow the standard .NET convention (`:` → `__`):

```pwsh
# Single value
$env:Tsak__Redb__License = "<jwt1>|<jwt2>"

# Array by index
$env:Tsak__Redb__License__0 = "<jwt1>"
$env:Tsak__Redb__License__1 = "<jwt2>"
```

Legacy/secret-store fallback (read directly by `LicenseStore`, bypassing config):
`REDB_PRO_LICENSE` and `TSAK_LICENSE` env vars (also `\|`-separated). Files `redb.license` /
`tsak.license` next to the executable are picked up automatically. All sources are merged into a
single pool and deduplicated by `jti`.

### `Tsak:Api` — Built-in REST API

The worker runs an embedded HTTP server for management, metrics, and log access.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Enabled` | `bool` | `true` | Enable the REST API. |
| `Host` | `string` | `"0.0.0.0"` | HTTP listener bind address. |
| `Port` | `int` | `9090` | HTTP listener port. |

Endpoints include `/api/health`, `/api/contexts`, `/api/modules`, `/api/routes`, `/api/metrics`, `/api/logs/tail`, and more.

### `Tsak:Auth` — API Authentication

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Enabled` | `bool` | `false` | Require API key for management endpoints. Health endpoints are always open. |
| `Secret` | `string` | — | JWT signing secret (min 16 characters). |
| `Keys` | `array` | `[]` | Pre-configured API keys. |

Each key in the `Keys` array:

| Key | Type | Description |
|-----|------|-------------|
| `Id` | `string` | Unique key identifier. |
| `KeyHash` | `string` | SHA-256 hash of the API key (uppercase hex). |
| `Name` | `string` | Human-readable label. |
| `Roles` | `string` | Comma-separated roles (e.g. `"admin"`). |
| `UserId` | `string?` | Optional user association. |

Clients authenticate with the `Authorization: Bearer <raw-key>` header. The worker hashes it and matches against `KeyHash`.

### `Tsak:Modules` — Module Discovery

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `AssemblyPaths` | `string[]` | `[]` | Directories scanned for module DLLs on startup and during hot-reload. |

Modules are .NET assemblies implementing `ITsakModule`. Place compiled DLLs in the configured directories.

### `Tsak:Contexts` — Execution Environments

Contexts group modules into isolated `RouteContext` instances with independent lifecycle.

Configuration uses a 5-layer merge strategy (each layer overrides the previous):

1. `Tsak:Contexts:default` — base settings applied to all contexts
2. `Tsak:Contexts:{name}` — named context overrides
3. `context.json` — bundled inside module assemblies
4. `{Module}.config.json` — per-module config files
5. `Tsak:Contexts:{name}:Override` — DevOps final word (always wins)

Per-context keys:

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `AutoStart` | `bool` | `false` | Start context automatically when worker starts. |
| `Modules` | `string[]` | `[]` | Module names assigned to this context. |
| `Dependencies` | `string[]` | `[]` | Other contexts that must start first. |

Any additional keys become custom context properties accessible by modules at runtime.

**Example:**

```jsonc
"Contexts": {
  "default": {
    "AutoStart": true
  },
  "api": {
    "AutoStart": true,
    "Modules": ["ApiModule"],
    "Dependencies": ["default"]
  }
}
```

### `Tsak:HotReload` — Dynamic Module Updates

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Enabled` | `bool` | `true` | Enable periodic filesystem scanning for new/updated DLLs. |
| `ScanIntervalSeconds` | `int` | `30` | Polling interval for module directory changes. |
| `KeepVersions` | `int` | `2` | Number of old module versions retained (for rollback). |
| `StartupTimeoutSeconds` | `int` | `60` | Max time for a new module version to initialize before automatic rollback. |
| `RollingUpdate` | `bool` | `true` | In cluster mode, stagger updates across nodes. |
| `RemovalDebounceScans` | `int` | `1` | Scan cycles to wait before confirming a DLL removal. |
| `Collectible` | `bool` | `false` | Use collectible AssemblyLoadContext (enables GC of old assemblies, but incompatible with some Emit-based APIs). |
| `SharedPath` | `string` | `"Libs/shared"` | Directory for shared assemblies loaded into all module contexts. |

When `Enabled = false`, the worker loads modules once at startup with no further scanning.

### `Tsak:Watchdog` — Hung Exchange Detection

Monitors in-flight route exchanges and alerts when processing takes too long.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Enabled` | `bool` | `true` | Enable watchdog background service. |
| `CheckIntervalSeconds` | `int` | `15` | How often to check for stuck exchanges. |
| `SuspectedThresholdMinutes` | `double` | `5.0` | Log WARNING if an exchange exceeds this age. |
| `HungThresholdMinutes` | `double` | `30.0` | Log ERROR and raise alert if an exchange exceeds this age. |
| `AutoRestartHungRoutes` | `bool` | `false` | Automatically stop and restart routes with hung exchanges. |
| `AutoRestartTimeoutSeconds` | `int` | `60` | Timeout for the automatic restart operation. |

Supports fractional minutes (e.g. `0.5` = 30 seconds).

### `Tsak:Metrics` — Monitoring & Telemetry

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `CollectionIntervalSeconds` | `int` | `10` | How often metrics are sampled into the circular buffer. |
| `HistoryHours` | `int` | `12` | Duration of metric retention. At 10s intervals, 12h = 4,320 data points. |
| `ClusterReportIntervalSeconds` | `int` | `60` | How often metrics are reported to the cluster registry (cluster mode only). |
| `Prometheus:Enabled` | `bool` | `false` | Enable OpenTelemetry Prometheus exporter. |
| `Prometheus:Port` | `int` | `9464` | Prometheus scrape endpoint port. |

When Prometheus is enabled, metrics are exported at `http://*:{Port}/metrics` for Grafana or other scrapers.

### `Tsak:Logs` — Live Log Buffer

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `BufferSize` | `int` | `2000` | Number of log entries retained in the in-memory ring buffer for `/api/logs/tail`. |

### `Tsak:Cluster` — Cluster Coordination (Pro)

Requires `Storage:Type = "Redb"`, `Redb:UsePro = true`, and a valid license.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Enabled` | `bool` | `false` | Enable cluster mode. If `false`, all cluster services are skipped. |
| `ClusterName` | `string` | `"default"` | Logical cluster name. Nodes with different cluster names are isolated. |
| `GroupName` | `string` | `"default"` | Node group within the cluster. Used for module assignment targeting. |
| `NodeId` | `string` | hostname | Unique node identifier. Auto-generated from hostname if empty. |
| `ApiEndpoint` | `string` | auto | Public API URL for this node. If empty, built from `Tsak:Api:Host` + `Tsak:Api:Port`. |
| `HeartbeatIntervalSeconds` | `int` | `15` | How often the node sends alive signals to the registry. |
| `DeadNodeTimeoutSeconds` | `int` | `60` | Seconds without heartbeat before a node is marked dead. |
| `LeaderLockTtlSeconds` | `int` | `30` | TTL for the distributed leader lock. |
| `Strategy` | `string` | `"round-robin"` | Module assignment strategy: `"round-robin"` or `"least-loaded"`. |

When disabled (default), the worker runs as a standalone instance with no inter-node coordination.

### Logging (Serilog)

The worker uses [Serilog](https://serilog.net/) with console and file sinks. A custom `MemoryUsageEnricher` adds `{MemoryUsage}` (process working set in KB) to log output.

| Key | Description |
|-----|-------------|
| `Serilog:MinimumLevel:Default` | Global minimum level. |
| `Serilog:MinimumLevel:Override:redb.Tsak` | Override for Tsak-specific logs. |
| `Serilog:MinimumLevel:Override:redb.Route` | Override for route engine logs. |
| `Serilog:MinimumLevel:Override:Quartz` | Override for scheduler logs. |

Log files are written to `Logs/log-{Date}.txt` with 7-day retention by default.

### Quartz Scheduler

The worker integrates [Quartz.NET](https://www.quartz-scheduler.net/) for cron-triggered routes.

| Key | Default | Description |
|-----|---------|-------------|
| `Quartz:quartz.threadPool.threadCount` | `10` | Scheduler thread pool size. |
| `Quartz:quartz.jobStore.type` | `JobStoreTX` | ADO.NET job store for persistence. |
| `Quartz:quartz.jobStore.clustered` | `true` | Enable Quartz clustering (requires shared database). |
| `Quartz:quartz.scheduler.instanceId` | `AUTO` | Unique scheduler instance ID (auto-generated). |
| `Quartz:quartz.scheduler.instanceName` | `TsakScheduler` | Scheduler name. |

Quartz tables are auto-created from embedded SQL scripts (`tables_postgres.sql` / `tables_sqlServer.sql`).

---

## Architecture

```
Program.cs
  └── AddTsak(configuration)
        ├── ConfigureRedb()              Database setup (if Redb provider configured)
        ├── ConfigureStorage()           InMemory or Redb stores
        ├── TsakModuleRegistry           Module catalog
        ├── TsakContextManager           Context lifecycle
        ├── TsakCoordinator              Module ↔ context orchestration
        ├── SystemContextBuilder         Built-in _system context (API, health)
        ├── ConfigureHotReload()         DLL scanning & rolling updates
        ├── ConfigureMonitoring()        Metrics, watchdog, Prometheus
        ├── ConfigureQuartz()            Cron scheduler
        ├── AddTsakCluster()             Pro: cluster services (no-op if disabled)
        └── TsakHostedService            Main lifecycle (IHostedService)
```

### Project Dependencies

```
redb.Tsak.Worker
  ├── redb.Tsak.Core           (module registry, contexts, coordinator, API, monitoring)
  └── redb.Tsak.Core.Pro       (cluster: leader election, node registry, assignments)
        └── redb.Core.Pro      (EAV, licensing)
```

### Module Loading

Modules are .NET assemblies placed in `Tsak:Modules:AssemblyPaths` directories. The worker:

1. Scans directories on startup
2. Loads assemblies into isolated `AssemblyLoadContext`
3. Discovers `ITsakModule` implementations
4. Assigns modules to contexts per `Tsak:Contexts` config
5. Starts contexts with `AutoStart = true`

If hot-reload is enabled, the worker re-scans directories periodically and performs rolling updates of changed modules.

### Standalone vs Cluster

| Aspect | Standalone | Cluster |
|--------|-----------|---------|
| `Cluster:Enabled` | `false` (default) | `true` |
| Node discovery | N/A — single node | Via EAV registry |
| Module assignment | All modules run locally | Distributed by leader node |
| Leader election | N/A | Automatic (distributed lock) |
| Rolling updates | Local only | Coordinated across nodes |
| Database required | Only if `Storage:Type = "Redb"` | Always |

---

## Minimal Configuration Examples

### Standalone (no database)

```jsonc
{
  "Tsak": {
    "Storage": { "Type": "InMemory" },
    "Modules": {
      "AssemblyPaths": ["./Libs"]
    },
    "Contexts": {
      "default": { "AutoStart": true }
    },
    "Api": {
      "Enabled": true,
      "Port": 9090
    }
  }
}
```

### Standalone with Persistent Storage

```jsonc
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=redb;Username=postgres;Password=postgres"
  },
  "Tsak": {
    "Storage": { "Type": "Redb" },
    "Redb": {
      "Provider": "postgres"
    },
    "Modules": {
      "AssemblyPaths": ["./Libs"]
    },
    "Contexts": {
      "default": { "AutoStart": true }
    }
  }
}
```

### Cluster Mode

```jsonc
{
  "ConnectionStrings": {
    "Postgres": "Host=db.prod.local;Database=redb;Username=app;Password=secret"
  },
  "Tsak": {
    "Storage": { "Type": "Redb" },
    "Redb": {
      "Provider": "postgres",
      "UsePro": true,
      "License": [
        "<jwt-core.pro+postgres.pro>",
        "<jwt-tsak.cluster>"
      ],
      "PropsSaveStrategy": "ChangeTracking"
    },
    "Cluster": {
      "Enabled": true,
      "ClusterName": "production",
      "GroupName": "workers",
      "HeartbeatIntervalSeconds": 15,
      "Strategy": "round-robin"
    },
    "Api": {
      "Enabled": true,
      "Port": 9090
    },
    "Auth": {
      "Enabled": true,
      "Keys": [
        {
          "Id": "dashboard",
          "KeyHash": "<sha256-of-your-key>",
          "Name": "web-dashboard",
          "Roles": "admin"
        }
      ]
    }
  }
}
```
