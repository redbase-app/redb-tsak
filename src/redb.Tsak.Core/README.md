# redb.Tsak.Core

The server-side engine of [redb.Tsak](../../README.md). Contains the REST API controllers, all runtime services (hot-reload, context management, coordinator, watchdog, metrics), the module system (assembly loading, isolated ALCs), security middleware (API key auth with HMAC-SHA256), and storage abstractions.

This project is referenced by the Worker host (`redb.Tsak.Worker`) and the Pro variant (`redb.Tsak.Core.Pro`). It is not meant to be consumed directly by application code — use `redb.Tsak.Client` for programmatic access.

## Project structure

```
redb.Tsak.Core/
├── Contracts/          ← Interfaces (ITsakModule, ITsakContextManager, ITsakCoordinator, …)
├── Controllers/        ← redb.Route controllers (14 here + 1 from Core.Pro, 57 endpoints)
├── Extensions/         ← DI registration helpers
├── Models/             ← Internal models (not exposed as API DTOs)
├── Modules/            ← Assembly loading: ALC, hot-swap, manifest, packages
├── Monitoring/         ← Watchdog, metrics, health, lifecycle audit, log ring-buffer
├── Security/           ← API key service, stores, audit filter
└── Services/           ← Business logic: context manager, coordinator, hot-reload, storage
```

## Core concepts

### Module

A **module** is a deployable unit — a directory of DLLs or a `.tpkg` archive — that contains one or more `redb.Route` route builders. The core module contract is `ITsakModule`:

| Status | Meaning |
|---|---|
| `Discovered` | Directory/package found by `HotReloadService`, not yet loaded |
| `Loaded` | Assembly loaded into isolated ALC |
| `Initialized` | `InitRoute.main()` / `RouteBuilder` resolved |
| `Running` | Context started, transports connected |
| `Stopped` | Graceful stop completed |
| `Faulted` | Error during load or initialization |

Two concrete implementations exist:
- `StaticMethodModule` — wraps a `static void main(IRouteContext)` entry point
- `RouteBuilderModule` — wraps a `RouteBuilder` subclass

### Context

A **context** is a named `IRouteContext` instance that groups one or more modules into a shared lifecycle, property bag, and redb instance. Contexts are either:

- **Named** — declared in `appsettings.json` under `Tsak:Contexts:{name}:Modules`, may host multiple modules.
- **Anonymous** — created automatically for any module not assigned to a named context; one module = one context.

### 5-layer configuration merge

Every context's property bag is built by `ConfigMergeHelper.DeepMerge` across five layers (outer layers win):

| Layer | Source | Who controls |
|---|---|---|
| 1 (base) | `Tsak:Contexts:default` | DevOps default |
| 2 | `Tsak:Contexts:{name}` | DevOps per-context |
| 3 | `context.json` (module dir) | Module developer |
| 4 | `{Module}.config.json` | Module developer |
| 5 (override) | `Tsak:Contexts:{name}:Override` | DevOps override (highest prio) |

## Services

### `HotReloadService`

Background service that periodically scans `Tsak:Modules:AssemblyPaths` for:
- Directories containing `*.dll` → loads via per-module `ModuleAssemblyLoadContext`
- `*.tpkg` ZIP archives → unpacks into `ModulePackage` with its own isolated ALC

Tracks file timestamps and performs graceful hot-swap:
1. New DLL/package detected → load new module into new ALC
2. Stop old module (drain in-flight exchanges)
3. Register new module, start context
4. Optionally unload old ALC (requires `Collectible = true`)

Key configuration (`Tsak:HotReload`):

| Key | Default | Description |
|---|---|---|
| `ScanIntervalSeconds` | `10` | How often to scan `Libs/` |
| `Collectible` | `false` | Enable ALC unload for GC reclamation (incompatible with `Reflection.Emit`) |
| `KeepVersions` | `2` | Old versions kept for rollback |
| `StartupTimeoutSeconds` | `60` | New version must settle within this window |
| `RollingUpdate` | `true` | In cluster: update nodes one by one |
| `RemovalDebounceScans` | `2` | Scan cycles a missing file must persist before triggering unload |

### `TsakContextManager`

Manages the lifecycle of all `IRouteContext` instances:
- Creates named and anonymous contexts from config + merged property bag
- Starts/stops/restarts contexts and individual routes
- Serializes lifecycle operations via `SemaphoreSlim` to prevent races
- Maintains isolated mini-DI containers per named `IRedbService` instance (created by `RedbInstanceFactory`)
- Persists autostart state in `ITsakStateStore`
- Publishes events to `LifecycleAuditService`

### `TsakCoordinator`

Bridges the module registry and the context manager. Subscribes to `ITsakModuleRegistry` events (`ModuleAdded`, `ModuleRemoved`, `ModuleUpdated`, `ModulesBatchAdded`) and orchestrates context creation/teardown:
- Groups modules by target context (named vs anonymous)
- Per-context locks prevent races when multiple modules arrive concurrently
- On module removal, decides whether to destroy the whole context or just remove one module from a named context

### `TsakModuleRegistry`

In-memory registry of all currently known `ITsakModule` instances. Raises typed events consumed by `TsakCoordinator`. Backed by `ITsakModuleStore` for persistence across restarts (optional).

### `RedbInstanceFactory`

Creates isolated `IRedbService` instances (Postgres or MSSql, open or Pro) from a config dictionary. Each instance lives in its own `ServiceProvider` mini-container that the context manager disposes when the context is removed.

### `SystemContextBuilder`

Builds the global `system` context that hosts Tsak's own REST API and monitoring endpoints. Configured by `ISystemContextPlugin` contributors (Pro variant adds cluster leadership, metrics retention, etc.).

## Monitoring subsystem

### `RouteWatchdogService`

Background service (`IHostedService`) that periodically scans all routes for:
- **Suspected** exchanges — in-flight longer than `Tsak:Watchdog:SuspectedThresholdSeconds`
- **Hung** exchanges — in-flight longer than `Tsak:Watchdog:HungThresholdSeconds`

Maintains a queryable `WatchdogState` with `ActiveAlerts`. Can auto-restart hung routes when configured. Enable/disable at runtime via `RouteWatchdogService.Enable()` / `.Disable()`, or via the REST API / CLI.

### `MetricsCollectionService`

Background service that samples CPU, memory, thread pool, and GC metrics on a configurable interval and stores them in a ring buffer. The ring buffer is queryable via `/api/system/metrics` and `/api/system/metrics/history`.

### `HealthCheckService`

Aggregates `IHealthContributor` registrations (from Core, Pro, and module-contributed) into a single `/api/system/health` response.

### `LifecycleAuditService`

Maintains an append-only audit log of context/route lifecycle events, queryable via `/api/diagnostics/lifecycle`. Persisted to `ITsakStateStore` in Redb-backed mode.

### `LogRingBuffer` / `RingBufferSink`

Serilog sink that captures recent log entries into a fixed-capacity ring buffer. Accessible via `/api/logs`. Enabled when `Tsak:Logging:RingBuffer:Enabled = true`.

## Security

### `ApiKeyService`

- Keys are stored as HMAC-SHA256 hashes (never raw).
- Validation uses `CryptographicOperations.FixedTimeEquals` to prevent timing attacks.
- Validated results are cached for `CacheTtl` (default 5 min) to reduce store round-trips.

### `IApiKeyStore` implementations

| Implementation | Storage | Use case |
|---|---|---|
| `ConfigApiKeyStore` | `appsettings.json` (static list) | Single-node dev/simple deployments |
| `RedbApiKeyStore` | redb-backed schema | Multi-node, runtime key management |

### `AdminAuditFilter`

Controller action filter that records every mutating API call (marked `[AuditAdminAction]`) to the registered `IAdminAuditService`. The effective sink is `RouteAdminAuditService`, which persists events to the flat `tsak_audit_log` table (via `direct://tsak-audit`) and falls back to `LogAdminAuditService` (a `[tsak-audit]`-anchored JSON log line) when no database is configured. See `Audit/` for the schema initializer, writer route, query service and retention job.

## Controllers

| Controller | Path | Operations |
|---|---|---|
| `AuthController` | `/api/auth` | Create/list/revoke API keys |
| `ContextsController` | `/api/contexts` | CRUD + lifecycle for contexts |
| `RoutesController` | `/api/contexts/{ctx}/routes` | Route lifecycle, inflight, force-stop, per-route metrics |
| `ModulesController` | `/api/modules` | List, get, remove modules |
| `SchedulerController` | `/api/scheduler` | Quartz status, jobs, start/standby/pause/resume |
| `ClusterController` | `/api/cluster` | Cluster status, nodes, rebalance (ships in `Core.Pro`, contributed via `ISystemContextPlugin`) |
| `WatchdogController` | `/api/watchdog` | State, alerts, enable/disable |
| `DiagnosticsController` | `/api/diagnostics` | System dump, route-level dump |
| `LogsController` | `/api/logs` | Ring-buffer entries, log files, download |
| `LifecycleController` | `/api/lifecycle` | Recent lifecycle events (filtered) |
| `AuditController` | `/api/audit` | Persisted admin-action trail (filtered, paged; `admin` role) |
| `SystemController` | `/api/system` | Health, info, metrics, metrics history |
| `UsersController` | `/api/users` | User CRUD (Pro) |
| `DashboardController` | `/api/dashboard` | Aggregated snapshot |
| `HealthProbeController` | `/api/health` | Kubernetes probes: `startup`, `live`, `ready` (auth-exempt) |

57 endpoints in total. Request path, auth model and the full per-endpoint map live in
[API_GUIDE.md](../../API_GUIDE.md).

## Module assembly loading

### `ModuleAssemblyLoadContext`

Per-module isolated `AssemblyLoadContext`. Resolution order:
1. Module's own directory (private DLLs)
2. Shared loader (`SharedAssemblyLoader` — common assemblies provided by the host)
3. Default ALC (host assemblies)

`Collectible = true` enables `Unload()` for GC reclamation after a hot-swap, at the cost of incompatibility with `Reflection.Emit`-based serializers.

### `ModulePackage`

Manages `.tpkg` ZIP archives. On load, extracts the archive to a temp directory, resolves entry-point DLLs, loads them into a dedicated isolated ALC. On unload (atomic package removal), tears down all modules from the package simultaneously.

### `ModuleManifest`

Deserialized from `manifest.json` inside each module directory or `.tpkg`:

```json
{
  "Name": "Orders",
  "Version": "2026.05.06.1430",
  "EntryPoints": ["Orders.dll"],
  "Dependencies": ["redb.Route.Kafka"]
}
```

`EntryPoints` lists DLLs containing route builders. Other DLLs in the package are companion dependencies loaded into the same ALC.

### `SharedAssemblyLoader`

Coordinates assembly resolution across multiple module ALCs to avoid loading the same host-provided DLL multiple times. Modules that resolve via the shared loader get the host's singleton instance (same type identity, no interface mismatch).

### `LoadedAssemblyTracker`

Prevents duplicate DLL loads when multiple modules in the same ALC reference the same assembly file.

## Storage abstractions

Two storage areas are abstracted behind interfaces:

| Interface | In-Memory impl | Redb-backed impl |
|---|---|---|
| `ITsakModuleStore` | `InMemoryTsakModuleStore` | `RedbTsakModuleStore` |
| `ITsakStateStore` | `InMemoryTsakStateStore` | `RedbTsakStateStore` |

`ITsakModuleStore` — persists module registrations across restarts.  
`ITsakStateStore` — persists context autostart flags and lifecycle audit events.

Configure via `Tsak:Storage:Mode`:
- `InMemory` — default; stateless restarts, no database required.
- `Redb` — persistent; requires a configured `IRedbService` (Postgres or MSSql).

## Extension points

| Interface | Description |
|---|---|
| `IContextCustomizer` | Runs after a context is created — inject custom properties or routes |
| `IHealthContributor` | Contributes checks to `/api/system/health` |
| `IModuleHealthContributor` | Per-module health contribution |
| `IStaticModuleProvider` | Provides in-process modules without hot-reload (e.g. for testing or embedding) |
| `ISystemContextPlugin` | Adds system-context routes (used by Pro for cluster endpoints) |
| `ITsakLifecycleHook` | Called on context/route start/stop events |

## Key dependencies

| Dependency | Purpose |
|---|---|
| `redb.Tsak.Contracts` | Shared DTO types for API responses |
| `redb.Core` / `redb.Core.Pro` | typed object store (state + key storage) |
| `redb.Route.Core` | Route context, builders, exchange model |
| `redb.Route.Http` | HTTP transport for route endpoints |
| `redb.Route.Quartz` | Quartz scheduler integration |
| `redb.Postgres` / `redb.MSSql` | Database providers |
| `Serilog` | Structured logging + ring-buffer sink |

## Related projects

| Project | Role |
|---|---|
| [redb.Tsak.Contracts](../redb.Tsak.Contracts/README.md) | DTO models exposed by this project's controllers |
| [redb.Tsak.Client](../redb.Tsak.Client/README.md) | Typed HTTP client for consuming this project's API |
| [redb.Tsak.CLI](../redb.Tsak.CLI/README.md) | CLI that talks to this project via the client |
| [redb.Tsak.Worker](../redb.Tsak.Worker/) | ASP.NET Core host that wires up this project |
| [redb.Tsak](../../README.md) | Full project overview |
