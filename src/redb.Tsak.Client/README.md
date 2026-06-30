# redb.Tsak.Client

Typed HTTP client for the [redb.Tsak](../../README.md) runtime REST API. Wraps every API endpoint behind a strongly-typed async interface with cancellation support, a configurable timeout, and structured error handling.

## Install

```bash
dotnet add package redb.Tsak.Client
```

The package targets `net8.0` / `net9.0` / `net10.0` and has a single dependency: `redb.Tsak.Contracts` (the shared DTO assembly).

## Quick start

```csharp
using redb.Tsak.Client;

// Disposable — wrap in using or manage lifetime via DI
using var client = new TsakApiClient(
    baseUrl: "http://tsak-host:5100",
    apiKey:  "sk-your-api-key");

// Get all contexts
var contexts = await client.ListContextsAsync();
foreach (var ctx in contexts)
    Console.WriteLine($"{ctx.Name}  {ctx.Status}  ({ctx.EndpointCount} endpoints)");
```

## Authentication

When the Tsak node has `Tsak:Auth:Enabled = true`, every call (except `/api/system/health`) requires an API key. Pass it to the constructor — the client adds it as the `X-API-Key` header on all requests automatically.

```csharp
var client = new TsakApiClient("http://host:5100", apiKey: "sk-...");
```

If auth is disabled on the node, pass `null` for `apiKey`.

## Timeout

The default timeout is **5 seconds** (`TsakApiClient.DefaultTimeout`). For long-running operations (diagnostics dump, force-stop, module install) override per-client or use a per-call `CancellationToken`:

```csharp
// Longer default for all calls on this client
using var client = new TsakApiClient(baseUrl, apiKey, timeout: TimeSpan.FromSeconds(30));

// Per-call deadline
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var dump = await client.GetDiagnosticDumpAsync(cts.Token);
```

## Interface

`ITsakApiClient` is the full surface. Inject it in your DI container and swap `TsakApiClient` with a test double:

```csharp
services.AddSingleton<ITsakApiClient>(_ =>
    new TsakApiClient(config["Tsak:Url"], config["Tsak:ApiKey"]));
```

### Auth — API key management

```csharp
// Create a key
var created = await client.CreateApiKeyAsync("ci-bot", roles: ["readonly"]);
Console.WriteLine(created.Key); // Print raw key — not returned again

// List keys
var keys = await client.ListApiKeysAsync();

// Revoke by ID
await client.RevokeApiKeyAsync(keyId);
```

### Contexts

```csharp
var contexts = await client.ListContextsAsync();
var detail   = await client.GetContextAsync("orders");

await client.StartContextAsync("orders");
await client.StopContextAsync("orders");
await client.RestartContextAsync("orders");
await client.ResetRouteStatesAsync("orders"); // reset persisted state + restart
await client.RemoveContextAsync("orders");
```

### Routes

```csharp
var routes    = await client.ListRoutesAsync("orders");
var route     = await client.GetRouteAsync("orders", "in-0");
var endpoints = await client.ListEndpointsAsync("orders");

await client.StartRouteAsync("orders", "in-0");
await client.StopRouteAsync("orders", "in-0", timeoutSeconds: 30);
await client.ForceStopRouteAsync("orders", "in-0"); // no drain

// In-flight exchange inspection
var inflight = await client.GetRouteInflightAsync("orders", "in-0");
var allInflight = await client.GetContextInflightAsync("orders");
```

### Modules

```csharp
var modules = await client.ListModulesAsync();
var mod     = await client.GetModuleAsync("Orders");
await client.RemoveModuleAsync("Orders");
```

### Scheduler (Quartz)

```csharp
var status  = await client.GetSchedulerStatusAsync();
var jobs    = await client.ListScheduledJobsAsync();
var running = await client.ListRunningJobsAsync();

await client.StartSchedulerAsync();
await client.StandbySchedulerAsync();
await client.PauseJobAsync("DEFAULT.my-job");
await client.ResumeJobAsync("DEFAULT.my-job");
```

### Cluster

```csharp
var status = await client.GetClusterStatusAsync();
var nodes  = await client.ListClusterNodesAsync();
await client.RebalanceClusterAsync();
await client.RemoveClusterNodeAsync(nodeId);
```

### System health & metrics

```csharp
var health  = await client.GetHealthAsync();
var metrics = await client.GetMetricsAsync();
var history = await client.GetMetricsHistoryAsync(minutes: 10);
var info    = await client.GetInfoAsync();
```

### Watchdog

```csharp
var state  = await client.GetWatchdogStatusAsync();
var alerts = await client.GetWatchdogAlertsAsync();
await client.EnableWatchdogAsync();
await client.DisableWatchdogAsync();
```

### Lifecycle audit log

```csharp
var events = await client.GetLifecycleEventsAsync(
    context: "orders",
    type: "RouteStarted",
    since: DateTime.UtcNow.AddHours(-1),
    limit: 50);
```

### Diagnostics

```csharp
var dump      = await client.GetDiagnosticDumpAsync();
var routeDump = await client.GetRouteDiagnosticsAsync("orders", "in-0");
var dashboard = await client.GetDashboardAsync();
```

### Logs

```csharp
var logs      = await client.GetLogsAsync(limit: 200, level: "Warning");
var logFiles  = await client.GetLogFilesAsync();
var zipBytes  = await client.DownloadLogFileAsync("tsak-2026-05-07.log");
```

### Users (Pro)

```csharp
var users = await client.ListUsersAsync();
var user  = await client.GetUserAsync("alice");

var created = await client.CreateUserAsync(new TsakCreateUserRequest { Login = "bob", ... });
await client.UpdateUserAsync("bob", new TsakUpdateUserRequest { ... });
await client.DeleteUserAsync("bob");
```

## Error handling

All methods throw `ApiException` on non-2xx responses:

```csharp
try
{
    await client.StartContextAsync("orders");
}
catch (ApiException ex)
{
    Console.Error.WriteLine($"[{ex.StatusCode}] {ex.Message}");
}
```

`ApiException` carries the HTTP status code and server-provided message string.

## API surface summary

| Domain | Methods |
|---|---|
| Auth | `CreateApiKey`, `ListApiKeys`, `RevokeApiKey` |
| Cluster | `GetClusterStatus`, `ListClusterNodes`, `RebalanceCluster`, `RemoveClusterNode` |
| Contexts | `ListContexts`, `GetContext`, `StartContext`, `StopContext`, `RestartContext`, `ResetRouteStates`, `RemoveContext` |
| Routes | `ListRoutes`, `GetRoute`, `ListEndpoints`, `StartRoute`, `StopRoute`, `ForceStopRoute`, `GetRouteInflight`, `GetContextInflight` |
| Modules | `ListModules`, `GetModule`, `RemoveModule` |
| Scheduler | `GetSchedulerStatus`, `ListScheduledJobs`, `ListRunningJobs`, `StartScheduler`, `StandbyScheduler`, `PauseJob`, `ResumeJob` |
| Watchdog | `GetWatchdogStatus`, `GetWatchdogAlerts`, `EnableWatchdog`, `DisableWatchdog` |
| Lifecycle | `GetLifecycleEvents` |
| Metrics | `GetRouteMetrics`, `GetDashboard`, `GetMetrics`, `GetMetricsHistory` |
| Diagnostics | `GetDiagnosticDump`, `GetRouteDiagnostics` |
| Logs | `GetLogs`, `GetLogFiles`, `DownloadLogFile` |
| System | `GetHealth`, `GetInfo` |
| Users | `ListUsers`, `GetUser`, `CreateUser`, `UpdateUser`, `DeleteUser` |

## Related projects

| Project | Role |
|---|---|
| [redb.Tsak.Contracts](../redb.Tsak.Contracts/README.md) | DTO models for all responses |
| [redb.Tsak.CLI](../redb.Tsak.CLI/README.md) | CLI tool built on top of this client |
| [redb.Tsak.Core](../redb.Tsak.Core/README.md) | Server-side implementation of the REST API |
| [redb.Tsak](../../README.md) | Full project overview |
