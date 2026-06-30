# redb.Tsak.Web

Blazor Server dashboard for monitoring and managing **redb.Tsak** worker nodes.  
Provides real-time visibility into node health, route execution, contexts, watchdog alerts, and cluster topology.

## Operating Modes

The dashboard supports two deployment modes, controlled by a single configuration key:

| Mode | `Tsak:Web:Mode` | Database required | Description |
|------|-----------------|-------------------|-------------|
| **Standalone** | `"standalone"` (or omitted) | No | Connects to worker nodes directly via HTTP. Node list is defined in config. |
| **Cluster** | `"cluster"` | Yes | Discovers nodes dynamically from the shared EAV database. Supports multi-node topology, leader election, and group management. |

## Quick Start

```bash
dotnet run --project src/redb.Tsak.Web
```

Default URL: `http://localhost:5100`  
Default credentials: `admin` / `admin` (configurable)

---

## Configuration Reference

All settings live under the `Tsak:Web` section in `appsettings.json`.

### Authentication

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `AdminLogin` | `string` | — | Login for dashboard access. Required in both modes. |
| `AdminPassword` | `string` | — | Password for dashboard access. Required in both modes. |

In **standalone** mode, credentials are validated directly against these config values.  
In **cluster** mode, credentials are validated against users stored in the EAV database (seeded from config on first startup).

> **Important — auth durability:** the admin sign-in state lives **in-memory per Blazor circuit**.
> After a Web process restart (or a circuit reconnect that lost server state), all admins are
> logged out and must sign in again. Persistent sign-in (cookie / JWT bearer) is **not** on the
> current roadmap — Web is intended as an internal operator console, not a public portal.

### Standalone Mode

No database, no EAV — the dashboard talks to worker nodes over HTTP using `TsakApiClient`.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Mode` | `string` | `"standalone"` | Can be omitted — standalone is the default. |
| `Nodes` | `array` | — | List of worker nodes to connect to. |
| `Nodes[].Id` | `string` | `node-{n}` | Unique node identifier. Auto-generated if omitted. |
| `Nodes[].Url` | `string` | — | Worker API base URL (e.g. `http://localhost:9090`). **Required.** |
| `Nodes[].ApiKey` | `string` | — | API key for authenticating with the worker. Must match the worker's `Tsak:Api:Key`. |
| `StandaloneUrl` | `string` | `http://localhost:9090` | Legacy fallback — used only if `Nodes` array is absent. Single-node shortcut. |

**Example:**

```jsonc
{
  "Tsak": {
    "Web": {
      "AdminLogin": "admin",
      "AdminPassword": "admin",
      "Nodes": [
        { "Id": "worker-1", "Url": "http://localhost:9090", "ApiKey": "your_api_key" }
      ]
    }
  }
}
```

> **Note:** ConnectionStrings and `Tsak:Redb` sections are **not required** in standalone mode.

### Cluster Mode

Requires a shared database (Postgres or MSSQL). Node topology is discovered from the EAV store. Supports leader election, node groups, module assignments, and automatic dead-node detection.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Mode` | `string` | — | Must be `"cluster"`. |
| `ServiceApiKey` | `string` | — | API key for node-to-node communication. Must match workers' `Tsak:Api:Key`. |

**Additional sections required in cluster mode:**

| Section | Description |
|---------|-------------|
| `ConnectionStrings:Postgres` or `ConnectionStrings:MSSql` | Database connection string for EAV access. |
| `Tsak:Redb:Provider` | Database provider: `"postgres"` or `"mssql"`. |
| `Tsak:Redb:UsePro` | Must be `true` for cluster features. |
| `Tsak:Redb:License` | redb.Core.Pro / Tsak.Cluster license key(s). Single JWT, `\|`-separated string, or JSON array. |
| `Tsak:Redb:PropsSaveStrategy` | EAV save strategy (e.g. `"ChangeTracking"`). |

**Example:**

```jsonc
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=redb"
  },
  "Tsak": {
    "Redb": {
      "Provider": "postgres",
      "UsePro": true,
      "License": [
        "<jwt-core.pro+postgres.pro>",
        "<jwt-tsak.cluster>"
      ],
      "PropsSaveStrategy": "ChangeTracking"
    },
    "Web": {
      "AdminLogin": "admin",
      "AdminPassword": "admin",
      "Mode": "cluster",
      "ServiceApiKey": "your_api_key"
    }
  }
}
```

**License — multiple keys / env vars**

The `License` field accepts a single JWT, a `|`-separated string, or a JSON array. Each JWT covers
a fixed feature set, so cluster mode usually needs two: one for `core.pro` + provider, one for
`tsak.cluster`.

Environment variables (standard .NET convention, `:` → `__`):

```pwsh
$env:Tsak__Redb__License__0 = "<jwt1>"
$env:Tsak__Redb__License__1 = "<jwt2>"
# or single pipe-separated
$env:Tsak__Redb__License = "<jwt1>|<jwt2>"
```

Legacy fallback (read by `LicenseStore` directly): `REDB_PRO_LICENSE`, `TSAK_LICENSE` env vars
and `redb.license` / `tsak.license` files next to the executable. All sources merge into one
pool, deduplicated by `jti`.

### Logging (Serilog)

Logging is configured via the standard `Serilog` section. The dashboard uses [Serilog.AspNetCore](https://github.com/serilog/serilog-aspnetcore) with console and file sinks.

| Key | Description |
|-----|-------------|
| `Serilog:MinimumLevel:Default` | Global minimum level (`Information`, `Debug`, etc.) |
| `Serilog:MinimumLevel:Override:redb.Tsak.Web` | Override for dashboard-specific logs. |
| `Serilog:WriteTo` | Sink configuration (Console, File). |

Log files are written to `Logs/web-{Date}.txt` with 7-day retention by default.

### Path Base

For reverse-proxy deployments (e.g. behind nginx at `/dashboard`):

| Key | Type | Description |
|-----|------|-------------|
| `ASPNETCORE_PATHBASE` | `string` | Base path prefix. Can also be set as an environment variable. |

---

## Architecture

```
Program.cs
  ├── Register standalone defaults (always)
  │     ├── StandaloneClientProvider  → INodeClientProvider
  │     └── ConfigAuthService         → IAuthService
  │
  └── AddTsakWebPro()  (always called, mode check inside)
        └── if Mode == "cluster":
              ├── ConfigureRedb()           → EAV database access
              ├── ClusterClientProvider     → INodeClientProvider  (overrides standalone)
              └── RedbAuthService           → IAuthService         (overrides standalone)
```

The **always-included** pattern means `Program.cs` has zero conditional logic. The Pro assembly checks `Tsak:Web:Mode` internally and either overrides the standalone defaults or no-ops.

### Key Interfaces

| Interface | Standalone impl | Cluster impl | Description |
|-----------|----------------|--------------|-------------|
| `INodeClientProvider` | `StandaloneClientProvider` | `ClusterClientProvider` | Provides `TsakApiClient` instances for worker nodes. |
| `IAuthService` | `ConfigAuthService` | `RedbAuthService` | Dashboard authentication and authorization. |

### Project Dependencies

```
redb.Tsak.Web
  ├── redb.Tsak.Client        (HTTP client for worker API)
  ├── redb.Tsak.Contracts     (shared models: ClusterTopology, NodeInfo, NodeStatus)
  └── redb.Tsak.Web.Pro       (cluster-mode services, interfaces)
        ├── redb.Tsak.Client
        ├── redb.Tsak.Contracts
        └── redb.Tsak.Core.Pro (EAV, licensing, node registry)
```

### UI Features

| Page | Description | Mode |
|------|-------------|------|
| **Dashboard** | Node health overview, sparklines, route metrics, watchdog alerts | Both |
| **Cluster** | Group topology, node grid, leader info, module assignments | Cluster only |
| **Node Detail** | Per-node deep dive: contexts, routes, endpoints, modules, metrics, logs | Both |

### BFF Proxy

The dashboard exposes a single BFF endpoint for log file downloads:

```
GET /api/proxy/{nodeId}/logs/download/{filename}
```

This proxies the request to the appropriate worker node via `TsakApiClient`, so the browser doesn't need direct access to worker APIs.

---

## Switching Modes

To switch from cluster to standalone (or vice versa), edit `appsettings.json` and restart:

```jsonc
// Standalone — comment out cluster block, uncomment standalone
"Web": {
  "AdminLogin": "admin",
  "AdminPassword": "admin",
  "Nodes": [
    { "Id": "local", "Url": "http://localhost:9090", "ApiKey": "..." }
  ]
}

// Cluster — set Mode, remove Nodes
"Web": {
  "AdminLogin": "admin",
  "AdminPassword": "admin",
  "Mode": "cluster",
  "ServiceApiKey": "..."
}
```

No code changes, no recompilation — just config.
