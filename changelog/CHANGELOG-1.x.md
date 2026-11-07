# redb.Tsak changelog: 1.x

Releases of the 1.x line, moved here unchanged from CHANGELOG.md when the main file started to
follow the current major only. Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [1.0.4] — 2026-05-06

First public NuGet release. All 9 implementation phases complete. Production-tested since 1.0.0.

### Added — Core Engine

#### Module System
- `ITsakModule` interface — convention-based module discovery (`InitRoute.main()` or `RouteBuilder`)
- `ModuleLoader` — file-based DLL discovery in configured `AssemblyPaths`
- `ModuleRegistry` — registry of loaded modules with version tracking per datetime stamp
- `ModuleAssemblyLoadContext` — isolated `AssemblyLoadContext` per module to prevent dependency conflicts
- `manifest.json` support — module metadata (Name, Version, Description, Author)
- `context.json` support — module-shipped infrastructure defaults (layer 3 of 5-layer config)
- `{Module}.config.json` support — module-shipped business settings (layer 4)

#### Execution Contexts
- `IContextManager` — lifecycle interface: Create, Start, Stop, Restart, Remove, AutoStart
- `ContextManager` — `ConcurrentDictionary`-backed implementation with event bus
- Named contexts with independent lifecycle
- `_system` protected context — hosts the REST API, cannot be stopped or removed via API
- AutoStart flag per context — contexts start automatically on worker startup
- Context lifecycle events: Created, Started, Stopped, Restarted, Removed

#### Hot Reload
- `HotReloadService` — file-system watcher with configurable scan interval
- Rolling update in cluster mode — nodes update sequentially, zero overall downtime
- Rollback via `KeepVersions` — keeps N previous versions, one-command revert
- `Collectible` ALC option — full GC reclamation for modules without `Reflection.Emit`
- `TryUnload()` — safe no-op when collectible mode is disabled
- `LeakedAlcCount` metric — tracks non-collectible orphaned ALCs awaiting process restart
- `HotReload:StartupTimeoutSeconds` — waits for old version to settle before loading new

#### 5-Layer Configuration Model
- Layer 1: `Tsak:Contexts:default` — base for all contexts
- Layer 2: `Tsak:Contexts:{name}` — named context overrides
- Layer 3: `Libs/{Module}/context.json` — module infra defaults
- Layer 4: `Libs/{Module}/{Module}.config.json` — module builder settings
- Layer 5: `Tsak:Contexts:{name}:Override` — DevOps final word
- Deep-merge semantics: later layers win on conflicts, earlier layers provide defaults
- `ConfigMerger` — type-safe merge engine supporting nested objects and arrays

### Added — REST API (32 Endpoints)

- `AuthController` (3) — `POST /api/auth/keys`, `GET /api/auth/keys`, `DELETE /api/auth/keys/{id}`
- `ContextsController` (7) — list, get, start, stop, restart, delete contexts + status
- `ModulesController` (3) — list, get, delete loaded modules
- `ClusterController` (3) — overview, node list, trigger rebalance
- `SystemController` (4) — health check, metrics snapshot, metrics history, server info
- `LogsController` (1) — query ring-buffer with level/limit filters
- `SchedulerController` (9) — status, job list, running jobs, start/standby, pause/resume, fire-now
- `WatchdogController` (2) — route health status, suspend/resume watchdog
- `DiagnosticsController` — internal diagnostics endpoints

### Added — Security

- API Key authentication — `Authorization: Bearer <key>` or `X-Api-Key: <key>`
- HMAC-SHA256 key hashing with constant-time comparison (timing-attack safe)
- Role-based authorization on individual endpoints
- `RedbApiKeyStore` — EAV-backed durable key store (Pro mode)
- In-memory key store for standalone mode (read-only, seeded from config)
- Key expiry — optional expiration date per key
- Key revocation — immediate revocation via API or CLI
- `UserId` association — link API keys to user accounts
- 5-minute TTL cache for key lookups (reduces DB reads in high-throughput scenarios)
- Config seed — keys from `Tsak:Auth:Keys` are seeded into EAV store on first startup

### Added — Cluster

- Leader election via distributed lock in redb EAV (epoch-fenced)
- `ClusterCoordinator` — orchestrates leader/follower roles on startup and on leader change
- `NodeRegistry` — heartbeat-based node registration with dead-node cleanup
- `AssignmentManager` — distributes contexts across nodes (round-robin strategy)
- `ClusterTopology` — 3-level tree: cluster → group → node, stored in redb EAV
- Heartbeat interval, dead-node timeout, leader lock TTL all configurable
- Rolling hot-reload in cluster mode — nodes update one by one using cluster coordination
- `ClusterReportIntervalSeconds` — periodic cluster state sync to shared store

### Added — Monitoring

- `MetricsService` — circular buffer metrics: CPU, memory, threads, GC (12h × 10s = 4320 points)
- `ContextMetricsCollector` — per-context route metrics aggregation
- OpenTelemetry integration — per-route message count, error rate, latency
- `HealthCheckService` — composite health: contexts + metrics thresholds + cluster state
- `LogRingBuffer` — Serilog in-memory sink, 2000 entries, queryable via REST
- `WatchdogService` — detects suspected routes (configurable threshold) and hung routes
  - Optional `AutoRestartHungRoutes` — automatic restart without operator action
  - `SuspectedThresholdMinutes` and `HungThresholdMinutes` configurable
- Prometheus scrape endpoint (optional, port 9464)

### Added — Scheduler

- `QuartzSchemaInitializer` — auto-creates `QRTZ_*` tables on first startup
- `RAMJobStore` — default for standalone mode (no DB required)
- `AdoJobStore` — cluster-safe persistent job store (PostgreSQL and MSSQL)
- Built-in `IScheduler` injected into every route context — modules get it for free
- `redb.Route.Quartz` integration — modules define cron/timer routes using `Cron.Schedule()`

### Added — CLI (30 Commands)

- Authentication: `login`, `logout`, `auth keys list/create/revoke`
- Contexts: `context list/get/start/stop/restart/delete/status`
- Modules: `module list/get/delete/deploy`
- Monitoring: `health`, `metrics`, `logs`, `watchdog status/suspend/resume`
- Scheduler: `scheduler status/jobs/running/start/standby/pause-job/resume-job/fire-job`
- Cluster: `cluster overview/nodes/rebalance`
- Diagnostics: `diagnostics`
- Route: `route list`, `route stop/start` via `RouteCommands`
- Watchdog: `watchdog` group via `WatchdogCommands`
- Rich table output with Spectre.Console
- `--output json` flag for CI-friendly output

### Added — Web Dashboard (10 Pages)

- **Dashboard** — node overview, health status, uptime, context count, metrics summary
- **Routes** — all routes, statuses, message count, error rate
- **Contexts** — context lifecycle management (start/stop/restart from UI)
- **Endpoints** — consumer/producer endpoints per route
- **Watchdog** — suspected/hung route alerts with manual controls
- **Cluster** — cluster topology, leader, nodes, group assignments
- **Logs** — searchable ring-buffer log viewer with level filter
- **Node Detail** — per-node drill-down
- **Auth** — API key management UI
- **Login** — credential-based access to the dashboard
- Standalone mode (node list in config) and Cluster mode (nodes from EAV)

### Added — Storage & Persistence

- `InMemory` mode — no database required, all state is volatile
- `Redb` mode (PostgreSQL and MSSQL) — durable module and state storage via EAV
- `redb.Core.Pro` integration — EAV change tracking, clustering primitives
- Auto schema initialization for both redb EAV tables and Quartz tables
- `ConnectionStrings:Postgres` and `ConnectionStrings:MSSql` support

### Added — Developer Experience

- Convention-based module discovery: any DLL with `InitRoute.main()` or `RouteBuilder` subclass is loaded automatically
- `redb.Tsak.Client` NuGet package — embed Tsak API access in other applications
- `ITsakApiClient` — typed async client for all 32 endpoints
- Serilog structured logging with `{MemoryUsage}` enricher
- Docker multi-stage build (`Dockerfile`) — produces minimal `aspnet:9.0` image
- `LifecycleHookOrdering` — deterministic startup/shutdown ordering for hosted services

### Tests

- 287 unit tests in `redb.Tsak.Tests` (xUnit, FluentAssertions, NSubstitute)
  - Core: module loading, context lifecycle, config merge, event bus
  - Security: key hashing, constant-time comparison, role validation, TTL cache
  - Cluster: leader election, heartbeat, rebalance logic, epoch fencing
  - Hot reload: rolling update, rollback, ALC isolation, collectible toggle
  - Monitoring: metrics collection, watchdog thresholds, health aggregation
- 64 CLI tests in `redb.Tsak.CLI.Tests`
  - All 30 commands: output format, error handling, auth, table rendering
- Total: **351 passing**

---

[3.2.0]: https://github.com/redbase-app/redb/compare/tsak-v1.0.4...HEAD
[1.0.4]: https://github.com/redbase-app/redb/releases/tag/tsak-v1.0.4
