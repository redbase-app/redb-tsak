# redb.Tsak

> **The runtime container for [redb.Route](https://github.com/redbase-app/redb-route) integration pipelines.**
> Deploy DLLs into a folder. Start, stop, reload them at runtime. Scale across nodes with leader election. Watch every message, every error, every restart — from a built-in REST API, a 30-command CLI, or a Blazor dashboard. Zero downtime. Production-tested.

> **LLMs / coding agents:** canonical training contract for the whole redb ecosystem → <https://redbase.app/llms.txt>

[![License: Apache 2.0](https://img.shields.io/badge/license-Apache_2.0-blue)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-purple)](https://dotnet.microsoft.com)
[![Tests](https://img.shields.io/badge/tests-351%20passing-brightgreen)](#testing)
[![Status](https://img.shields.io/badge/status-production-green)](#)
[![REST API](https://img.shields.io/badge/REST_API-32_endpoints-blue)](#rest-api)
[![CLI](https://img.shields.io/badge/CLI-30_commands-blue)](#cli)
[![Web UI](https://img.shields.io/badge/Web_UI-Blazor_Server-512BD4)](#web-dashboard)
[![Cluster](https://img.shields.io/badge/cluster-leader_election-orange)](#cluster-mode)

---

## What you get out of the box

| | |
|---|---|
| **Module-based deployment** | Drop a `.dll` into `Libs/` — Tsak loads it, builds an `IRouteContext`, starts the routes. Update the DLL — Tsak hot-swaps it without dropping a single in-flight message in other contexts. |
| **Three deployment modes** | `Standalone` (in-memory, no DB) · `Single-node + redb` (durable RTTI-storage state) · `Cluster` (leader election + automatic context redistribution across nodes). |
| **Three API modes** | `Full` (REST API + management) · `Headless` (zero ports, embedded use) · `Readonly` (only `GET` endpoints — perfect for monitoring sidecars). |
| **5-layer configuration** | Module ships defaults. DevOps overrides. Config hot-reloads. No code changes, no restarts. |
| **Built-in observability** | Per-process metrics (CPU/RAM/threads/GC, 12h history), per-route metrics (count/error rate/latency), ring-buffer logs, OpenTelemetry traces, optional Prometheus scrape. |
| **Watchdog** | Detects suspected and hung routes. Optionally auto-restarts them. |
| **Quartz scheduler** | Built-in `IScheduler` injected into every context. `RAMJobStore` standalone, `AdoJobStore` cluster-safe — schema auto-created on first start. |
| **Security** | API Key + HMAC-SHA256 + roles + expiry + revocation. Constant-time comparison. redb-backed key store. Optional user binding (disabled user → key dies). |
| **Module isolation** | Per-module `AssemblyLoadContext` — dependencies don't conflict between modules. |
| **Clients ready to ship** | Typed C# `ITsakApiClient`, 30-command `tsak` CLI with profiles and JSON output, Blazor Server dashboard. |

---
"redb.Tsak — .NET analogue of Apache Camel K".

## Why redb.Tsak exists

Most .NET teams either:

1. **Bake their integration code into a Worker Service** — every change means redeploy the entire process. Multiple unrelated pipelines fight in the same `Program.cs`.
2. **Buy a heavyweight enterprise ESB** — pay six figures for a UI, a runtime, a management API, and a deployment workflow they barely use.
3. **Roll their own management plane** — and re-invent metrics, hot-reload, leader election, and an admin API for the third time this decade.

Tsak is the missing piece between "a Worker Service" and "an enterprise ESB":

| | Hand-rolled Worker | Tsak | Heavy ESB |
|---|---|---|---|
| Deploy a new pipeline | Redeploy whole process | `cp module.dll Libs/` (hot-reload) | Vendor wizard, hours |
| Stop one pipeline without affecting others | Custom code | `tsak context stop orders` | Vendor UI |
| Distribute pipelines across nodes | Custom coordinator | Built-in leader election + auto-rebalance | Vendor cluster |
| REST API for ops/CI | Build it yourself | 32 endpoints, typed client | Yes, vendor-locked |
| CLI for CI/CD | Build it yourself | 30 commands, profiles, JSON output | Maybe |
| Web dashboard | Build it yourself | Blazor Server, 10 pages | Yes, vendor-locked |
| Cost | Engineering time | Apache 2.0, free | $$$$ + lock-in |
| Routing engine | DIY or MassTransit | **redb.Route** — 22 transports, 24 EIP processors, fluent DSL | Vendor's DSL |

---

## For redb.Route users

If you already write `RouteBuilder` classes for [redb.Route](https://github.com/redbase-app/redb-route), Tsak is what runs them in production. You do not change a single line of route code.

```
┌──────────────────────┐         ┌──────────────────────┐         ┌──────────────────────┐
│   Your routes        │  --->   │   class library      │  --->   │  Tsak picks it up    │
│   (RouteBuilder)     │ publish │   (Orders.dll)       │   cp    │  hot-reload + run    │
└──────────────────────┘         └──────────────────────┘         └──────────────────────┘
```

What Tsak adds **on top** of a plain `redb.Route` worker:

| Pure `redb.Route` worker (`Host.CreateApplicationBuilder`) | `redb.Route` inside Tsak |
|---|---|
| One `Program.cs` wires every `RouteBuilder` at compile time. | Drop the DLL into `Libs/` — Tsak loads it. |
| Add a route → redeploy the whole process. | Hot-reload one module without touching the others. |
| One process = one bag of routes. | Multiple **named contexts**, each with its own properties, lifecycle and assembly load context. |
| Stop one route = stop the process. | `tsak route stop <ctx> <id>` or `POST /api/routes/{id}/stop`. |
| Distribute across nodes = roll your own coordinator. | Built-in leader election + per-context assignment across cluster nodes. |
| Schedule a job = wire Quartz yourself in `Program.cs`. | `IScheduler` injected into every context, schema auto-bootstrapped. |
| Operate it = parse logs, build a dashboard, expose metrics yourself. | REST + CLI + Blazor + Prometheus + OTel out of the box. |

A `RouteBuilder` you wrote against `redb.Route`'s plain `IHostedService` runs unchanged inside a Tsak module — the same `Configure()` is called, the same `IExchange` flows through, the same `OnException` and `.Transacted()` semantics apply. Tsak only owns **how** the routes are loaded, scheduled, observed and torn down.

```csharp
// MyRoutes/InitRoute.cs — the only Tsak-specific piece
public static class InitRoute
{
    public static IRouteContext main(IRouteContext context)
    {
        // Plain redb.Route — exactly what you would write outside Tsak
        ((RouteContext)context).AddRoutes(new OrderRoutes());
        ((RouteContext)context).AddRoutes(new ShipmentRoutes());
        return context;
    }
}
```

For the routing DSL itself (transports, EIP patterns, expressions, transactional pipelines, OnException, error handling), see the [redb.Route README](https://github.com/redbase-app/redb-route). Everything below in this document is about the **container**. redb.Route now includes an Amazon **SQS + SNS** connector (`redb.Route.Sqs`) and a **Telegram Bot** connector (`redb.Route.Telegram`) — both bundled into the Tsak distribution at release.

---

## Architecture

### Process layout

```
┌────────────────────────────────────────────────────────────────────┐
│                        redb.Tsak.Worker                            │
│                                                                    │
│  ┌──────────────────────────────────────────────────────────────┐ │
│  │  _system context (protected, cannot be stopped or removed)   │ │
│  │  ────────────────────────────────────────────────────────── │ │
│  │  HTTP listener  →  AuthMiddleware  →  ControllerDispatcher   │ │
│  │  /api/auth        Bearer / X-Api-Key   AuthController         │ │
│  │  /api/contexts    role check           ContextsController     │ │
│  │  /api/modules                          ModulesController      │ │
│  │  /api/cluster                          ClusterController      │ │
│  │  /api/system                           SystemController       │ │
│  │  /api/scheduler                        SchedulerController    │ │
│  │  /api/logs                             LogsController         │ │
│  │  /api/users                            UsersController        │ │
│  │  /api/watchdog  /api/diagnostics  /api/lifecycle  /api/routes │ │
│  └──────────────────────────────────────────────────────────────┘ │
│                                                                    │
│  ┌────────────────┐  ┌────────────────┐  ┌────────────────┐      │
│  │   Context A    │  │   Context B    │  │   Context C    │      │
│  │   "orders"     │  │   "payments"   │  │   "analytics"  │      │
│  │ ────────────── │  │ ────────────── │  │ ────────────── │      │
│  │ Module:        │  │ Module:        │  │ Modules:       │      │
│  │  Api.Orders    │  │  Pay.Stripe    │  │  Etl.Loader    │      │
│  │ Route 1: Kafka │  │ Route 1: HTTP  │  │  Etl.Reports   │      │
│  │ Route 2: Cron  │  │ Route 2: AMQP  │  │ Route 1..N     │      │
│  │ Route N: HTTP  │  │ Route N: Mail  │  │                │      │
│  │ ALC: isolated  │  │ ALC: isolated  │  │ ALC: isolated  │      │
│  └────────────────┘  └────────────────┘  └────────────────┘      │
│                                                                    │
│  ┌──────────────────────────────────────────────────────────────┐ │
│  │  Infrastructure services                                      │ │
│  │  ────────────────────────────────────────────────────────── │ │
│  │  HotReloadService   ClusterCoordinator    QuartzScheduler    │ │
│  │  ModuleRegistry     LeaderElection        WatchdogService    │ │
│  │  ContextManager     NodeRegistry          MetricsService     │ │
│  │  ConfigMerger       AssignmentManager     LogRingBuffer      │ │
│  │                                            HealthCheckService │ │
│  └──────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼
                ┌─────────────────────────────────┐
                │  redb (Postgres or MSSQL)   │
                │  - API keys (RedbApiKeyStore)   │
                │  - Cluster topology (Tree)      │
                │  - Lifecycle events             │
                │  - Idempotent state             │
                │  - Quartz QRTZ_* tables         │
                └─────────────────────────────────┘
```

### Anatomy of a request

```
External call:  curl -H "Authorization: Bearer $KEY" http://node:9090/api/contexts/orders/start
       │
       ▼
HTTP listener (Kestrel) inside _system context — port 9090
       │
       ▼
HeaderBridge processor      → normalizes route.path = "contexts/orders/start", route.method = "POST"
       │
       ▼
AuthorizeProcessor          → resolves API key (5min cache) → validates role → enriches Exchange with claims
       │
       ▼
ControllerDispatcher        → looks up [Route("contexts/{name}/start")] on ContextsController
       │
       ▼
ContextsController.Start    → ITsakContextManager.StartAsync("orders")
       │
       ▼
                            → graceful start: load assemblies → wire routes → connect transports → mark Running
       │
       ▼
JSON response               → { "name": "orders", "status": "Running", "startedAt": "..." }
```

The same `ContextsController` can also be invoked over **RabbitMQ RPC**, **gRPC**, **SignalR**, or any other `redb.Route` `InOut` transport — the dispatcher is transport-agnostic. This is how Tsak avoids tying its management API to HTTP-only.

---

## Project structure

```
redb.Tsak/
├── src/
│   ├── redb.Tsak.Core/          Kernel: contracts, controllers, services,
│   │                            security, cluster, hot-reload, monitoring
│   ├── redb.Tsak.Core.Pro/      Pro extensions (cluster, hooks, monitoring)
│   ├── redb.Tsak.Worker/        Hosted process: DI wiring, Serilog,
│   │                            Quartz, appsettings.json, Dockerfile
│   ├── redb.Tsak.Contracts/     Wire DTOs (System.Text.Json only) shared
│   │                            between Core, Client, Web, CLI
│   ├── redb.Tsak.Client/        ITsakApiClient + TsakApiClient (HTTP)
│   ├── redb.Tsak.CLI/           tsak — 30 commands, profiles, JSON output
│   ├── redb.Tsak.Web/           Blazor Server dashboard, custom design system
│   └── redb.Tsak.Web.Pro/       Pro web extensions (auth, node providers)
├── tests/
│   ├── redb.Tsak.Tests/         287 unit + integration tests
│   └── redb.Tsak.CLI.Tests/     64 CLI command tests
├── docs/
│   ├── PLAN.md                  Architecture reference
│   ├── CONFIG_GUIDE.md          5-layer configuration deep-dive
│   ├── LT_TSAK_ANALYSIS.md      Analysis of the original lt.tsak
│   ├── ENTERPRISE_AUDIT.md
│   └── phases/                  Per-phase implementation notes (0..8B)
├── README.md                    This file
├── CHANGELOG.md                 Per-version changes
├── CONTRIBUTING.md              Contribution guide
├── SECURITY.md                  Security policy
├── DEPLOYMENT_SECRETS.md        Secrets management for production
└── LICENSE                      Apache 2.0
```

---

## Quick start

### Option A — Docker (fastest)

Pre-built images are published to GitHub Container Registry — no build step, no .NET SDK required:

```bash
# Worker only (REST API + cluster, no UI)
docker run -d --name tsak \
  -p 9090:9090 \
  -v $PWD/Libs:/app/Libs \
  ghcr.io/redbase-app/redb-tsak-worker:latest

# Stack (Worker + Web UI in one container, like rabbitmq:management)
docker run -d --name tsak \
  -p 9090:9090 -p 8085:8085 \
  -v $PWD/Libs:/app/worker/Libs \
  ghcr.io/redbase-app/redb-tsak-stack:latest
# UI: http://localhost:8085  ·  REST: http://localhost:9090
```

| Image | Contains | Size | Ports |
|---|---|---|---|
| `ghcr.io/redbase-app/redb-tsak-worker` | Worker (REST + cluster) | ~360 MB | `9090` |
| `ghcr.io/redbase-app/redb-tsak-web`    | Standalone Web UI       | ~250 MB | `8085` |
| `ghcr.io/redbase-app/redb-tsak-stack`  | Worker + Web (supervisord) | ~430 MB | `9090`, `8085` |

Tags: `latest`, `<version>` (e.g. `2.0.0`), `<version>-net9` (Worker also `-net8`, `-net10`).

**With PostgreSQL (durable RTTI-storage state, multi-node, cluster):**

```bash
docker run -d --name tsak \
  -p 9090:9090 -p 8085:8085 \
  -v $PWD/Libs:/app/worker/Libs \
  -e ConnectionStrings__Postgres="Host=pg;Port=5432;Database=redb;Username=postgres;Password=postgres" \
  ghcr.io/redbase-app/redb-tsak-stack:latest
```

Ready-to-use compose files (worker / web / stack / full-with-postgres) live in [`publish/docker/`](publish/docker/).

### Option B — Standalone archive (no Docker)

Self-contained archives (no .NET runtime required on host) are attached to every [GitHub Release](https://github.com/redbase-app/redb-tsak/releases):

| File | Platform |
|---|---|
| `redb-tsak-<version>-linux-x64.tar.gz` | Linux x64 |
| `redb-tsak-<version>-win-x64.zip`      | Windows x64 |

Each archive bundles `worker/`, `web/`, `cli/`, all 20 Route connectors in `worker/Libs/shared/`, and startup scripts (`start-worker.sh`/`.bat`/`.ps1`, same for web and stack).

```bash
curl -LO https://github.com/redbase-app/redb-tsak/releases/latest/download/redb-tsak-2.0.2-linux-x64.tar.gz
tar xzf redb-tsak-2.0.2-linux-x64.tar.gz
cd redb-tsak-2.0.2-linux-x64
./scripts/start-stack.sh   # worker on :9090, web on :8085
```

### Verifying signatures (recommended)

All images and archives are signed with [cosign](https://docs.sigstore.dev/cosign/installation/). Public key: [`publish/keys/cosign.pub`](publish/keys/cosign.pub).

```bash
# Image
cosign verify --key cosign.pub ghcr.io/redbase-app/redb-tsak-worker:2.0.2

# Archive
cosign verify-blob --key cosign.pub \
  --bundle redb-tsak-2.0.2-linux-x64.tar.gz.bundle \
  redb-tsak-2.0.2-linux-x64.tar.gz
```

SHA256 sums for every artifact are in `checksums.txt` on the release page.

### Option C — Build from source

### 1. Run a Tsak worker (no database, in-memory)

```bash
cd redb.Tsak/src/redb.Tsak.Worker
dotnet run
```

```
[INF] redb.Tsak.Worker starting…
[INF] Storage: InMemory
[INF] Cluster: disabled
[INF] HotReload: enabled (scan every 10s)
[INF] HTTP API listening on http://0.0.0.0:9090
[INF] _system context started
[INF] Discovered 0 modules in Libs/
[INF] Ready
```

```bash
curl http://localhost:9090/api/system/health
# { "status": "Healthy", "checks": [...] }
```

### 2. Deploy your first module

A Tsak module is a plain .NET class library exposing one of two well-defined entry-point shapes: a `public static class InitRoute` with `public static IRouteContext main(IRouteContext ctx)` (Apache Camel-style convention, shown below), or a concrete public type implementing `ITsakModule`. Inside the entry point you wire up your `RouteBuilder` subclasses against the supplied `IRouteContext`.

```csharp
// MyRoutes/InitRoute.cs
public static class InitRoute
{
    public static IRouteContext main(IRouteContext context)
    {
        ((RouteContext)context).AddRoutes(new OrderRoutes());
        return context;
    }
}

// MyRoutes/OrderRoutes.cs
public class OrderRoutes : RouteBuilder
{
    protected override void Configure()
    {
        From("kafka://orders?brokers=localhost:9092&groupId=svc")
            .Filter(e => e.Message.GetHeader<string>("type") == "new")
            .Process(async (e, ct) => await ProcessAsync(e, ct))
            .To("rabbitmq://processed?host=localhost");
    }
}
```

```bash
# Build and deploy
dotnet publish MyRoutes -c Release -o publish/
cp -r publish/* /opt/tsak/Libs/MyRoutes/

# Tsak picks it up within HotReload:ScanIntervalSeconds (default 10s)
```

```bash
tsak context list
# Name      Status   Routes  Endpoints  Modules
# _system   Running       11          1   (system)
# MyRoutes  Running        1          2   MyRoutes
```

### 3. Manage from the CLI

```bash
tsak login http://localhost:9090 --key $TSAK_KEY
tsak health
tsak context restart MyRoutes
tsak logs --level Error --limit 50
tsak metrics
tsak scheduler jobs
tsak cluster overview
```

### 4. Open the dashboard

```bash
cd redb.Tsak/src/redb.Tsak.Web
dotnet run
# Browse to http://localhost:5100
```

---

## Module workflow

Tsak supports **two equivalent deployment formats** under `Libs/`. Both are scanned by `HotReloadService`, hot-reloaded on file change, gracefully unloaded on file removal — pick whichever fits your workflow.

| Format | What it is | When to pick it |
|---|---|---|
| **Bare directory** | A folder of loose `*.dll` files (+ optional config) | Local dev, fast inner loop, atomic file replace via `cp -r` |
| **`.tpkg` package** | A single ZIP archive bundling manifest + DLLs + config | CI/CD, immutable artifacts, one-file deploys, atomic rollback |

### What Tsak actually loads

Tsak does **not** load arbitrary .NET DLLs. Each candidate assembly is scanned for one of two well-defined module shapes:

1. A concrete public type implementing `ITsakModule`.
2. A public static class named `InitRoute` exposing a `static IRouteContext main(IRouteContext ctx)` method (Apache Camel-style entry point convention).

If neither shape is found, the assembly is **classified as a dependency, not a module**:

- It is registered in `LoadedAssemblyTracker` so that other modules in the process can resolve it as a transitive dependency (same `Assembly` instance everywhere — no type-identity drift across ALCs).
- Its file timestamp is recorded in an internal "ignored" set; subsequent scans skip it unless the file is replaced with a newer version (which triggers a re-scan in case the new bits *do* contain a module).

So the rule is: **random DLLs are not loaded as modules — but DLLs that other modules depend on are loaded and made available, just not started as routes.** This keeps the runtime predictable and prevents random vendor SDKs sitting in `Libs/` from being treated as deployable units.

### Format 1 — Bare directory layout

```
Libs/
└── Orders/
    ├── Orders.dll                  ← module entry point (contains ITsakModule or InitRoute.main)
    ├── Orders.deps.json            ← dependency graph (from dotnet publish)
    ├── Orders.Domain.dll           ← companion dependency (no module → loaded as dep only)
    ├── Newtonsoft.Json.dll         ← any third-party dep that isn't already in the host
    ├── context.json                ← infrastructure defaults (Layer 3)  — optional
    └── Orders.config.json          ← module business settings (Layer 4) — optional
```

How the loader treats each file:

| File | Role |
|---|---|
| `Orders.dll` | Loaded into a per-module isolated `ModuleAssemblyLoadContext`, scanned for `ITsakModule`/`InitRoute.main`, registered as a module |
| `Orders.Domain.dll` | Scanned, no module shape found → kept as a shared dependency (resolvable by other ALCs) |
| `*.deps.json` | Used by `dotnet publish` to record the full dep graph (Tsak does not parse it directly, but it is what makes `CopyLocalLockFileAssemblies=true` work) |
| `context.json` | Layer 3 of the [5-layer config merge](#5-layer-configuration) |
| `{Module}.config.json` | Layer 4 of the same merge |

### Format 2 — `.tpkg` package (atomic, immutable, isolated)

A `.tpkg` is a plain ZIP archive bundling everything a module needs into a single file. Drop one file into `Libs/`, Tsak picks it up and loads it as one atomic unit. Replace or delete that one file, the whole bundle reloads or shuts down together — no half-state in the middle.

```
Orders.tpkg                          (ZIP archive, flat structure)
├── manifest.json                    ← REQUIRED — package metadata
├── Orders.dll                       ← entry point (declared in manifest.EntryPoints)
├── Orders.config.json               ← optional Layer 4 config
├── Orders.Domain.dll                ← companion dependency (loaded into shared tracker)
├── Orders.Models.dll                ← companion dependency
└── Newtonsoft.Json.dll              ← any other transitive dep not provided by the host
```

`manifest.json` schema (matches `redb.Tsak.Core.Modules.ModuleManifest`):

```json
{
  "Name": "Orders",
  "Version": "1.0.0",
  "EntryPoints": [ "Orders.dll" ],
  "Dependencies": []
}
```

| Field | Meaning |
|---|---|
| `Name` | Logical package name. Must be unique across all packages loaded in the process. |
| `Version` | Informational version string used in logs and diagnostics. |
| `EntryPoints` | List of DLL filenames inside the archive that contain `ITsakModule` or `InitRoute.main`. Each entry is loaded into the **package's isolated ALC** and scanned for modules. |
| `Dependencies` | Informational list of other packages this one logically depends on (used in diagnostics; not enforced by the loader). |

A single `.tpkg` may declare **multiple entry points** — for example a `Core` module and an `Api` module that ship together and share a private dependency tree:

```json
{
  "Name": "tsum",
  "Version": "1.0.0",
  "EntryPoints": [ "tsum.Core.dll", "tsum.Api.dll" ],
  "Dependencies": []
}
```

Both entry-point DLLs land in the same isolated ALC, so they share static state and can pass non-public types between each other without serialization.

### Isolation model — what is shared, what is not

Tsak runs many modules in one .NET process without letting them collide. The isolation model has three layers:

| Layer | What lives here | Sharing semantics |
|---|---|---|
| **Default ALC** (host) | Tsak Worker, redb.Core, redb.Route abstractions, all NuGet deps the host ships with | Singleton — all modules see the same `IRouteContext`, `IRedbService`, etc. type identity. This is what makes interfaces work across the boundary. |
| **`LoadedAssemblyTracker`** (shared dependency layer in Default ALC) | Companion DLLs from `.tpkg` files, bare-DLL non-module dependencies | One `Assembly` instance per assembly name across the whole process. If two packages each ship `MyCommon.dll` v1.5, the second one **reuses** the first load (last-loaded-wins on `forceReload=true` for hot updates). |
| **Per-package `ModuleAssemblyLoadContext`** | Entry-point DLLs declared in a package's `manifest.json` | **Isolated.** Each package gets its own ALC. Two packages can ship two different versions of the same entry-point assembly without conflict. Resolving a missing reference falls through: package ALC → tracker → host Default ALC. |

Concretely, this means:

- **Host contracts (`IRouteContext`, `ITsakModule`, `IRedbService`, …) always resolve to the host's Default ALC.** A module never accidentally rebinds them to a private copy.
- **Companion DLLs are de-duplicated.** If your package ships `Microsoft.Extensions.Logging.Abstractions.dll` and the host already has it, the host wins. If the host doesn't have it, the first package to ship it wins for the whole process.
- **Entry-point code is fully isolated per package.** Module A cannot reach into Module B's static fields, even if they both extend the same base class — different ALC, different `Type` instance.
- **One bad package does not poison the host.** A type-load failure in `Orders.tpkg` is logged and `Orders.tpkg` is skipped; every other package keeps running.

### How Tsak loads a module

1. `HotReloadService` scans every directory under `Tsak:Modules:AssemblyPaths` for `*.dll` and `*.tpkg`.
2. For each file the loader checks `LastWriteTimeUtc` against its known snapshot — unchanged files are skipped instantly.
3. **For a `.tpkg`**: the manifest is read, **companion DLLs load first** (so dependency resolution works), then **entry-point DLLs load into a fresh per-package ALC**.
4. **For a bare `.dll`**: the file loads into a fresh per-module ALC. If `ITsakModule`/`InitRoute.main` is found → it becomes a module; otherwise it is registered as a shared dependency and remembered as "not a module".
5. `ConfigMerger` deep-merges all 5 config layers into a single `IDictionary<string, object?>` and exposes it as the context's property bag.
6. `TsakContextManager` creates an `IRouteContext`, registers Quartz `IScheduler`, and either invokes `InitRoute.main(ctx)` or instantiates the `ITsakModule`.
7. The context starts (if `AutoStart = true`): transports connect, consumers begin reading, routes go live.

### Hot-swap

Replace the artifact on disk — that is the whole API.

```bash
# Bare directory: atomic rsync over existing files
rsync -a ./publish/ /opt/tsak/Libs/Orders/

# .tpkg: replace the single file
cp ./output/Orders.tpkg /opt/tsak/Libs/
```

`HotReloadService` detects the timestamp change, performs a **graceful swap** (start new ALC → wait for it to settle → drain old context's in-flight exchanges → stop old context → optionally unload old ALC). With `Cluster:Enabled = true` and `HotReload:RollingUpdate = true`, nodes update sequentially — there is **never a moment when zero nodes are running the new version**, and **never a moment when in-flight messages are dropped**.

### Graceful shutdown when an artifact is removed

Deleting a file from `Libs/` is a first-class deployment operation, not an error condition. The runtime treats it as an explicit "stop and unload" command:

| You do this | Tsak does this |
|---|---|
| `rm Libs/Orders.tpkg` | After `RemovalDebounceScans` confirm the file is gone, **all modules from that package are unloaded atomically**: each context stops gracefully (drain in-flight, close transports, close DB connections), the package's isolated ALC is disposed. Other packages keep running. |
| `rm Libs/Orders/Orders.dll` | Same flow scoped to one bare-DLL module: graceful context stop → unregister module → dispose its ALC. |
| `rm -rf Libs/Orders/` | Same as above for every module file in the directory. |

The debounce (`HotReload:RemovalDebounceScans`, default `2` scans) protects against false positives during atomic file replacements where a deployment briefly removes the file before writing the new one.

### Hot-reload knobs

| Key | Default | Behavior |
|---|---|---|
| `Tsak:Modules:AssemblyPaths` | `["Libs"]` | Directories to scan for `*.dll` and `*.tpkg` files. |
| `HotReload:ScanIntervalSeconds` | `10` | How often to scan the configured paths. |
| `HotReload:KeepVersions` | `2` | Old versions kept in memory for one-command rollback (bare-DLL flow). |
| `HotReload:StartupTimeoutSeconds` | `60` | Wait time for the new version to settle before retiring the old one. |
| `HotReload:RollingUpdate` | `true` | In a cluster, update nodes sequentially. |
| `HotReload:Collectible` | `false` | Enable `AssemblyLoadContext.Unload()` for full GC reclamation. **Do not enable** if your modules use `Reflection.Emit` (XmlSerializer, source-gen serializers, compiled regex) — set to `false` (default) and accept that old ALCs stay in memory until process restart. The number of accumulated non-collectible ALCs is exposed as `LeakedAlcCount` for monitoring. |
| `HotReload:RemovalDebounceScans` | `2` | Number of consecutive scan cycles a missing file must persist before its module is unloaded — protects against false positives during atomic file replacement. |

### Building a `.tpkg`

Two production-tested approaches ship with the repo. Pick whichever matches your build pipeline.

#### Option A — MSBuild target inside the `.csproj`

Put packing inside the build itself. After every `dotnet build` the module is repackaged and dropped into the local Tsak `Libs/` for instant hot-reload. This is the pattern used by [redb.Route.Demo](https://github.com/redbase-app/redb-route/tree/main/redb.Route.Demo):

```xml
<PropertyGroup>
  <!-- Required: ensures NuGet PackageReference DLLs land in bin/ for class libraries -->
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>

  <TsakModuleName>Orders</TsakModuleName>
  <TsakLibsDir>$(MSBuildThisFileDirectory)..\redb.Tsak\src\redb.Tsak.Worker\Libs</TsakLibsDir>
</PropertyGroup>

<Target Name="PackTpkg" AfterTargets="Build">
  <PropertyGroup>
    <_TpkgStaging>$(IntermediateOutputPath)tpkg</_TpkgStaging>
    <_TpkgFile>$(MSBuildThisFileDirectory)output\$(TsakModuleName).tpkg</_TpkgFile>
  </PropertyGroup>

  <RemoveDir Directories="$(_TpkgStaging)" />
  <MakeDir Directories="$(_TpkgStaging);$(MSBuildThisFileDirectory)output" />

  <Copy SourceFiles="$(MSBuildThisFileDirectory)manifest.json"
        DestinationFolder="$(_TpkgStaging)" />
  <Copy SourceFiles="$(TargetPath)"
        DestinationFolder="$(_TpkgStaging)" />
  <Copy SourceFiles="$(MSBuildThisFileDirectory)$(TsakModuleName).config.json"
        DestinationFolder="$(_TpkgStaging)"
        Condition="Exists('$(MSBuildThisFileDirectory)$(TsakModuleName).config.json')" />

  <ZipDirectory SourceDirectory="$(_TpkgStaging)"
                DestinationFile="$(_TpkgFile)" Overwrite="true" />

  <MakeDir Directories="$(TsakLibsDir)" Condition="!Exists('$(TsakLibsDir)')" />
  <Copy SourceFiles="$(_TpkgFile)" DestinationFolder="$(TsakLibsDir)" SkipUnchangedFiles="false" />
  <Touch Files="$(TsakLibsDir)\$(TsakModuleName).tpkg" />

  <Message Importance="high" Text="Packed $(TsakModuleName) → $(_TpkgFile)" />
</Target>
```

The `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` line is critical for class-library SDK projects — without it, NuGet `PackageReference` assemblies are not copied to `bin/` and your `.tpkg` ships with missing transitive dependencies.

The final `<Touch>` updates `LastWriteTimeUtc` on the copy, which guarantees `HotReloadService` notices the change even when the new file's content hash matches a recently-loaded version.

#### Option B — Standalone PowerShell script

Best when you want to package multiple projects together as one `.tpkg` (e.g. a `Core` + `Api` pair sharing private dependencies), or when you want fine-grained control over which DLLs to include. Pattern from a real production module that bundles two entry-point projects plus three companions into one `tsum.tpkg`:

```powershell
param(
    [ValidateSet("Debug","Release")] [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"

$tfm     = "net9.0"
$pkgName = "tsum"
$staging = Join-Path $env:TEMP "${pkgName}_tpkg_staging"
$tpkg    = Join-Path $PSScriptRoot "output\$pkgName.tpkg"
$libs    = "..\redb.Tsak\src\redb.Tsak.Worker\Libs"

# 1. Build every project that contributes to the package
dotnet build .\tsum.Core\tsum.Core.csproj -c $Configuration --nologo
dotnet build .\tsum.Api\tsum.Api.csproj   -c $Configuration --nologo

# 2. Stage manifest, entry points, companion DLLs, configs
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Path $staging | Out-Null

Copy-Item .\tsum.Core\manifest.json $staging
Copy-Item ".\tsum.Core\bin\$Configuration\$tfm\tsum.Core.dll" $staging   # entry point
Copy-Item ".\tsum.Api\bin\$Configuration\$tfm\tsum.Api.dll"   $staging   # entry point
Copy-Item ".\tsum.Core\bin\$Configuration\$tfm\tsum.Models.dll" $staging # companion
Copy-Item ".\tsum.Core\bin\$Configuration\$tfm\tsum.Domain.dll" $staging # companion
Copy-Item ".\tsum.Core\tsum.Core.config.json" $staging -ErrorAction SilentlyContinue
Copy-Item ".\tsum.Api\tsum.Api.config.json"   $staging -ErrorAction SilentlyContinue

# 3. Zip and deploy
if (Test-Path $tpkg) { Remove-Item $tpkg -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $tpkg)

Copy-Item $tpkg $libs -Force
(Get-Item (Join-Path $libs "$pkgName.tpkg")).LastWriteTime = Get-Date  # trigger hot-reload
Remove-Item -Recurse -Force $staging
```

The corresponding `manifest.json` declares both entry points so Tsak loads each one as a separately-managed module inside the shared package ALC:

```json
{
  "Name": "tsum",
  "Version": "1.0.0",
  "EntryPoints": [ "tsum.Core.dll", "tsum.Api.dll" ],
  "Dependencies": []
}
```

#### Option C — Plain ZIP (any tool, any pipeline)

A `.tpkg` is just a ZIP. Any tool that can write a ZIP can produce one:

```powershell
Compress-Archive -Path manifest.json,Orders.dll,Orders.Domain.dll,Orders.config.json `
                 -DestinationPath Orders.zip -Force
Move-Item Orders.zip Orders.tpkg -Force
```

Or `zip -j Orders.tpkg manifest.json *.dll *.config.json` on Linux.

#### Excluding host-provided DLLs (recommended for shipping)

Tsak Worker already loads dozens of assemblies (`redb.Core`, `redb.Route.*`, every transport it ships, Quartz, Serilog, ASP.NET Core). Repacking those into every `.tpkg` wastes disk, bloats the archive, and occasionally causes version skew if your module references a different version than the host.

The recommended pattern is: build an exclude set from `redb.Tsak.Worker\bin\<tfm>\*.dll` and skip any DLL in that set when staging — except for a small force-include list of packages you know are version-incompatible with the host's copy.

```powershell
$hostBin   = "..\redb.Tsak\src\redb.Tsak.Worker\bin\$Configuration\$tfm"
$exclude   = New-Object 'System.Collections.Generic.HashSet[string]' (
                 [System.StringComparer]::OrdinalIgnoreCase)
Get-ChildItem "$hostBin\*.dll" | ForEach-Object { [void]$exclude.Add($_.Name) }

# Always include these even if the host ships an older copy (binary-incompatible)
$forceInclude = @( 'Microsoft.IdentityModel.*.dll' )

Get-ChildItem ".\bin\$Configuration\$tfm\*.dll" | ForEach-Object {
    $force = $false
    foreach ($pat in $forceInclude) { if ($_.Name -like $pat) { $force = $true; break } }
    if ($force -or -not $exclude.Contains($_.Name)) {
        Copy-Item $_.FullName $staging
    }
}
```

The module's per-package ALC then resolves a force-included DLL from the package itself before falling through to the host's older copy in the Default ALC.

---

## 5-layer configuration

The most powerful feature in Tsak's configuration model: deep merge across five layers, with predictable precedence.

```
Layer 1: Tsak:Contexts:default              ← base for all contexts (lowest priority)
Layer 2: Tsak:Contexts:{name}               ← named context overrides
Layer 3: Libs/{Module}/context.json         ← module infrastructure defaults
Layer 4: Libs/{Module}/{Module}.config.json ← module business settings
Layer 5: Tsak:Contexts:{name}:Override      ← DevOps final word (highest priority)
```

Later layers **deep-merge** over earlier layers. Nested objects merge recursively — they do not replace each other wholesale.

### Worked example

```json
// appsettings.json
{
  "Tsak": {
    "Contexts": {
      "default": {
        "AutoStart": true,
        "RabbitMQ": { "Host": "localhost", "Port": 5672 }
      },
      "orders": {
        "Modules": ["Orders"],
        "RabbitMQ": { "Host": "rabbit-orders.local" },
        "Override": {
          "RabbitMQ": { "Password": "from-secret-manager" }
        }
      }
    }
  }
}
```

```json
// Libs/Orders/context.json
{ "RabbitMQ": { "Vhost": "/orders", "Username": "orders-svc" } }
```

```json
// Libs/Orders/Orders.config.json
{ "MaxRetries": 10 }
```

**Effective configuration for the `orders` context:**

```json
{
  "AutoStart": true,
  "MaxRetries": 10,
  "RabbitMQ": {
    "Host": "rabbit-orders.local",       // from Layer 2 (overrode Layer 1 "localhost")
    "Port": 5672,                         // from Layer 1 (preserved through merge)
    "Vhost": "/orders",                   // from Layer 3 (module ships default)
    "Username": "orders-svc",             // from Layer 3
    "Password": "from-secret-manager"     // from Layer 5 (DevOps wins)
  }
}
```

### Hot-reload of config

Edit `context.json` or `{Module}.config.json` while Tsak is running. The hot-reload scan re-merges all five layers and restarts the affected context with the new properties. **No worker restart required.**

### Named vs anonymous contexts

- **Named** — explicitly defined in `Tsak:Contexts:{name}` with a `Modules` array. Multiple modules share one `IRouteContext`.
- **Anonymous** — for any module not assigned to a named context. One module = one context, named after the module.

```json
{
  "Tsak": { "Contexts": {
    "api": { "Modules": ["Api.Orders", "Api.Catalog"], "AutoStart": true }
  }}
}
```

`Api.Orders` and `Api.Catalog` share the `api` context (and its property bag). Any other module gets its own anonymous context.

Full reference: [CONFIG_GUIDE.md](CONFIG_GUIDE.md).

---

## REST API

32 endpoints organized into 12 controllers. Every endpoint speaks JSON. Auth is opt-in (`Tsak:Auth:Enabled`) — when enabled, all endpoints require an API key except the auth-exempt health probes under `/api/health/*` (configurable via `Tsak:Api:AuthExempt`).

### Controllers

| Group | Endpoints | Purpose |
|---|---:|---|
| `/api/auth` | 3 | Create / list / revoke API keys |
| `/api/users` | 5 | User CRUD (Pro feature, redb-backed) |
| `/api/contexts` | 7 | List / get / start / stop / restart / reset-route-states / remove |
| `/api/routes` | 6 | List / get / start / stop / force-stop / inflight per route |
| `/api/modules` | 3 | List / get / remove loaded modules |
| `/api/cluster` | 4 | Status / nodes / rebalance / remove-node |
| `/api/system` | 4 | Health / metrics / metrics-history / info |
| `/api/scheduler` | 9 | Status / jobs / running / start / standby / pause-job / resume-job / fire-job |
| `/api/watchdog` | 2 | State / alerts |
| `/api/lifecycle` | 1 | Recent lifecycle events (filtered) |
| `/api/diagnostics` | 2 | Dump (cluster-wide) / route-level diagnostics |
| `/api/logs` | 3 | Tail / list-files / download-file |

### Sample calls

```bash
# Authenticate
KEY="$(tsak auth keys create --name ci --roles admin --output json | jq -r .rawKey)"

# Health
curl -s http://localhost:9090/api/system/health | jq
# {
#   "status": "Healthy",
#   "checks": [
#     { "name": "Contexts", "status": "Healthy", "data": { "running": 4, "failed": 0 } },
#     { "name": "Memory", "status": "Healthy", "data": { "workingSetMB": 184 } },
#     { "name": "Cluster", "status": "Healthy", "data": { "leader": "node-1", "epoch": 7 } }
#   ]
# }

# Start a context
curl -s -X POST -H "Authorization: Bearer $KEY" \
  http://localhost:9090/api/contexts/orders/start | jq

# Live in-flight exchanges in a route (debugging stuck routes)
curl -s -H "Authorization: Bearer $KEY" \
  http://localhost:9090/api/contexts/orders/routes/route-1/inflight | jq

# Force-stop a hung route
curl -s -X POST -H "Authorization: Bearer $KEY" \
  http://localhost:9090/api/contexts/orders/routes/route-1/force-stop

# Recent lifecycle events
curl -s -H "Authorization: Bearer $KEY" \
  "http://localhost:9090/api/lifecycle?contextName=orders&limit=20" | jq

# Diagnostic dump (CPU profiles, GC info, thread states)
curl -s -H "Authorization: Bearer $KEY" \
  http://localhost:9090/api/diagnostics/dump > tsak-dump.json
```

### Typed C# client

```csharp
services.AddTsakClient(opts =>
{
    opts.BaseUrl = "http://tsak-prod:9090";
    opts.ApiKey = builder.Configuration["TsakKey"];
});

public class MyService(ITsakApiClient tsak)
{
    public async Task RestartAllAsync(CancellationToken ct)
    {
        var contexts = await tsak.ListContextsAsync(ct);
        foreach (var c in contexts.Where(c => c.Status == "Failed"))
            await tsak.RestartContextAsync(c.Name, ct);
    }
}
```

---

## CLI

`tsak` is a single binary that talks to any Tsak worker. It supports multiple connection profiles, JSON output for CI, and rich tabular output for humans.

```bash
# Profile management
tsak login http://prod-1:9090 --key $PROD_KEY --profile prod
tsak login http://stg-1:9090 --key $STG_KEY  --profile staging
tsak profile use prod
tsak profile list

# Per-call override
tsak --server http://other:9090 --key $OTHER_KEY  context list

# Output format
tsak context list                  # tabular (default)
tsak context list --output json    # JSON
tsak context list --output plain   # raw lines (grep-friendly)
```

### Command groups (30 commands)

| Group | Commands |
|---|---|
| **profile** | `login`, `logout`, `use`, `list` |
| **auth** | `auth keys list`, `auth keys create`, `auth keys revoke` |
| **context** | `context list`, `context get`, `context start`, `context stop`, `context restart`, `context reset-routes`, `context delete` |
| **route** | `route list`, `route get`, `route start`, `route stop`, `route force-stop`, `route inflight` |
| **module** | `module list`, `module get`, `module deploy`, `module delete` |
| **scheduler** | `scheduler status`, `scheduler jobs`, `scheduler running`, `scheduler start`, `scheduler standby`, `scheduler pause-job`, `scheduler resume-job`, `scheduler fire-job` |
| **cluster** | `cluster overview`, `cluster nodes`, `cluster rebalance`, `cluster remove-node` |
| **monitoring** | `health`, `metrics`, `metrics history`, `info`, `logs`, `logs files`, `logs download`, `lifecycle`, `diagnostics`, `route-diagnostics` |
| **watchdog** | `watchdog status`, `watchdog alerts` |
| **users** | `users list`, `users get`, `users create`, `users update`, `users delete` |

---

## Web dashboard

A separate Blazor Server process (`redb.Tsak.Web`) — works in two modes:

| Mode | Storage | Node discovery |
|---|---|---|
| **Standalone** | None | Static node list in `appsettings.json` |
| **Cluster** | Required (Postgres / MSSQL) | Discovered dynamically from redb cluster topology |

### Pages

| Page | Highlights |
|---|---|
| **Dashboard** | Cluster overview, status donut chart, sparkline metrics, node list grid (sortable, filterable, paginated). |
| **Cluster** | 3-level topology tree (cluster → group → node), module assignment, per-node health, click-through to NodeDetail. |
| **NodeDetail** | 5 tabs: Contexts · Scheduler · Modules · Monitoring · Logs. Live Chart.js graphs (CPU, GC, memory, threads), 10s auto-refresh. |
| **Routes** | All routes across all contexts. Status, message count, error rate, click-through to route detail. |
| **RouteView** | Per-route deep-dive: definition, current state, in-flight exchanges, recent diagnostics. |
| **Endpoints** | Consumer / producer endpoints per route. |
| **Watchdog** | Suspected and hung route alerts with manual stop/restart actions. |
| **Logs** | Searchable ring-buffer log viewer with level filter and tail mode. |
| **Auth** | API key management UI — create, revoke (with confirmation). |
| **Login** | Credential-based dashboard access (cluster mode = redb users; standalone = config). |

### Custom design system

The dashboard uses a custom CSS design system — **no Bootstrap, no MUI, no Tailwind**. Built on CSS variables, supports dark/light theme, system fonts, inline SVG icons.

Reusable components: `TsakGrid<T>`, `TsakChart`, `TsakCard`, `TsakBadge`, `TsakIcon`, `TsakConfirmDialog`, `TsakToast`, `TsakPageHeader`, `TsakErrorBoundary`.

---

## Cluster mode

Enable with `Tsak:Cluster:Enabled = true` and a Postgres/MSSQL connection string. Tsak handles the rest.

### What you get

- **Leader election** — distributed lock in redb with TTL and epoch fencing. A new leader is elected automatically when the current one dies or loses its lock.
- **Node registry** — each worker registers itself with periodic heartbeats. Dead nodes are evicted after `DeadNodeTimeoutSeconds`.
- **Automatic context assignment** — the leader distributes contexts across live nodes (currently `round-robin`; weighted strategies on the roadmap). Contexts are reassigned automatically when nodes join or leave.
- **Rolling hot-reload** — module updates roll across nodes in sequence, never updating multiple nodes concurrently.
- **Cluster-wide diagnostics** — `tsak cluster overview` aggregates state from every node.

### Topology in redb

Stored as a polymorphic 3-level tree using `redb.Tree` (so it shows up nicely in any redb-aware tool):

```
cluster:default                     ← scheme: _tsak_clusters
 └── group:default:default          ← scheme: _tsak_groups
      ├── node:default:worker-1     ← scheme: _tsak_nodes
      ├── node:default:worker-2     ← scheme: _tsak_nodes
      └── node:default:worker-3     ← scheme: _tsak_nodes
```

Cluster operations (assignment, leader change, rebalance) mutate this tree atomically. Every operation is fenced by the leader's epoch token — a stale leader cannot corrupt state after losing election.

### Cluster configuration

```json
{
  "Tsak": {
    "Cluster": {
      "Enabled": true,
      "ClusterName": "default",
      "GroupName": "default",
      "NodeId": "",
      "ApiEndpoint": "http://node-1.local:9090",
      "HeartbeatIntervalSeconds": 15,
      "DeadNodeTimeoutSeconds": 60,
      "LeaderLockTtlSeconds": 30,
      "Strategy": "round-robin"
    }
  }
}
```

### Pluggable cluster backends

All cluster coordination is hidden behind interfaces in `redb.Tsak.Core.Pro`:

| Interface | Default implementation (redb) | Drop-in alternative |
|---|---|---|
| `ILeaderElection` | `RedbLeaderElection` (epoch-fenced lock in redb) | `KubernetesLeaderElection` (Lease API), `EtcdLeaderElection`, ZK |
| `IDistributedLock` | `RedbDistributedLock` (TTL row in redb) | `RedisDistributedLock`, `KubernetesLeaseLock` |
| `INodeRegistry` | `RedbNodeRegistry` (heartbeat rows) | `KubernetesPodRegistry` (label selector), Consul |
| `IClusterCoordinator` | `ClusterCoordinator` (background loop) | implementation owns the loop |
| `IClusterBootstrap` | `RedbClusterBootstrap` | bootstrap from K8s ConfigMap |
| `IAssignmentManager` | `RoundRobinAssignmentManager` | `WeightedAssignmentManager`, custom |

Replace any of them with a single DI registration **before** `AddTsakCluster()`. Nothing else in the codebase changes — the `ClusterCoordinator` only talks to these interfaces.

```csharp
builder.Services
    .AddSingleton<ILeaderElection, KubernetesLeaderElection>()  // override default
    .AddTsakCluster(builder.Configuration);                     // everything else stays
```

This is the design path for native Kubernetes integration without ever touching redb for coordination — handy when the cluster runs against an external operational database that you do not want to use as a synchronization primitive.

---

## Watchdog

`WatchdogService` continuously inspects every route and classifies it into one of three states:

```
                                 (no progress for SuspectedThresholdMinutes)
       ┌────────────┐                            │
       │  Healthy   │ ─────────────────────────────────────► ┌─────────────┐
       └────────────┘                                         │  Suspected  │
              ▲                                               └─────────────┘
              │                                                       │
              │ (progress resumes)            (no progress for HungThresholdMinutes)
              │                                                       │
              │                                                       ▼
              │                                                ┌─────────────┐
              └─────────────────────────────────────────────── │    Hung     │
                                                                └─────────────┘
                                                                       │
                                            (AutoRestartHungRoutes = true)
                                                                       │
                                                                       ▼
                                                       Force-stop + restart route
```

Configurable:

```json
{
  "Tsak": {
    "Watchdog": {
      "Enabled": true,
      "CheckIntervalSeconds": 10,
      "SuspectedThresholdMinutes": 0.5,
      "HungThresholdMinutes": 1.5,
      "AutoRestartHungRoutes": false
    }
  }
}
```

Alerts surface in `/api/watchdog/alerts`, in the CLI (`tsak watchdog alerts`), and in the Web dashboard's Watchdog page.

---

## Lifecycle & graceful shutdown

Tsak's startup and shutdown sequences are **deterministic** — `LifecycleHookOrdering` enforces the order so operators can rely on what is up before what depends on it, and what drains before what stops.

### Startup order (Worker DI)

1. **`QuartzSchemaInitializer`** — applies Quartz DDL (Postgres / MSSQL) idempotently via raw ADO.NET. No redb dependency at this stage to avoid bootstrap deadlocks.
2. **`QuartzHostedService`** — starts the scheduler (`RAMJobStore` standalone, `AdoJobStore` cluster).
3. **`MetricsCollectionService`** — periodic process / GC sampling into the circular buffer.
4. **`TsakHostedService`** — main coordinator: shared assembly loader → module discovery → context start → cluster register.
5. **`RouteWatchdogService`** — hung-exchange detector (Pro).

### Shutdown sequence (`TsakHostedService.StopAsync`)

1. Logs `"Tsak graceful shutdown, stopping all contexts..."`.
2. Runs every `ITsakLifecycleHook.OnStoppingAsync()` **in reverse registration order** — last hook to start is first to stop. **Cluster deregistration is a hook**, so leadership is released *before* contexts begin draining.
3. For each context, calls `ITsakContextManager.StopContextAsync(ctx, CancellationToken.None)` — deliberately **not** the host shutdown token. One slow context cannot cancel the drain of the others. Each context owns its own `context:graceful-stop-timeout` for in-flight exchanges.
4. `base.StopAsync()` releases hosted-service resources.
5. Quartz scheduler stops, Serilog sinks flush.

Stop / restart REST responses include `DrainTimeMs` and `InflightAfter` — operators see exactly how long the drain took and whether anything was force-killed. SIGTERM handling is delegated to the standard .NET `IHostApplicationLifetime`.

### Lifecycle hooks

```csharp
public class MyHook : ITsakLifecycleHook
{
    public Task OnStartingAsync(CancellationToken ct)  => /* before module discovery */;
    public Task OnStartedAsync (CancellationToken ct)  => /* after all contexts running */;
    public Task OnStoppingAsync(CancellationToken ct)  => /* before context drain (reverse) */;
    public Task OnStoppedAsync (CancellationToken ct)  => /* after all contexts stopped */;
}

builder.Services.AddSingleton<ITsakLifecycleHook, MyHook>();
```

Every start / stop / error event is also persisted by `LifecycleAuditService` and queryable through `/api/lifecycle`.

---

## Storage modes

| Mode | API keys | Modules | Cluster | State | Use case |
|---|---|---|---|---|---|
| **InMemory** | `ConfigApiKeyStore` (read-only, from `appsettings`) | In-process registry | Not supported | Lost on restart | Dev, CI, embedded scenarios |
| **Redb (Postgres)** | `RedbApiKeyStore` (redb, runtime CRUD) | Persistent | Supported | Survives restart | Single-node production, lightweight clusters |
| **Redb (MSSQL)** | `RedbApiKeyStore` (redb, runtime CRUD) | Persistent | Supported | Survives restart | Single-node production, MSSQL shops |

Switch modes with one config setting:

```json
{ "Tsak": { "Storage": { "Type": "Redb" }, "Redb": { "Provider": "postgres" } } }
```

When `UsePro = true`, [redb.Core.Pro](https://github.com/redbase-app/redb) is enabled — gives you redb change tracking (faster writes), distributed locking primitives, and the cluster topology features.

---

## Security

| Layer | Mechanism |
|---|---|
| **Wire** | API Key in `Authorization: Bearer` or `X-Api-Key` header |
| **Storage** | SHA-256 hash, raw key never persisted |
| **Comparison** | `CryptographicOperations.FixedTimeEquals` (timing-attack safe) |
| **Authorization** | Per-endpoint role checks (`admin`, `reader`, custom) |
| **Lifecycle** | Optional expiry per key, runtime revocation, immediate cache invalidation |
| **User binding** | Optional `UserId` link — when the user is disabled or deleted, the key dies (via `IUserProvider`) |
| **Cache** | 5-minute TTL key lookup cache, invalidated on revoke |
| **Cluster trust** | Inter-node calls use the same API key auth — no implicit trust between nodes |
| **Protected resources** | The `_system` context cannot be stopped or removed by any caller, including admins |

Full policy: [SECURITY.md](SECURITY.md). Production secrets handling: [DEPLOYMENT_SECRETS.md](DEPLOYMENT_SECRETS.md).

---

## Observability

### Metrics

- `MetricsService` — circular buffer with CPU, working set, managed memory, threads, GC pressure. Default window: **12 hours × 10s sample = 4320 points**.
- `ContextMetricsCollector` — per-context aggregation: messages/sec, error rate, in-flight count.
- Per-route metrics from `redb.Route`'s `.Metered()` step.
- Cluster-wide periodic state report: `ClusterReportIntervalSeconds`.

### OpenTelemetry & Prometheus

First-class OTel integration via the standard NuGet packages:

- `OpenTelemetry.Extensions.Hosting`
- `OpenTelemetry.Exporter.Prometheus.HttpListener`
- `OpenTelemetry.Instrumentation.Process`
- `OpenTelemetry.Instrumentation.Runtime`

When `Tsak:Metrics:Prometheus:Enabled = true`, Tsak exposes a Prometheus-format endpoint at **`http://<host>:9090/metrics`** — served straight from the facade's Kestrel server (the API port), auth-exempt. The OTel scrape listener itself binds **loopback** (`localhost:<Port>`, default 9464) and the `/metrics` route proxies it out. That means **no separate exposed port and no Windows URL ACL** — Kestrel binds sockets directly, so a wildcard `HttpListener` is never needed. `Tsak:Metrics:Prometheus:Port` only changes the internal loopback port.

The OTel pipeline registers the redb.Route `ActivitySource` (`RouteActivitySource.SourceName`) so any tracing emitted by route processors is collected automatically — point an OTLP collector / Jaeger / Tempo at the activity source (see [Tracing](#tracing)), or scrape `:9090/metrics` from Prometheus.

```jsonc
"Tsak": {
  "Metrics": {
    "Prometheus": { "Enabled": true, "Port": 9464 }   // Port = internal loopback only
  },
  "Tracing": {
    "ServiceName": "redb-tsak-worker",   // OTel resource service.name (Jaeger/collector)
    "Otlp": { "Enabled": true, "Endpoint": "http://localhost:4317", "Protocol": "grpc" }
  }
}
```

> The exporter is **pre-flighted**: if the loopback bind ever fails, Tsak logs a warning and
> runs **without** metrics rather than crashing the host — an optional exporter must never take
> the Worker down.

Ready-to-use Kubernetes manifests, a Grafana dashboard and a local Prometheus + Grafana +
Jaeger `docker compose` stack live under [`deploy/`](deploy/README.md).

### Health probes — Kubernetes-ready

Distinct probes for the three K8s lifecycle phases. All three are **auth-exempt** by default (`Tsak:Api:AuthExempt`):

| Endpoint | Probe type | Returns 200 when |
|---|---|---|
| `GET /api/health/startup` | startup | process is up |
| `GET /api/health/live` | liveness | process is not deadlocked |
| `GET /api/health/ready` | **readiness** | contexts are running **and** healthy (stricter than liveness) |

> The probe endpoints live under **`/api/health/*`** (the auth-exempt prefix). The rich
> aggregate at `GET /api/system/health` is a separate, auth-gated endpoint for CLI/Web.

`HealthCheckService` aggregates probes (worst status wins: Unhealthy > Degraded > Healthy) and never throws — exceptions inside a probe become Unhealthy, never a 500. Modules can contribute custom probes by implementing `IModuleHealthContributor`. Pro ships `ClusterHealthContributor` reporting leader / member health.

### Logs

- `LogRingBuffer` — Serilog in-memory sink, default 2000 entries, queryable via REST and Web UI.
- File logs via Serilog (rolling files, configurable path).
- `MemoryUsageEnricher` — every log event carries `{MemoryUsage}` for cheap memory pressure correlation.

### Tracing

- OpenTelemetry traces via `redb.Route`'s `.Traced()` step — per-route, per-step spans
  (route/transport spans like `route:identity-discovery` are captured automatically).
- OTLP exporter — opt-in via `Tsak:Tracing:Otlp:Enabled` (+ `Endpoint`, `Protocol`). Ships
  traces to any OTLP collector or directly to **Jaeger** (native OTLP ingest on `:4317` gRPC /
  `:4318` http). Off by default.
- Service identity via `Tsak:Tracing:ServiceName` (default `redb-tsak-worker`) — the
  `service.name` shown in Jaeger / the collector.

### Diagnostics

- Per-route diagnostic dumps via `/api/contexts/{ctx}/routes/{id}/diagnostics`.
- Cluster-wide dumps via `/api/diagnostics/dump`.
- Lifecycle events feed (route start/stop/restart, hot-swap, cluster reassignment, watchdog alerts).
- In-flight exchange tracking — see exactly which messages are sitting where right now.

---

## Quartz scheduler

Every Tsak context gets an `IScheduler` injected for free. Modules use it via `redb.Route.Quartz`:

```csharp
From("cron:0 */5 * * * ?")           // every 5 minutes
    .Setbody(() => DateTime.UtcNow)
    .To("rabbitmq://heartbeats");
```

| Mode | Storage | Cluster-safe | Use |
|---|---|---|---|
| `RAMJobStore` | In-memory | No | Standalone, dev |
| `AdoJobStore` | Postgres / MSSQL `QRTZ_*` tables | Yes | Production |

Schema is auto-created on first start by `QuartzSchemaInitializer` — embedded SQL scripts for both Postgres and MSSQL, idempotent, runs **before** Quartz initializes its own connection pool. No DBA action required.

REST endpoints under `/api/scheduler` cover: status, listing scheduled jobs, listing currently-running jobs, start/standby, pause/resume/fire-now per job key.

---

## Three deployment recipes

### Recipe A — Standalone, single binary

For development, demos, embedded use.

```json
{
  "Tsak": {
    "Storage": { "Type": "InMemory" },
    "Cluster": { "Enabled": false },
    "Auth":    { "Enabled": false }
  }
}
```

Just `dotnet run`. No DB, no auth, no cluster. Fastest path to running a Tsak module locally.

### Recipe B — Single node + redb (Postgres)

For production single-node deployments where state must survive restarts.

```json
{
  "ConnectionStrings": { "Postgres": "Host=db;Database=redb;Username=tsak;Password=$$" },
  "Tsak": {
    "Storage": { "Type": "Redb" },
    "Redb":    { "Provider": "postgres", "UsePro": true, "License": "$$" },
    "Cluster": { "Enabled": false },
    "Auth":    { "Enabled": true, "Secret": "$$" }
  }
}
```

API keys persist. Quartz jobs persist. Lifecycle events persist. Module assignments persist. One node, durable state.

### Recipe C — Cluster

For HA and horizontal scaling. Same `appsettings` on every node, only `NodeId` and `ApiEndpoint` differ:

```json
{
  "ConnectionStrings": { "Postgres": "Host=db.cluster;Database=redb;Username=tsak;Password=$$" },
  "Tsak": {
    "Storage": { "Type": "Redb" },
    "Redb":    { "Provider": "postgres", "UsePro": true },
    "Cluster": {
      "Enabled": true,
      "ClusterName": "production",
      "GroupName": "default",
      "ApiEndpoint": "http://node-1.local:9090",
      "Strategy": "round-robin"
    },
    "HotReload": { "RollingUpdate": true },
    "Auth":     { "Enabled": true, "Secret": "$$" }
  }
}
```

Start three workers. They'll discover each other through the shared redb store, elect a leader, distribute your modules, and roll updates without downtime.

---

## Docker

Pre-built images are published to GitHub Container Registry for every release — no build step required. See [Quick start → Option A](#option-a--docker-fastest) for the basic flow. This section covers production deployment.

### Image variants

| Image | Best for | Default ports |
|---|---|---|
| `ghcr.io/redbase-app/redb-tsak-worker` | Headless workers (k8s `Deployment`/`StatefulSet`), one process per pod | `9090` (REST) |
| `ghcr.io/redbase-app/redb-tsak-web`    | Separate management UI pod talking to a worker cluster | `8085` |
| `ghcr.io/redbase-app/redb-tsak-stack`  | Single-host install (Worker + Web in one container, like `rabbitmq:management`) | `9090`, `8085` |

Available tags:
- `latest`, `<version>` — net9 build (default).
- `<version>-net8`, `<version>-net9`, `<version>-net10` — Worker only; pick a TFM that matches your shared connector ABI.
- `<version>-net9` — Web and Stack are net9 only.

### Production run (Worker + external Postgres)

```bash
docker run -d --name tsak \
  -p 9090:9090 \
  -v /opt/tsak/Libs:/app/Libs \
  -e ConnectionStrings__Postgres="$PG_CONN" \
  -e Tsak__Auth__Secret="$TSAK_SECRET" \
  -e Tsak__Metrics__Prometheus__Enabled=true \
  ghcr.io/redbase-app/redb-tsak-worker:2.0.2
```

Mount `Libs/` from the host (or a shared volume) so module updates can be deployed without rebuilding the image. `appsettings.json` overrides flow through environment variables in the standard ASP.NET pattern (`__` for nesting).

### docker compose templates

Ready-to-use compose files are shipped in [`publish/docker/`](publish/docker/) — copy and edit, no monorepo paths:

| File | What it stands up |
|---|---|
| `compose.worker.yml` | Worker only |
| `compose.web.yml`    | Web only (talks to an existing worker) |
| `compose.stack.yml`  | Worker + Web in one container |
| `compose.full.yml`   | Stack + PostgreSQL (durable RTTI storage, single-node) |

Each has a matching `compose.*.env.example` — copy to `.env` and fill in.

### Verifying images

```bash
cosign verify --key cosign.pub ghcr.io/redbase-app/redb-tsak-worker:2.0.2
```

Public key: [`publish/keys/cosign.pub`](publish/keys/cosign.pub) in the repo, or downloadable from any [release](https://github.com/redbase-app/redb-tsak/releases).

### Building images yourself

If you need a custom build (e.g. proprietary connectors baked in), see [`publish/HOW_TO_PUBLISH.md`](publish/HOW_TO_PUBLISH.md) for the full pipeline (`pwsh publish/build.ps1 -All`).

---

## Kubernetes deployment

Tsak is built from the ground up for container deployment — distinct K8s probes, OTel/Prometheus exporter, env-var configuration, and pluggable cluster backends are all in the box.

### Probes

Map the three health endpoints to the matching K8s probe types:

```yaml
startupProbe:
  httpGet: { path: /api/health/startup, port: 9090 }
  failureThreshold: 30
  periodSeconds: 4           # up to 120s to boot

livenessProbe:
  httpGet: { path: /api/health/live, port: 9090 }
  periodSeconds: 10
  failureThreshold: 3        # 30s before pod restart

readinessProbe:
  httpGet: { path: /api/health/ready, port: 9090 }
  periodSeconds: 5
  failureThreshold: 2        # 10s before removal from load balancer
```

**Liveness** intentionally does *not* check module health — that avoids restart loops during rolling updates. **Readiness** is stricter: any context in a non-running state → pod is removed from service endpoints (no restart), allowing the cluster to rebalance via `IAssignmentManager`.

### Graceful termination

Kubernetes sends SIGTERM, then SIGKILL after `terminationGracePeriodSeconds`. Set `Tsak:Shutdown:TimeoutSeconds` to **`terminationGracePeriodSeconds − 5`** so cluster deregistration has its buffer. The shutdown sequence then becomes:

```
SIGTERM → cluster deregister (lifecycle hook, reverse-order) →
context drain (isolated CT, up to TimeoutSeconds each) →
Quartz shutdown → log flush → SIGKILL never needed
```

```yaml
spec:
  terminationGracePeriodSeconds: 60
  containers:
    - name: tsak
      env:
        - name: Tsak__Shutdown__TimeoutSeconds
          value: "55"
```

### Pod identity in cluster mode

StableNode IDs across pod restarts matter for assignment continuity. Inject pod metadata via the downward API and bind `NodeId` to `metadata.name`:

```yaml
env:
  - name: POD_NAME
    valueFrom: { fieldRef: { fieldPath: metadata.name } }
  - name: POD_NAMESPACE
    valueFrom: { fieldRef: { fieldPath: metadata.namespace } }
  - name: POD_IP
    valueFrom: { fieldRef: { fieldPath: status.podIP } }
  - name: Tsak__Cluster__NodeId
    valueFrom: { fieldRef: { fieldPath: metadata.name } }
  - name: Tsak__Cluster__ApiEndpoint
    value: http://$(POD_IP):9090
```

Use a **StatefulSet** (or a Deployment + headless service) for predictable pod names. Tsak treats `Tsak:Cluster:NodeId` as the cluster identity — mapping it to `metadata.name` keeps assignment stable across restarts.

### Prometheus scraping

```yaml
metadata:
  annotations:
    prometheus.io/scrape: "true"
    prometheus.io/port:   "9090"
    prometheus.io/path:   "/metrics"
```

Set `Tsak:Metrics:Prometheus:Enabled = true` to expose `/metrics` on the facade (port 9090). The OTel listener binds loopback only and is proxied out by the facade route — no separate metrics port and no Windows URL ACL. Ready-to-use manifests are under [`deploy/k8s/`](deploy/k8s).

### Native cluster integration

All coordination interfaces (`ILeaderElection`, `IDistributedLock`, `INodeRegistry`, `IClusterCoordinator`, `IClusterBootstrap`, `IAssignmentManager`) are pluggable — see [Pluggable cluster backends](#pluggable-cluster-backends). A Kubernetes-native Lease implementation (`KubernetesLeaderElection`) can be dropped in without touching redb for coordination, leaving redb only for module / lifecycle / API key state.

---

## Testing

| Suite | Count | What's covered |
|---|---:|---|
| `redb.Tsak.Tests` | **287** | Module loading, context lifecycle, config merge, security, controllers, monitoring, scheduler, cluster (leader election, heartbeat, rebalance, epoch fencing), hot-reload (ALC isolation, rolling update, rollback, removal debounce), in-memory and Redb stores, host startup, full API integration |
| `redb.Tsak.CLI.Tests` | **64** | All 30 CLI commands — output format (table/JSON/plain), error handling, auth failure, profile resolution |
| **Total** | **351** | All passing on net8.0, net9.0, net10.0 |

```bash
dotnet test redb.Tsak/tests/redb.Tsak.Tests
dotnet test redb.Tsak/tests/redb.Tsak.CLI.Tests
```

---

## Implementation status

All 9 phases are complete and merged. See [STATUS.md](STATUS.md) for the per-phase breakdown.

| # | Phase | Status | Tests |
|---|---|---|---|
| 0 | Infrastructure | Done | 1 |
| 1 | Controller Dispatcher (transport-agnostic) | Done | 20 |
| 2 | Container Core (modules, contexts, coordinator) | Done | 32 |
| 3 | Cluster (leader election, registry, assignment) | Done | 9 |
| 4 | Hot Reload (collectible ALC, rolling update, rollback) | Done | 25 |
| 5 | Monitoring (metrics, health, logs, watchdog) | Done | 50 |
| 6 | REST API & Auth (12 controllers, redb key store) | Done | 42 |
| 7 | Quartz Scheduler (DI, schema initializer, controller) | Done | 30 |
| 8A | CLI (30 commands, profiles, JSON) | Done | 64 |
| 8B | Web UI (Blazor Server, 10 pages, design system) | Done | — |

**Total: 351 tests passing.**

---

## Frequently asked questions

**Is Tsak a routing engine?**
No. Routing is [redb.Route](https://github.com/redbase-app/redb-route). Tsak is the runtime container that hosts route contexts, manages their lifecycle, and exposes a management surface.

**Can Tsak load any .NET DLL?**
Tsak loads class libraries that follow its module convention: a static `InitRoute.main(IRouteContext)` method, or one or more public `RouteBuilder` subclasses. Random DLLs are ignored.

**Is module isolation a sandbox?**
No. It is a dependency isolation boundary (separate `AssemblyLoadContext` per module). A malicious module runs with the same OS privileges as the Tsak process. Restrict write access to `Libs/` accordingly. See [SECURITY.md](SECURITY.md).

**Why is `Collectible: false` the default for hot-reload?**
Because `Reflection.Emit`-based code (XmlSerializer, source-gen serializers, compiled regex, MEF) crashes inside collectible `AssemblyLoadContext`s. The .NET runtime forbids non-collectible dynamic assemblies (generated by Emit) from referencing types in a collectible ALC. Default is the safe choice; opt in only when you know your modules are Emit-free.

**What happens to in-flight messages during hot-swap?**
The old context drains (existing exchanges complete). The new context starts in parallel. Once the new context is healthy and `StartupTimeoutSeconds` elapses, the old context is retired. Zero message loss for transports that support graceful shutdown.

**Can I write my own assignment strategy?**
Yes — `IAssignmentManager` is the extensibility point. The `round-robin` strategy is the only one shipped today; weighted strategies are on the roadmap.

**Does Tsak support multi-region clusters?**
Out of the box, no. The cluster coordination assumes low-latency access to the shared redb database. For multi-region, run one cluster per region and federate above the Tsak layer (e.g. via `redb.Route.RabbitMQ` shovels).

**Why a custom design system instead of Bootstrap or MUI?**
The dashboard is small and focused. Custom CSS keeps the bundle tiny, eliminates a major source of UI churn (vendor breaking changes), and gives full control over theming. CSS variables enable dark/light themes with no JS.

**Is there an OpenAPI spec?**
Not yet. The 12 controllers are documented in [STATUS.md](STATUS.md) and exposed via the typed `ITsakApiClient`. OpenAPI / Swagger generation is on the roadmap.

---

## Roadmap

- OpenAPI / Swagger generation for the REST API
- Weighted cluster assignment strategies (CPU + memory composite)
- Batch operations (`POST /api/contexts/batch/start`)
- Webhook subscriptions for lifecycle events (push instead of poll)
- Live config editor in the dashboard
- Multi-region federation primitives

See [docs/](docs/) for design notes on each.

---

## Part of

- [redb.Route](https://github.com/redbase-app/redb-route) — ESB & EIP framework for .NET (the routing engine Tsak hosts)
- [redb.Core / redb.Core.Pro](https://github.com/redbase-app/redb) — RTTI storage backend (the persistence layer Tsak uses)
- [RedBase](https://github.com/redbase-app) — full ecosystem

---

## License

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Copyright © 2024–2026 RedBase.
