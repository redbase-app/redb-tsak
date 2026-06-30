# redb.Tsak.Contracts

Shared DTO (Data Transfer Object) library for the [redb.Tsak](../../README.md) management API. Contains all request and response types that flow between the Tsak Worker (server) and consumers: the typed HTTP client (`redb.Tsak.Client`), the CLI (`redb.Tsak.CLI`), the Web dashboard, and any third-party integration.

## Why a dedicated contracts assembly?

- **Zero external dependencies** — no NuGet packages beyond the .NET SDK. Can be referenced anywhere without pulling in server-side dependencies.
- **Shared source of truth** — both `redb.Tsak.Core` (server controllers) and `redb.Tsak.Client` (HTTP client) reference this single assembly, eliminating DTO drift.
- **Stable API surface** — the types in this assembly form the public REST API contract. Additive changes are backwards-compatible; breaking changes follow semver.

## Install

```bash
dotnet add package redb.Tsak.Contracts
```

Usually you do not reference this package directly. It is pulled in transitively by `redb.Tsak.Client`.

## Model reference

All types are `sealed record` with `init`-only properties, serialized with `System.Text.Json` (case-insensitive, enum-as-string).

### Auth (`AuthModels.cs`)

| Type | Description |
|---|---|
| `ApiKeyCreatedResponse` | Result of key creation — includes the raw key (shown once only) |
| `ApiKeyInfo` | Key metadata: ID, name, roles, expiry, validity |
| `ApiKeyRevokedResponse` | Confirmation of revocation |

```csharp
public sealed record ApiKeyCreatedResponse
{
    public required string Id       { get; init; }
    public required string Key      { get; init; } // raw key — save it now
    public required string Name     { get; init; }
    public required string Roles    { get; init; }
    public string?         UserId   { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Message { get; init; }
}
```

### Cluster (`ClusterModels.cs`, `ClusterTopology.cs`, `NodeStatus.cs`)

| Type | Description |
|---|---|
| `ClusterStatusResponse` | Enabled flag, node ID, leader flag, current epoch |
| `ClusterNodesResponse` | `Enabled` flag + `Nodes` list |
| `ClusterNodeInfo` | Node ID, hostname, status (`NodeStatus` enum), started-at, last-heartbeat |
| `ClusterRebalanceResponse` | New epoch after rebalance |
| `ClusterNodeRemovedResponse` | Confirmation of node removal |
| `NodeStatus` | Enum: `Unknown`, `Active`, `Offline`, `Dead` |
| `ClusterTopology` | EAV-backed topology map (context → node assignments) |

### Contexts (`ContextModels.cs`)

| Type | Description |
|---|---|
| `ContextSummary` | Name, status, endpoint count, anonymous flag, route count |
| `ContextDetail` | Full context view: routes, endpoints, components, autostart, started-at |
| `ContextActionResponse` | Result of start/stop/restart/remove; includes drain metrics |
| `TsakRouteInfo` | Route ID, from-URI, status, autostart, inflight count, cluster policy |
| `TsakEndpointInfo` | Endpoint URI and type |
| `RouteDetail` | Full route view: endpoints, policy metadata |
| `RoutePolicyInfo` | Cluster policy: requested flag, effective policy, factory type, reason |
| `RouteActionResponse` | Result of a route lifecycle operation |
| `InflightExchangeDto` | Snapshot of one in-flight exchange (ID, route, age, body preview) |

`ContextActionResponse` carries drain telemetry useful for capacity planning:

```csharp
public sealed record ContextActionResponse
{
    public required string Name    { get; init; }
    public string?         Status  { get; init; }
    public bool?           Removed { get; init; }
    public int?  InflightBefore    { get; init; } // exchanges at stop initiation
    public int?  InflightAfter     { get; init; } // exchanges after drain (0 = fully drained)
    public long? DrainTimeMs       { get; init; } // time spent draining
}
```

### Diagnostics (`DiagnosticModels.cs`)

| Type | Description |
|---|---|
| `DiagnosticDump` | Full system snapshot: inflight exchanges, ThreadPool, GC, context summaries, watchdog alerts, lifecycle events |
| `DiagnosticContextSummary` | Per-context counts in the dump |
| `ThreadPoolDiagnostics` | Pending work items, worker thread pool min/max/available |
| `GcDiagnostics` | Gen0/1/2 collection counts, total managed memory (MB) |
| `RouteDiagnosticDump` | Single-route dump: inflight, metrics, recent errors, lifecycle history |

### Lifecycle (`LifecycleModels.cs`)

| Type | Description |
|---|---|
| `LifecycleEvent` | Timestamp, context name, route ID, event type, details string |
| `LifecycleEventType` | Enum: `ContextStarted`, `ContextStopped`, `RouteStarted`, `RouteStopped`, `RouteError`, `ModuleLoaded`, `ModuleUnloaded`, … |

### Logs (`LogModels.cs`)

| Type | Description |
|---|---|
| `LogsResponse` | `Available` flag, `Entries` list, optional continuation ID |
| `LogEntry` | Timestamp, level, source, message |
| `LogFilesResponse` | `Available` flag, list of `LogFileInfo` (name, size, last-modified) |

### Metrics (`MetricsModels.cs`)

| Type | Description |
|---|---|
| `MetricsResponse` | `Available` flag, latest `SystemMetricsSnapshot`, stored point count |
| `MetricsHistoryResponse` | Time-windowed list of `SystemMetricsSnapshot` |
| `SystemMetricsSnapshot` | Timestamp, CPU (process + system), memory, threading, GC |
| `RouteMetricsSummary` | Per-route: processed count, error count, avg/max latency (ms) |
| `DashboardSnapshot` | Aggregated view: system metrics + context/route states |

### Modules (`ModuleModels.cs`)

| Type | Description |
|---|---|
| `ModuleInfo` | Module name, version, description, status, `CanInitialize` flag, dependencies |
| `ModuleRemovedResponse` | Confirmation of removal |

### Scheduler (`SchedulerModels.cs`)

| Type | Description |
|---|---|
| `SchedulerStatusResponse` | Status string, scheduler name, instance ID, started/standby/shutdown flags, job counts |
| `ScheduledJobsResponse` | List of `ScheduledJobInfo` |
| `ScheduledJobInfo` | Job name, group, trigger state, cron expression, next/previous fire time |
| `RunningJobsResponse` | List of `RunningJobInfo` |
| `RunningJobInfo` | Job name, group, fire time, runtime (ms) |
| `SchedulerActionResponse` | Message string from start/standby/pause/resume |

### System (`SystemModels.cs`)

| Type | Description |
|---|---|
| `SystemInfoResponse` | Version, started-at, uptime string, context count, module count, machine name, CPU count, working set (MB) |
| `HealthCheckResult` | Status enum, description, per-check dictionary, timestamp |

### Users (`UserModels.cs`)

| Type | Description |
|---|---|
| `TsakUserInfo` | Login, display name, email, roles, created-at |
| `TsakCreateUserRequest` | Login, display name, email, password, roles |
| `TsakUpdateUserRequest` | Display name, email, password, roles (all optional) |

### Watchdog (`WatchdogModels.cs`)

| Type | Description |
|---|---|
| `WatchdogState` | Last check time, suspected/hung exchange counts, auto-restart count, enabled flag, active alerts list |
| `WatchdogAlert` | Exchange ID, route ID, context name, level (`Suspected`/`Hung`), detected-at, elapsed seconds |

## Serialization conventions

All types are designed for `System.Text.Json` with these settings (applied by `TsakApiClient`):

```csharp
new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }
}
```

Enum values serialize as their string names (e.g. `"Active"`, not `2`). All date/time values use `DateTimeOffset` (UTC).

## Versioning

This assembly follows [Semantic Versioning](https://semver.org):

- **Additive changes** (new optional properties, new enum members) → patch/minor bump, fully backwards-compatible.
- **Renames or removals** → major version bump, coordinated with `redb.Tsak.Core` and `redb.Tsak.Client`.

Current version: **1.0.4**

## Related projects

| Project | Role |
|---|---|
| [redb.Tsak.Client](../redb.Tsak.Client/README.md) | Typed HTTP client that returns these types |
| [redb.Tsak.Core](../redb.Tsak.Core/README.md) | Server-side controllers that produce these types |
| [redb.Tsak.CLI](../redb.Tsak.CLI/README.md) | CLI tool that consumes these types via the client |
| [redb.Tsak](../../README.md) | Full project overview |
