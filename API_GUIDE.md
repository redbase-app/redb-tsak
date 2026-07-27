# redb.Tsak — Management API Guide

What the REST API is, how it is wired, and why a runtime container needs this many
endpoints. Written so you can answer those questions **without opening `src/`**.

> Operators and integrators: this is the reference for driving a Tsak node from
> curl, CI, Kubernetes, or your own C# service. For deployment see
> [DEPLOYMENT.md](DEPLOYMENT.md), for settings see [CONFIG_GUIDE.md](CONFIG_GUIDE.md).

---

## 1. Where the API lives

The management API is **not** a separate ASP.NET application bolted onto the worker.
It is a redb.Route context named `_system`, started by `SystemContextBuilder` alongside
your own module contexts, on the same runtime, with the same lifecycle.

```
Tsak worker process
├── _system    ← the management API (this document)
├── orders     ← your module context
└── billing    ← your module context
```

Consequences that matter in practice:

- `tsak context list` shows `_system` next to your contexts. It has routes, metrics,
  and in-flight exchanges like any other context — the API can be observed by itself.
- One HTTP listener, one port: `Tsak:Api:Host` / `Tsak:Api:Port` (default `0.0.0.0:9090`).
- The whole API can be switched off with `Tsak:Api:Enabled=false` — the worker then runs
  your routes headlessly, with no control plane exposed.
- The Web dashboard is a **separate** process/port (default `5100`) and is just another
  client of this API. Nothing in the API depends on the UI being deployed.

---

## 2. How a request is served

There is exactly **one** route for the whole API — a catch-all HTTP listener whose
`Process` block is the entire pipeline:

```
HTTP request
   │
   ▼
from("http:0.0.0.0:9090/{**path}")          ← single listener, single socket
   │
   ├─ BridgeHttpHeaders                     redbHttp.* → route.* + query.* headers
   │
   ├─ AuthorizeProcessor                    skipped when path matches Tsak:Api:AuthExempt
   │  └─ KeyedThrottleProcessor             per-IP gate, only on /api/auth/*
   │
   ├─ ControllerDispatcherProcessor         path/method → controller method
   │     └─ AuthController, ContextsController, HealthProbeController, …
   │
   └─ PrepareHttpResponse                   object → JSON, status code, content type
```

Controllers are plain classes deriving from `RedbController` (from `redb.Route.Controllers`),
annotated with `[Route]` / `[HttpGet]` / `[HttpPost]` / `[FromQuery]`. They are discovered by
**assembly scan**, so adding an endpoint means adding a method — no registration, no DI wiring,
no change to `SystemContextBuilder`. Pro contributes its own controllers by registering an
`ISystemContextPlugin`, which hands additional assemblies to the same registry.

Inside a controller you work with Route primitives, not ASP.NET ones: `Exchange` for the
message, `Context.GetService<T>()` for dependencies, `ApiResponse.*` + `Exchange.Stop()` to
short-circuit with a status code.

### The three routes that are *not* controllers

Two endpoints need to bypass the shared pipeline, so they are registered as their own routes
on the same port (path specificity makes them out-rank the catch-all):

| Route id | Path | Why it is standalone |
|---|---|---|
| `system-echo` | `/api/echo` (`Tsak:Api:Echo:Path`) | Own pipeline with **no** auth processor, and `AutoStart(false)` so it can be started/stopped on demand from the Routes API or dashboard. Reflects the request back as JSON — "am I reaching the host, and what did it actually receive?" |
| `system-metrics` | `/metrics` | Proxies the loopback OpenTelemetry scrape listener so Prometheus can scrape the main port. Returns `text/plain; version=0.0.4`, not JSON. Mounted only when `Tsak:Metrics:Prometheus:Enabled=true`. |

The **health probes are not** in this list — they are ordinary controllers
(`HealthProbeController`), because they need nothing special: authentication is skipped
declaratively via `Tsak:Api:AuthExempt`, and their response is plain JSON.

---

## 3. Why there are this many endpoints

Every endpoint exists to answer a question you would otherwise answer by SSH-ing into the
box, reading log files, or restarting the process and hoping. The API surface is the set of
operational questions a route runtime gets asked:

| Question | Endpoints | Who asks |
|---|---|---|
| *Is the process alive / ready for traffic?* | `/api/health/*` | Kubernetes, load balancers |
| *What is actually running right now?* | `/api/contexts`, `.../routes`, `/api/modules`, `/api/dashboard` | operators, UI |
| *Why is this route stuck?* | `.../inflight`, `.../force-stop`, `/api/watchdog/*`, `/api/diagnostics/*` | on-call |
| *What happened just before it broke?* | `/api/lifecycle`, `/api/logs*` | on-call |
| *Who did what, and when?* | `/api/audit` | admins, compliance |
| *How much is it costing me?* | `/api/system/metrics`, `/metrics/history`, `.../routes/{id}/metrics`, Prometheus `/metrics` | monitoring |
| *Deploy / roll a module without downtime* | `/api/contexts/{name}/start`, `/stop`, `/restart`, `/api/modules/{name}` DELETE | CI/CD |
| *What is the cluster doing?* | `/api/cluster/*` | operators (Pro) |
| *When does this job fire, and can I pause it?* | `/api/scheduler/*` | operators |
| *Who is allowed to do all of the above?* | `/api/auth/keys*`, `/api/users*` | admins |

The design rule: anything the dashboard can show, the API can return, and anything the API
can do, the `tsak` CLI can do — so a human, a script, and the UI are never on unequal footing.

---

## 4. Endpoint map

**69 endpoints across 16 controllers.** All speak JSON. 14 controllers ship in
`redb.Tsak.Core`; `ClusterController` comes from `redb.Tsak.Core.Pro` via `ISystemContextPlugin`.

### Health probes — `/api/health` (3, auth-exempt)

| Method | Path | 200 when |
|---|---|---|
| GET | `/api/health/startup` | contexts are loaded and the cluster coordinator has synced |
| GET | `/api/health/live` | process is responsive; no hung exchanges reported by the watchdog |
| GET | `/api/health/ready` | full health evaluation is not `Unhealthy` |

Failures return `503` with a short reason. Bodies are intentionally minimal — kubelet
does not read them. See §6 for the rich variant.

### System — `/api/system` (5)

| Method | Path | Returns |
|---|---|---|
| GET | `/api/system/health` | aggregated health with per-component statuses |
| GET | `/api/system/metrics` | latest CPU / memory / GC / thread snapshot |
| GET | `/api/system/metrics/history` | stored metric points for charting |
| GET | `/api/system/info` | version, uptime, machine info |
| GET | `/api/system/config` | effective merged config (`Tsak:*`/`ConnectionStrings:*`), secrets redacted (`admin`) |

`/api/system/config` answers "what settings is this node actually on" on an incident — a flat
key→value map with sensitive keys masked and connection-string passwords stripped. `admin` only
(it reveals topology). Client `GetConfigAsync`, CLI `tsak system config`.

### Contexts — `/api/contexts` (8)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/contexts` | list all contexts with status and counts |
| GET | `/api/contexts/{name}` | one context in detail |
| POST | `/api/contexts/{name}/start` | start |
| POST | `/api/contexts/{name}/stop` | graceful stop |
| POST | `/api/contexts/{name}/restart` | stop + start |
| POST | `/api/contexts/{name}/reset-route-states` | clear persisted per-route start/stop state |
| GET | `/api/contexts/{name}/endpoints` | endpoints exposed by the context, with health |
| DELETE | `/api/contexts/{name}` | remove the context |

### Routes — `/api/contexts/{name}/routes` (8)

Routes live under their context, which is why they share the `/api/contexts` prefix.

| Method | Path | Purpose |
|---|---|---|
| GET | `.../routes` | list routes of a context |
| GET | `.../routes/{routeId}` | one route in detail |
| POST | `.../routes/{routeId}/start` | start a single route |
| POST | `.../routes/{routeId}/stop` | graceful stop of a single route |
| POST | `.../routes/{routeId}/force-stop` | abandon in-flight work and stop |
| GET | `.../routes/{routeId}/inflight` | exchanges currently inside this route |
| GET | `/api/contexts/{name}/inflight` | in-flight exchanges across the whole context |
| GET | `.../routes/{routeId}/metrics` | per-route counters and timings |

`inflight` + `force-stop` are the pair you reach for when a route will not drain: look at
what is stuck, then decide whether to abandon it.

### Modules — `/api/modules` (6)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/modules` | loaded modules, versions, origin (`.tpkg` or bare directory) |
| GET | `/api/modules/{name}` | module detail |
| DELETE | `/api/modules/{name}` | unload a module and shut down its contexts gracefully |
| POST | `/api/modules/upload` | deploy a signed `.tpkg` (admin; **off by default**) |
| POST | `/api/modules/validate` | dry-run: validate a `.tpkg` without installing (admin) |
| POST | `/api/modules/{name}/rollback` | restore the previous on-disk version (admin) |

A package update is **staged**: the new `.tpkg` is loaded and checked in a throwaway ALC before
the running version is torn down, so a broken package leaves the live context running.

Upload/rollback deploy **executable code**, so they are disabled by default
(`Tsak:Modules:Upload:Enabled`), admin-only, audited, and gated by signature verification. The
full trust model — the two verification boundaries, the two "require signature" switches, and the
keygen/sign flow — is in **[MODULE_DEPLOYMENT.md](MODULE_DEPLOYMENT.md)**.

### Watchdog — `/api/watchdog` (6)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/watchdog/status` | suspected / hung exchange counters |
| GET | `/api/watchdog/alerts` | recent alerts |
| POST | `/api/watchdog/enable` | turn detection on at runtime |
| POST | `/api/watchdog/disable` | turn it off (e.g. during a known long batch) |
| GET | `/api/watchdog/alerts/status` | is alert delivery active, and which channels are enabled |
| POST | `/api/watchdog/test-alert` | send a synthetic alert through every enabled channel (per-channel result) |

New alerts are pushed to configurable channels (all off by default): **webhook**, **telegram**,
**email** (all native, no extra deps) and a generic **endpoint** channel that sends to any
redb.Route producer URI (`kafka:`, `rabbitmq:`, `amqp:`, `sqs:`, `mqtt:` …) — one channel for
every broker, no per-connector code. Delivery is fire-and-forget with dedup by incident.
Configure under `Tsak:Watchdog:Alerts`; verify with `test-alert`.

### Diagnostics & lifecycle — `/api/diagnostics`, `/api/lifecycle` (3)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/diagnostics/dump` | full dump — GC, threads, contexts, routes (cluster-wide) |
| GET | `/api/diagnostics/{contextName}/{routeId}` | route-scoped diagnostics |
| GET | `/api/lifecycle` | recent lifecycle events, filterable by context and limit |

### Audit — `/api/audit` (1, admin)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/audit?actor=&action=&target=&since=&until=&limit=&offset=` | persisted admin-action trail, newest-first |

Every mutating admin action (marked `[AuditAdminAction]`) is persisted to the flat
`tsak_audit_log` table — who, what, target, status, duration, source IP — and survives
restarts. Requires the `admin` role: the trail records who did what, so it must not be
enumerable by a lower-privilege caller. Without a database the trail lives in the log instead
(a `[tsak-audit]`-anchored JSON line) and this endpoint reports `available: false`.

Filtering and paging are **server-side** (the table can be large): `limit` is clamped to
1..1000, `offset` pages. Exposed through `ITsakApiClient.GetAuditAsync`, the `tsak audit` CLI
command, and the dashboard **Audit** page.

### Dead-letter queue — `/api/exchanges` (3)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/exchanges/failed?context=&route=&status=&since=&until=&limit=&offset=` | failed exchanges, newest-first (`operator`) |
| POST | `/api/exchanges/{id}/replay` | re-run the entry's route tail from its snapshot (`operator`) |
| DELETE | `/api/exchanges/{id}` | discard an entry (`admin`) |

An exchange that fails at a redb.Route **replay checkpoint** (`.Replayable("…")` in the route) is
captured into the flat `tsak_dlq` table — snapshot, route, marker, exception. **Opt-in by
construction**: only routes carrying a marker leave a checkpoint, so the DLQ never tries to own an
exchange a broker/transaction already redelivers. Replay rehydrates the snapshot and re-runs the
route tail (the tail gets fresh redb/SQL connections). Replay is **at-least-once + manual** — make
replayed side-effects idempotent. Client `GetFailedExchangesAsync`/`ReplayExchangeAsync`, CLI
`tsak dlq`, dashboard **Dead-letter** page. Without a database the endpoint reports
`available: false`.

### Logs — `/api/logs` (3)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/logs?afterId=&limit=&level=` | ring-buffer entries; `afterId` makes tailing incremental |
| GET | `/api/logs/files` | list rolling log files with size and mtime |
| GET | `/api/logs/files/{filename}` | download one file as a ZIP |

### Scheduler — `/api/scheduler` (8)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/scheduler/status` | started / standby / stopped, job and trigger counts |
| GET | `/api/scheduler/scheduled` | every scheduled job with its triggers and next fire time |
| GET | `/api/scheduler/jobs` | jobs **currently executing** |
| POST | `/api/scheduler/start` | start the scheduler |
| POST | `/api/scheduler/standby` | pause firing without losing schedules |
| POST | `/api/scheduler/pause-job?key={jobKey}` | pause one job |
| POST | `/api/scheduler/resume-job?key={jobKey}` | resume it |
| POST | `/api/scheduler/fire-job?key={jobKey}` | fire a job immediately, out of schedule (`operator`) |

### Cluster — `/api/cluster` (6, Pro)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/cluster` | leader, epoch, membership, assignment state |
| GET | `/api/cluster/nodes` | node list with heartbeats and `cordoned` flag |
| POST | `/api/cluster/rebalance` | force a reassignment pass |
| DELETE | `/api/cluster/nodes/{nodeId}` | evict a dead node from the registry |
| POST | `/api/cluster/nodes/{nodeId}/cordon` | cordon a node — keeps current work, takes no new work, drains locks to peers |
| POST | `/api/cluster/nodes/{nodeId}/uncordon` | uncordon — resume taking work |

Cordon is the graceful middle state for planned maintenance / rolling upgrade: `remove-node` is a
hard eviction, `cordon` lets the node drain first. A cordoned node stays Online (heartbeating) but
its clustered route locks release to peers.

### Auth & users — `/api/auth`, `/api/users` (8)

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/auth/keys` | create an API key (raw value returned **once**) |
| GET | `/api/auth/keys` | list key metadata — never the secrets |
| DELETE | `/api/auth/keys/{id}` | revoke a key |
| GET | `/api/users` | list users |
| GET | `/api/users/{login}` | one user |
| POST | `/api/users` | create |
| PUT | `/api/users/{login}` | update |
| DELETE | `/api/users/{login}` | delete |

`/api/users` is backed by redb's user provider, so it requires a redb-backed storage mode —
in standalone/in-memory mode there is nothing to store users in.

### Dashboard — `/api/dashboard` (1)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/dashboard` | one aggregated snapshot: metrics + contexts + top routes + alerts |

Exists so the UI renders a screen with a single round-trip instead of six.

---

## 5. Authentication

Auth is **opt-in**: `Tsak:Auth:Enabled` (default `false`). With it off, every endpoint is
open — appropriate only for a private network or a local dev box.

With it on, each request carries a key in either form:

```
Authorization: Bearer tsak_xxxxxxxx
X-Api-Key: tsak_xxxxxxxx
```

- Keys are created via `/api/auth/keys` and carry roles; see **Roles** below.
- Failures are `401` (missing/invalid key) or `403` (valid key, insufficient roles).
- `/api/auth/*` is additionally rate-limited per remote IP —
  `Tsak:Api:AuthThrottle:Limit` (default 10) per `WindowSeconds` (default 60). Set the limit
  to `0` to disable.
- `Tsak:Api:AuthExempt` lists paths that skip auth entirely. Default:
  `["/api/health/*", "/api/health"]`. A trailing `*` is a prefix match; otherwise the match is
  exact and case-insensitive.

### Roles

Every key carries a comma-separated role list. Three roles form a ladder — a higher one
satisfies any requirement for a lower one:

| Role | May do |
|---|---|
| `viewer` | every `GET`: contexts, routes, modules, metrics, health, lifecycle, dashboard |
| `operator` | plus lifecycle actions — start / stop / restart contexts and routes, scheduler control, watchdog toggle, diagnostics dumps, log access |
| `admin` | plus API keys, users, module removal, context removal, route `force-stop`, cluster rebalance and node eviction |

Custom roles (any other string) are matched by exact name only — `admin` does **not**
satisfy a requirement for `release-bot`.

Endpoints declare their requirement with `[RequiresRole(...)]` on the action or the
controller. An endpoint with no attribute falls back to a safe default derived from the HTTP
method: `GET` needs `viewer`, everything else needs `operator` — a newly added endpoint is
therefore never open by accident.

**Technical endpoints are never role-gated.** The check runs only for authenticated
requests, so the auth-exempt Kubernetes probes pass straight through; on top of that
`HealthProbeController` is marked `[NoRoleRequired]`, so even if an operator narrows
`Tsak:Api:AuthExempt`, a probe can start requiring a key but will never answer `403` — a
probe rejected on privileges would take a healthy pod out of rotation. The echo and
Prometheus routes have their own pipelines and never reach the check at all.

Two switches control the behaviour:

- `Tsak:Auth:EnforceRoles` (default `true`) — turn role checking off entirely.
- `Tsak:Auth:RolelessKeysAreAdmin` (default `true`) — keys issued before roles were
  enforced carry no roles; they keep working as `admin` and log a one-time warning. Set to
  `false` to deny them once all keys have been re-issued with explicit roles.

**Deployment note.** Three things answer without a key: the health probes (by the exempt
list) plus `/api/echo` and `/metrics` (own pipelines). None of them expose business data, but
all three confirm the node exists and `/metrics` describes its load — keep the API port on an
internal network or behind an ingress rule rather than on the public internet. `/api/echo`
additionally ships stopped (`AutoStart(false)`), so it answers only after somebody starts it.

---

## 6. Health: two endpoints, two audiences

| | `/api/health/{startup,live,ready}` | `/api/system/health` |
|---|---|---|
| Audience | kubelet, load balancers | humans, CLI, dashboard |
| Auth | exempt by default | subject to auth |
| Body | `{"status":"ok"}` or `503` + reason | full `HealthCheckResult` with per-check statuses |
| Cost | minimal | runs every contributor |

`HealthCheckService` aggregates the built-in `contexts` / `metrics` / `watchdog` checks with
two extension points, and **never throws** — an exception inside a probe becomes `Unhealthy`,
not a `500`. Worst status wins: `Unhealthy` > `Degraded` > `Healthy`.

- `IHealthContributor` — sees the whole evaluation bag. Pro's `ClusterHealthContributor`
  uses it to report leader and member health.
- `IModuleHealthContributor` — per-module probe, published under the key `module:{name}`.
  Implement it in your module to surface "my downstream is unreachable" to
  `/api/health/ready`, which is what makes Kubernetes take the pod out of rotation.

Note that `Degraded` still returns `200` on readiness — only `Unhealthy` is a `503`.

---

## 7. Four ways to call it

```bash
# 1. curl — everything is JSON
curl -s http://localhost:9090/api/health/ready -o /dev/null -w '%{http_code}\n'
curl -s -H "X-Api-Key: $KEY" http://localhost:9090/api/system/health | jq
```

```bash
# 2. tsak CLI — same operations, tabular or --output json for CI
tsak context list
tsak health
tsak route inflight orders route-1
```

```csharp
// 3. Typed C# client — ITsakApiClient covers the whole surface
services.AddTsakClient(o => { o.BaseUrl = "http://tsak-prod:9090"; o.ApiKey = key; });

public class Ops(ITsakApiClient tsak)
{
    public async Task RestartFailedAsync(CancellationToken ct)
    {
        foreach (var c in (await tsak.ListContextsAsync(ct)).Where(c => c.Status == "Failed"))
            await tsak.RestartContextAsync(c.Name, ct);
    }
}
```

4. **Web dashboard** — a Blazor app on its own port that consumes exactly the endpoints above.

---

## 8. Extending the API

| You want to… | Do this |
|---|---|
| add endpoints from a Pro/host assembly | implement `ISystemContextPlugin`, return your assembly from `GetControllerAssemblies()`, register it in DI |
| require a specific role on an endpoint | annotate the action or controller with `[RequiresRole(TsakRoles.Admin)]` |
| expose a technical endpoint that must never 403 | annotate it `[NoRoleRequired]` and add its path to `Tsak:Api:AuthExempt` |
| report module health to k8s | implement `IModuleHealthContributor` in the module |
| add a system-wide health check | implement `IHealthContributor` |
| expose an endpoint without auth | add its path to `Tsak:Api:AuthExempt` |
| expose an endpoint that needs its own pipeline | register a route on the API port — a concrete path out-ranks the `{**path}` dispatcher |

---

## 9. Configuration keys

| Key | Default | Effect |
|---|---|---|
| `Tsak:Api:Enabled` | `true` | build and start the `_system` context at all |
| `Tsak:Api:Host` | `0.0.0.0` | listener bind address |
| `Tsak:Api:Port` | `9090` | listener port (shared by API, echo, `/metrics`) |
| `Tsak:Auth:Enabled` | `false` | require an API key |
| `Tsak:Auth:EnforceRoles` | `true` | enforce `[RequiresRole]` on endpoints (no effect when auth is off) |
| `Tsak:Auth:RolelessKeysAreAdmin` | `true` | keys with no roles keep full access (back-compat); `false` denies them |
| `Tsak:Api:AuthExempt` | `["/api/health/*", "/api/health"]` | paths that skip auth |
| `Tsak:Api:AuthThrottle:Limit` | `10` | per-IP attempts on the auth prefix; `0` disables |
| `Tsak:Api:AuthThrottle:WindowSeconds` | `60` | throttle window |
| `Tsak:Api:AuthThrottle:PathPrefix` | `/api/auth/` | what the throttle covers |
| `Tsak:Api:Echo:Path` | `/api/echo` | echo route path |
| `Tsak:Metrics:Prometheus:Enabled` | `false` | mount `/metrics` on the API port |
| `Tsak:Metrics:Prometheus:Port` | `9464` | loopback OTel listener being proxied |
| `Tsak:Audit:Enabled` | `true` | persist admin actions to `tsak_audit_log` (falls back to log with no DB) |
| `Tsak:Audit:RetentionDays` | `90` | prune audit entries older than this; `0` keeps forever |
| `Tsak:Dlq:Enabled` | `true` | capture failed exchanges (at route checkpoints) into `tsak_dlq` |
| `Tsak:Dlq:RetentionDays` | `30` | prune DLQ entries older than this; `0` keeps forever |
| `Tsak:Health:DegradedNotReady` | `false` | when `true`, the readiness probe also returns `503` on `Degraded` |
| `Tsak:Modules:Upload:Enabled` | `false` | enable `POST /api/modules/upload` + `/rollback` (RCE surface — keep off unless needed) |
| `Tsak:Modules:Signature:Required` | `false` | refuse unsigned/tampered `.tpkg` at load (needs `PublicKeyPath`) |

---

## See also

- [README.md](README.md) — the project as a whole
- [CONFIG_GUIDE.md](CONFIG_GUIDE.md) — the 5-layer configuration model
- [DEPLOYMENT.md](DEPLOYMENT.md) — running the published images
- [deploy/README.md](deploy/README.md) — Kubernetes manifests with the probes already wired
- [SECURITY.md](SECURITY.md) — hardening and vulnerability reporting
