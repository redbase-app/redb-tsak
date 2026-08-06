# Changelog — redb.Tsak

All notable changes to redb.Tsak are documented in this file.  
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).  
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## Projects

| Project | Description |
|---------|-------------|
| `redb.Tsak.Core` | Kernel: contracts, services, security, cluster, hot-reload, monitoring |
| `redb.Tsak.Core.Pro` | Pro features: EAV key store, cluster topology |
| `redb.Tsak.Worker` | Hosted process: DI wiring, Serilog, Quartz, Dockerfile |
| `redb.Tsak.Contracts` | Shared wire DTOs |
| `redb.Tsak.Client` | `ITsakApiClient` / `TsakApiClient` — HTTP client |
| `redb.Tsak.CLI` | `tsak` command-line tool — 30 commands |
| `redb.Tsak.Web` | Blazor Server dashboard — 10 pages |
| `redb.Tsak.Web.Pro` | Pro web extensions |

---

> **Note on version history:** redb.Tsak has been running in production since version 1.0.0.
> Versions 1.0.0 – 1.0.3 were not published to NuGet (internal deployments only).
> The first public NuGet release is **1.0.4**.

---

## [3.5.0] — 2026-08-05

**No changes in redb.Tsak itself — this is a rebuild onto redb 3.5.0**, released together with the
rest of the ecosystem.

It is not optional housekeeping. The shared-runtime layer is gated on the **minor** version
(`SharedRuntimeBootstrap`): a 3.4.x Worker refuses to start when a 3.5.0 framework is dropped into
`Libs/shared`, because patch-level drift is the only drift the gate allows. Staying a minor behind
would therefore make the whole "swap a DLL instead of re-releasing the runtime" flow unusable, and
would keep two correctness fixes in redb.Core away from every Tsak deployment: a props-cache entry
that could be served stale after in-place mutation, and `RedbHash` being order-dependent for
`Dictionary` properties. Both are described in the root `CHANGELOG.md`.

What ships underneath: `redb.Route` 3.5.0 (Message History, XSLT and Routing Slip EIPs, `{{key}}`
placeholders in endpoint URIs, event-driven IBM MQ receive) and redb core 3.5.0.

## [3.4.0] — 2026-07-27

Minor bump aligning redb.Tsak with the 3.4.0 ecosystem (redb.Core / redb.Route / redb.Identity).
Headline: the **shared runtime layer** — redb.* now lives in `Libs/shared` and is swappable without
rebuilding Tsak. Also: retention sweeps became first-class `cron://` routes.

### Runtime — redb.* framework served from the shared layer (swappable patch DLLs)

**How Tsak lives now:** the redb.* framework and providers (`redb.Core(.Pro)`,
`redb.Route.Core/Http/Quartz/Sql`, `redb.Postgres/MSSql/SQLite (.Pro)`) no longer ship in the
application bin — they live in **`Libs/shared/`** alongside the Route connectors and are loaded from
there at startup. Only `redb.Tsak.*` (+ `redb.Licensing`) stay in the bin. **The payoff:** a
binary-compatible patch of any redb leaf/provider/connector — or a new beta connector — ships by
**swapping its DLL in `Libs/shared/`**, with **no rebuild and no re-spin of the Tsak/Identity
archives**. A framework patch (`3.3.4 → 3.3.5`) is now a file drop, not a release of everything.

- **Early bootstrap.** `SharedRuntime.InstallEarly` runs as the very first statement of the process
  (`Program.cs`), before any redb type is touched: it installs the shared-layer resolver and
  **byte-loads** the framework from `Libs/shared` (file never locked → swappable). Reuses the
  existing `SharedAssemblyLoader` primitives — `LoadedAssemblyTracker` unification (so `.tpkg`
  modules still see one identity of each redb type), per-assembly native resolver
  (`runtimes/<rid>/native`, e.g. librdkafka / e_sqlite3), and version-tolerant forwarding.
- **Out of the bin.** Every redb.\* runtime DLL — the framework/providers **and** the redb.Route.\*
  that leak in transitively (connectors, `redb.Route.Controllers`, the `redb.Route` umbrella) — is
  pruned from the Worker output root at build/publish (`redb.Tsak.Worker.csproj`, glob-based); only
  `redb.Tsak.*` and `redb.Licensing` stay. The framework is byte-loaded early; the rest is served
  on demand by the shared resolver. `Libs/shared/` copies are untouched. Third-party transitive deps
  (Npgsql, MailKit) intentionally stay in the bin (stable, resolve from app-base).
- **Fail-fast.** A missing or corrupt framework DLL in `Libs/shared/` aborts startup **immediately**
  with a precise message (which assembly, where), instead of a `MissingMethodException` a day later
  under load. Scoped to the framework set; the lazy transitive tail stays soft.
- **Compat-gate.** On startup the shared redb.* **minor** is checked against this Tsak build's minor
  (expected minor derived from `redb.Tsak.Core`'s own version). Patch differences are allowed — that
  is the whole point — but a minor mismatch, or a mix of minors inside `Libs/shared/`, aborts start.
- **`GET /api/system/assemblies`** (admin) — what redb is *really* loaded: name, version, and origin
  (shared / bin / runtime). The diagnostic counterpart to a swappable layer ("which redb is running").
- **Tooling.** One data manifest (`scripts/shared-manifest.psd1`) + one parameterized
  `scripts/build-shared.ps1` (dev **and** publish via `-OutRoot`, `-IncludeFramework`) replace the
  hand-duplicated connector list in the removed `publish/scripts/build-shared-multitfm.ps1`. New
  **`scripts/refresh-shared.ps1`** rebuilds one library and drops its DLL into a target
  `Libs/shared/` (dev or an archive's `worker/Libs/shared/`) — the "patch without rebuilding Tsak" flow.

**Ops / build note.** Dev and publish now build the framework into the shared layer:
`scripts/build-shared.ps1 -IncludeFramework` (publish does this automatically via `publish/build.ps1`).
Running the Worker without it triggers the fail-fast (the message says exactly what to run).

**Verified.** Framework 0 in bin root / 12 in `Libs/shared`; clean start loads all 12 from shared and
boots (sqlite Pro, cluster, scheduler); Kafka (+librdkafka), Mail (MailKit), SQLite (e_sqlite3) all
served from shared and exercised end-to-end; `.tpkg` modules unify; fail-fast negative case aborts
with a clear error; compat-gate passes; full Tsak suite green.

### Cluster — node cordon / uncordon (Pro)

Planned maintenance without an abrupt failover: **cordon** a node and it keeps its current work but
takes on no new work and **drains** its route locks to peers; **uncordon** to resume. Previously the
only options were `rebalance` (redistribute everything) or `remove-node` (hard eviction) — no graceful
middle state for a rolling upgrade.

- `POST /api/cluster/nodes/{id}/cordon` / `/uncordon` (admin, audited); `GET /api/cluster/nodes` now
  reports `cordoned`.
- `TsakNodeProps.Cordoned` (durable, orthogonal to `Status` — a cordoned node stays Online) +
  `INodeRegistry.SetCordonedAsync`. A process-local `NodeCordonState` mirror, refreshed from the
  node's own record on each heartbeat, lets the per-route watch loops read the flag cheaply.
- `ClusteredRoutePolicy`: when the node is cordoned, the watch loop stops acquiring route locks and
  releases the ones it holds (drain → peers acquire the freed locks). Existing work keeps running
  until it drains.
- Client `CordonNodeAsync` / `UncordonNodeAsync`, CLI `tsak cluster cordon|uncordon` (+ a Cordoned
  column), and the dashboard **Cluster** page (cordoned badge + per-node Cordon/Uncordon buttons).

**Tests.** Controller cordon/uncordon (set/clear, unknown node → 404, disabled → 400).

### Ops — quick wins: effective config, manual job fire, stricter readiness

- **`GET /api/system/config`** (admin) — the effective (merged, resolved) configuration this node is
  actually running with: a flat `Tsak:*` / `ConnectionStrings:*` key→value map with **secrets
  redacted** (`ConfigRedactor`: sensitive key names → `***`, connection-string passwords masked).
  Answers "what settings is this node on" without SSH. Client `GetConfigAsync`, CLI `tsak system config`.
- **`POST /api/scheduler/fire-job?key=`** (operator, audited) — fire a scheduled job immediately
  (Quartz `TriggerJob`), the "run it now" button. Client `FireJobAsync`, CLI `tsak scheduler fire`.
- **`Tsak:Health:DegradedNotReady`** (default `false`) — when `true`, the readiness probe returns
  `503` on `Degraded` too, not only `Unhealthy` (stricter readiness for deployments that want it).

**Tests.** HTTP end-to-end (real Kestrel) for the DLQ (`ApiDlqIntegrationTests` — list/replay/discard
against a real SQLite DB and a live checkpoint route, plus `available:false` with no DB) and for the
config endpoint (`ApiSystemConfigIntegrationTests` — effective values + secret redaction + auth gate);
controller-level fire-job and Degraded-readiness cases. `TsakTestHarness.CreateWithDlq()` spins up a
real DLQ + checkpoint context for the HTTP tests.

### Feature — dead-letter queue with replay

An exchange that fails at a redb.Route replay checkpoint (`.Replayable("…")`) is now captured,
browsable, and replayable — the operator "show me what failed overnight and re-run it after the
fix" workflow. Builds on the redb.Route replay-checkpoint primitive (`route.checkpoint` +
`IRouteContext.ReplayAsync`); replay tails get fresh, cleaned-up connections (Route closed the
DI-scope-on-replay gap).

**Added**

- **`CheckpointDlqHandler`** — a Tier-3 `IErrorHandler` installed on every module context. On
  failure it reads `route.checkpoint` off the exchange and dead-letters it. **Opt-in by
  construction**: only routes carrying a `.Replayable()` marker leave a checkpoint, so only those
  are captured — the DLQ never fights a broker/transaction that already owns redelivery.
- **`tsak_dlq`** — a flat table (PG/MSSQL/SQLite), created on startup like the audit table. Stores
  the serialized snapshot (body + headers + scalar properties), route, marker, exception, status.
- **`ExchangeSnapshotCodec`** — serializes/rehydrates the snapshot for durable storage. `byte[]`
  and `string` round-trip exactly; other bodies via System.Text.Json (exact CLR type restored when
  its assembly is loadable). A non-serializable body is stored visible but marked **not replayable**
  — it never breaks capture.
- **`ExchangesController`** — `GET /api/exchanges/failed` (filtered, paged, `operator`),
  `POST /api/exchanges/{id}/replay` (`operator`, audited), `DELETE /api/exchanges/{id}` (`admin`,
  audited). Replay rehydrates the snapshot and calls `ReplayAsync`; the route tail resolves fresh
  redb/SQL connections lazily.
- DLQ retention as a first-class `cron://tsak-dlq-retention` route on the `_system` context
  (`Tsak:Dlq:RetentionDays`, default 30) — visible in the Routes API/dashboard and the scheduler page.
- Client (`GetFailedExchangesAsync` / `ReplayExchangeAsync` / `DiscardExchangeAsync`), CLI
  (`tsak dlq list|replay|discard`), and a **Dead-letter** dashboard page (server-side filters/paging,
  per-row Replay/Discard).

**Semantics.** Replay is **at-least-once + manual** — the tail may run more than once, so replayed
side-effects must be idempotent. Not Temporal-style durable execution.

**New configuration:** `Tsak:Dlq:Enabled` (default `true`), `Tsak:Dlq:RetentionDays` (default `30`).

**Tests.** `DlqTests` — codec round-trips (byte[]/string/POCO/null/non-serializable) and an
end-to-end capture → query → replay → discard over a real SQLite database driving a real route
context's checkpoint tail (prefix not repeated).

### Reliability — staged `.tpkg` validation before hot-swap

A package update tore the running version down *before* loading the new one: `ReloadPackageAsync`
unregistered the old modules and disposed the old ALC, then opened the new `.tpkg` — so a broken
package left the context with no modules. (Bare-DLL hot-swap already auto-rolled-back; the package
path did not.)

**Added**

- **Staged validation** in `HotReloadService.ReloadPackageAsync`: the new `.tpkg` is first opened in
  a throwaway collectible ALC (`forceReload:false`, so the shared assembly tracker is not mutated and
  running modules are untouched) and checked that it loads and discovers at least one module. Only
  then is the old version torn down. A package that fails to open or has no modules is refused with a
  logged reason and the **current version keeps running**.
- `POST /api/modules/validate` (admin) — dry-run: the same structural + signature checks as upload,
  installing nothing. For CI to verify a package before deploying. Client `ValidateModuleAsync`, CLI
  `tsak module validate`.

**Tests.** `ModuleDeploymentTests` extended with dry-run validation cases (valid signed, bad ZIP,
invalid signature).

### Config — redb cache options exposed via `Tsak:Redb:Cache`

Tsak previously passed only `PropsSaveStrategy` and `EnsureCreated` through to redb, so the redb
cache tuning knobs could not be set from a Tsak node's configuration — they ran on redb's defaults.

**Added** the optional `Tsak:Redb:Cache` section, mapped onto the redb configuration in
`ConfigureRedb` for both the Pro and Free tiers. Every key is optional and defaults to redb's own
default, so an absent section changes nothing:

- Props cache: `EnableProps` (false), `PropsMaxSize` (10000), `PropsTtlMinutes` (60).
- `SkipHashValidationOnCacheCheck` (false) — trust the cache without re-checking the object hash in
  the database. Faster, but single-writer only.
- List cache: `EnableList` (true), `ListTtlMinutes` (5).
- Metadata cache: `EnableMetadata` (true), `MetadataTtlMinutes` (30).
- `AutoRecomputeHash` (true), `CacheDomain` (derived from the connection string when empty).

**Safety.** Enabling `SkipHashValidationOnCacheCheck` together with `Tsak:Cluster:Enabled=true`
logs a startup warning — skipping cache hash validation can serve stale data across cluster nodes
writing to the same database. (Lazy props loading is intentionally not exposed; it stays off.)

The Worker's default `appsettings.json` now lists the whole section with its default values, so the
available knobs are visible and editable in place.

### Feature — module upload & rollback via API (signed)

Deploying a module previously meant filesystem access to the module directory. Modules can now be
uploaded and rolled back over the API — but since a module is **code Tsak loads in-process**, the
whole feature is built around a signature trust anchor and safe-by-default switches. Full trust
model and signing walkthrough: **[MODULE_DEPLOYMENT.md](MODULE_DEPLOYMENT.md)**.

**Added**

- `POST /api/modules/upload` (admin, audited) — accepts a `.tpkg` body with a detached signature in
  the `X-Tsak-Signature` header. **Disabled by default** (`Tsak:Modules:Upload:Enabled=false`) — a
  node that doesn't need remote deploy exposes no upload surface at all.
- `POST /api/modules/{name}/rollback` (admin, audited) — restores the previous on-disk version
  (`KeepVersions` packages kept as `{name}.tpkg.v{n}`).
- `ModuleSignatureVerifier` — RSA/ECDSA detached-signature verification, BCL-only (no cosign
  dependency).
- **Load-boundary enforcement** in `HotReloadService`: with `Tsak:Modules:Signature:Required=true`
  and a configured public key, **every** `.tpkg` — uploaded *or* dropped into the directory by an
  operator — must carry a valid `.tpkg.sig` or it is refused before any of its code loads. The
  public key, not filesystem access, becomes the trust anchor. Stricter than the WSO2 MI default.
- CLI: `tsak module keygen` (generate an ECDSA key pair), `tsak module sign` (sign a `.tpkg` →
  `.tpkg.sig`), `tsak module deploy` (upload), `tsak module rollback`. Client:
  `UploadModuleAsync` / `RollbackModuleAsync`.

**Upload-time guards** (in `ModuleUploadService`): size ceiling, valid-ZIP + manifest check, the
stored name is taken from the manifest and sanitized (no `/`, `\`, `..` — path-traversal / zip-slip
safe), fail-fast signature verification, atomic install (temp → move), previous version archived.

**New configuration:** `Tsak:Modules:Upload` (`Enabled`, `MaxSizeMB`, `TargetPath`, `KeepVersions`,
`RequireSignatureForUpload`) and `Tsak:Modules:Signature` (`Required`, `PublicKeyPath` /
`PublicKeyPem`).

**Tests.** `ModuleDeploymentTests` — signature round-trip / tamper / wrong-key / RSA, and every
upload guard (disabled, oversize, bad ZIP, missing manifest, unsafe name, signature required /
missing / invalid / valid) plus rollback.

### Feature — watchdog alert delivery

The watchdog detected hung / suspected exchanges but no one was notified — alerts only
accumulated behind `GET /api/watchdog/alerts`, a poll nobody runs at 3 a.m. Alerts are now
pushed to configurable channels.

**Added**

- `AlertDispatcher` — fans new alerts out to every enabled channel. **Fire-and-forget** (bounded
  queue + background pump, like the audit sink): the watchdog scan never blocks on a slow SMTP
  server, and a broken alert backend can never take the node down. **Dedup** by
  `context+route+exchange+level` within `DedupWindowMinutes` — the scan rebuilds its snapshot
  every cycle, so without this a hung exchange would re-page every tick.
- Channels, **all off by default**, each with its own config and connection params:
  - **webhook** — POSTs alert JSON to a URL (Slack / Teams / PagerDuty / any collector). Native HTTP.
  - **telegram** — Bot API (`sendMessage`). Native HTTPS — no connector.
  - **email** — SMTP via the BCL `SmtpClient`. No extra package.
  - **endpoint** — generic: sends to any redb.Route producer URI (`kafka:`, `rabbitmq:`, `amqp:`,
    `sqs:`, `mqtt:` …) via a `ProducerTemplate`. One channel covers every broker with zero
    per-connector code; the component is supplied by the host, so **no broker ever becomes a
    compile-time dependency of Core**.
- `POST /api/watchdog/test-alert` (role `operator`) — sends a synthetic alert through every
  enabled channel and returns the per-channel outcome, so an operator can verify configuration
  without waiting for a real hung exchange. Bypasses the dedup window.
- `GET /api/watchdog/alerts/status` — whether delivery is active and which channels are enabled.
- Dashboard: an **Alert Delivery** panel on the Watchdog page — status, enabled channels, and a
  "Send test alert" button with per-channel results. Client: `GetAlertStatusAsync` / `TestAlertAsync`.

**New configuration:** the `Tsak:Watchdog:Alerts` section (`Enabled`, `MinLevel`,
`DedupWindowMinutes`, and `Webhook` / `Telegram` / `Email` / `Endpoint` sub-sections).

**Tests.** `AlertDeliveryTests` — level filter, dedup window, channel isolation, `TestAsync`
outcomes, and an end-to-end webhook delivery against a real in-process `HttpListener`.

### Architecture — `IModuleHealthContributor` moved to `redb.Tsak.Contracts`

The per-module health SPI lived in `redb.Tsak.Core`, so any module implementing it (e.g.
redb.Identity) had to compile against the entire Tsak host and its transitive graph — and
adding the SQL-audit `Route.Sql` reference to `redb.Tsak.Core` would have leaked into every
such module. The interface depends only on `HealthStatus`, which already lives in
`redb.Tsak.Contracts`, so it now lives there too.

- `IModuleHealthContributor` moved from `redb.Tsak.Core` (namespace `redb.Tsak.Core.Contracts`)
  to `redb.Tsak.Contracts` (namespace `redb.Tsak.Contracts`).
- **Breaking for implementers**: change `using redb.Tsak.Core.Contracts;` to
  `using redb.Tsak.Contracts;`. A module implementing this interface can now reference only the
  lightweight `redb.Tsak.Contracts` assembly and drop its `redb.Tsak.Core` reference entirely.
- `IHealthContributor` stays in `redb.Tsak.Core`: it exposes the mutable `HealthEvaluation`
  bag, a host-internal type.

### Security — persistent admin audit trail

Admin actions were audited only to the log (`LogAdminAuditService`), and the lifecycle audit
lived in a 1000-entry in-memory ring — after a restart there was no record of who did what.
The audit is now persisted to a flat, queryable table.

**Added**

- `tsak_audit_log` — a flat table (not a redb object: append-only, grows, fixed schema),
  created on startup by `AuditSchemaInitializer` for the configured provider. Mirrors the
  existing `QuartzSchemaInitializer`: switch over `Tsak:Redb:Provider`, one embedded idempotent
  DDL script per dialect (Postgres / SQL Server / SQLite), raw ADO.NET.
- `RouteAdminAuditService` — the effective `IAdminAuditService`. Writes each event
  **fire-and-forget** through the `direct://tsak-audit` route (`Sql.Execute` INSERT), so an API
  call never waits for the database and a broken audit backend can never take the node down.
  A bounded queue drains on a background pump; on backend failure the event falls back to the
  log sink, and under sustained flood the oldest queued events are dropped with a warning.
- `GET /api/audit` (`AuditController`, role `admin`) — filtered, paged, newest-first, with
  server-side filtering only (no in-memory scans).
- Audit retention as a first-class `cron://tsak-audit-retention` route on the `_system` context —
  prunes entries older than `Tsak:Audit:RetentionDays` (default 90; `0` keeps forever). Visible in
  the Routes API/dashboard and the scheduler page.
- The sink is an endpoint on purpose: the same event stream can be pointed at a file, broker
  or HTTP collector by configuration — the pattern the watchdog-alert work will reuse.

**No database configured (standalone / in-memory):** audit stays on `LogAdminAuditService`,
now emitting a `[tsak-audit]`-anchored JSON line (anchor first, one JSON object after it) so a
standalone deployment can grep and parse it with no log-format handling.

**Storage details:** `payload` is `jsonb` (Postgres) / `nvarchar(max)` (SQL Server) / `TEXT`
(SQLite); timestamps are `TEXT` ISO-8601 in SQLite (readable, lexically sortable — Tsak's own
table, not part of the redb REAL-Julian convention). Indexed by time, actor and action.

**New configuration:** `Tsak:Audit:Enabled` (default `true`), `Tsak:Audit:RetentionDays`
(default `90`).

**Consumers.** `ITsakApiClient.GetAuditAsync` (server-side filters + limit/offset paging), the
`tsak audit` CLI command (`--actor/--action/--target/--since/--until/--limit/--offset`), and a
new **Audit** dashboard page — filters and paging are all server-side, so the (potentially
large) table is never pulled into the browser.

**Tests.** `AuditStorageTests` (dialect SQL + event→header mapping) and `AuditPersistenceTests`
(end-to-end write→read→filter→prune over a real SQLite database).

### Security — role enforcement on the management API

API keys have carried roles since 1.0.0, and `AuthorizeProcessor` has always been able to
check them — but no endpoint ever declared a requirement, so the check was never armed. With
`Tsak:Auth:Enabled=true` any valid key could call any endpoint: a key issued for read-only
dashboard access could force-stop a production route, remove a module, or mint itself a new
key. `viewer` was functionally equivalent to `admin`.

**Added**

- `RequiresRoleAttribute` — declares the role(s) an action (or a whole controller) needs.
  Multiple roles are OR-ed; a method-level attribute overrides the controller-level one.
- `NoRoleRequiredAttribute` — marks technical endpoints that must never answer `403`.
- `TsakRoles` — the `viewer` &lt; `operator` &lt; `admin` ladder, with `reader`/`ops` synonyms.
  Custom roles are matched by exact name only.
- `RoleAuthorizationProcessor` — enforcement, wired into the `_system` pipeline immediately
  after a successful auth check. Resolves the target action through the same
  `ControllerRegistry` the dispatcher uses, so both agree on what the request would hit.

**Endpoints now requiring `admin`:** `/api/auth/*` (all, including reads), `/api/users/*`,
`DELETE /api/contexts/{name}`, `DELETE /api/modules/{name}`, route `force-stop`,
`POST /api/cluster/rebalance`, `DELETE /api/cluster/nodes/{id}`.
**Requiring `operator`:** `/api/diagnostics/*` and `/api/logs/*` (both expose internals), plus
every mutating endpoint by default. **Requiring `viewer`:** every other `GET`.

**Technical endpoints are never gated.** The check runs only for authenticated exchanges, so
auth-exempt Kubernetes probes pass through untouched; `HealthProbeController` is additionally
marked `[NoRoleRequired]` so it cannot start answering `403` if an operator narrows
`Tsak:Api:AuthExempt`. The echo and Prometheus routes have their own pipelines and never
reach the check.

**Compatibility.** Nothing changes when auth is disabled. Keys that carry no roles keep full
access and log a one-time warning — set `Tsak:Auth:RolelessKeysAreAdmin=false` to deny them
once every key has been re-issued with explicit roles. Enforcement as a whole can be switched
off with `Tsak:Auth:EnforceRoles=false`.

**New configuration:** `Tsak:Auth:EnforceRoles` (default `true`),
`Tsak:Auth:RolelessKeysAreAdmin` (default `true`).

**Tests.** `RoleAuthorizationTests` (32) covers the processor and the role ladder;
`ApiRoleIntegrationTests` (27) drives the real Kestrel pipeline with `viewer`, `operator` and
roleless keys, including proof that probes answer without a key and never return `403`.
`TsakTestHarness.CreateWithAuth` now takes the key's roles and extra configuration.

### Fixed — two stale tests

- `SchedulerControllerTests.ConfigureQuartz_WithoutSection_SkipsRegistration` asserted that no
  scheduler is registered without a `Quartz` config section. That behaviour was deliberately
  removed when Tsak started always handing out one shared `IScheduler` (otherwise a cron
  consumer builds its own RAMJobStore that `_system` cannot see, and the dashboard shows no
  jobs while the route runs). Renamed to `..._RegistersSharedRamScheduler` and re-pointed at
  the intended behaviour: factory registered, `RAMJobStore` defaulted, `IScheduler` a singleton.
- `ClientIntegrationTests.GetClusterStatusAsync_ReturnsStatus` failed with
  `No action matches GET /api/cluster`: when the shared `TsakTestHarness` was extracted, the
  `ISystemContextPlugin` registration was lost, so the Pro `ClusterController` was never
  mounted. The harness now contributes the Pro controller assembly the way a real deployment
  does — without registering cluster services, so the controllers report `Enabled = false`.

### Documentation

- New root-level `API_GUIDE.md` — endpoint map (56 endpoints / 14 controllers), request
  pipeline, auth and role model, health endpoints, extension points, configuration keys.
- Corrected stale endpoint documentation: `HealthProbeController` was documented at `/healthz`
  (it is `/api/health`), the controller table listed a `MetricsController` that does not exist
  and omitted `LifecycleController`, and endpoint counts were stated as 32/12.

---

## [3.3.3] — 2026-07-15

> **Why the bump.** **No functional changes to redb.Tsak.** Rebuilds the distribution (Docker images +
> standalone archives) on top of **redb.Route 3.3.3** and **redb.Core 3.3.3**.
>
> Two reasons:
> 1. **`redb.Core` 3.3.3 fixes schema init under a non-superuser database owner.** Tsak depends on redb
>    storage, so a Tsak deployment on a least-privilege database could not initialize its schema on
>    first start. Rebuilding is the only way that fix reaches Tsak users.
> 2. **One number for the ecosystem.** The family had drifted (core 3.3.0, Route/Tsak 3.3.1,
>    Route.Sql/Sqs 3.3.2). From 3.3.3 redb core, redb.Route and redb.Tsak all ship the same number, so
>    a Tsak image no longer needs a compatibility table to say which Route it carries.
>
> The version jumps 3.3.1 → 3.3.3 (no 3.3.2 for Tsak) to land on that shared number.

## [3.3.1] — 2026-07-10

> **Why the bump.** Rebuilds the redb.Tsak distribution (Docker images + standalone archives)
> on top of **redb.Route 3.3.1**, which fixes header ↔ property round-tripping across five connectors
> and adds fluent `string` overloads. No `redb.Tsak.Core` code changes — this is a distribution rebuild
> so bundled tsak routes pick up the connector fixes. Binary version moves **3.3.0 → 3.3.1**.

### Changed
- **Dashboard default port 8080 → 8085.** The Web UI / Stack image bound the widely-used port `8080`
  by default (frequent dev-machine conflict); it now binds **`8085`** — images (`ASPNETCORE_URLS`,
  `EXPOSE`), stack supervisord, archive start scripts, and example compose defaults
  (`${WEB_PORT:-8085}:8085`). Worker port (`9090`) unchanged; override via `WEB_PORT` /
  `ASPNETCORE_URLS`.
- Bundled connectors updated to **redb.Route 3.3.1**:
  - **RabbitMQ / AMQP** — full AMQP property ↔ header round-trip (a consume→produce hop carries
    CorrelationId, ReplyTo, Priority, Timestamp, …); standard properties use their bare well-known
    names, the `redbRmq.*` / `redbAmqp.*` prefix stays for delivery/transport metadata.
  - **IBM MQ** — standard, JMS-equivalent MQMD fields forward from headers by default; raw MQMD
    fields stay behind `MqmdWriteEnabled`.
  - **Redis** — prefixed `StreamFields` header; **Azure Service Bus** — batch send now sets native +
    application properties.
  - **Fluent DSL** — `string` overloads on expression-first builders (RabbitMQ, Sftp, Ftp, File,
    MqttNet, Kafka, Redis, Http): `.Host("localhost")` compiles and `${...}` still interpolates.

## [3.3.0] — 2026-07-09

### Added
- **Amazon SQS + SNS connector (`redb.Route.Sqs`)** available to bundle — `sqs://` (queue) and
  `sns://` (topic) endpoints for tsak routes. Wired into the distribution at release.
- **Telegram Bot connector (`redb.Route.Telegram`)** available to bundle — `telegram://` long-polling
  consumer + send/document/photo/edit/delete/answer producer (429 rate-limit handling, parseMode,
  inline/reply keyboards, webhook unpack) for tsak routes. Wired into the distribution at release.

### Fixed
- **`redb.Tsak.Core` — the dashboard scheduler page now shows cron jobs even without a `Quartz` config
  section.** Tsak now **always** hands out one shared `IScheduler` (falling back to an in-memory
  `RAMJobStore` when no `Quartz` section is configured), injected into every route context. Previously,
  with no `Quartz` section Tsak registered no scheduler, so a cron route's consumer self-created a
  *per-context* in-memory scheduler that the management API's `_system` context could not see — the
  scheduler page showed nothing even though the route ran and was listed. No clustering or database is
  required for a single node; `AdoJobStore` remains the way to persist and share jobs across processes /
  cluster nodes. (Standalone `redb.Route` — no Tsak host — is unchanged: the Quartz consumer still
  self-creates its own scheduler when the host provides none.)
- **`redb.Tsak.Core` — Users admin API (`UsersController`) now uses a per-request scoped
  `IRedbService`.** It previously resolved `Context.GetRedbService()` — the shared captive singleton
  (one non-thread-safe connection) — so concurrent admin requests could contend and fail with
  *"A command is already in progress"*. It now resolves the per-request instance via
  `controller.Redb()` (redb.Route.Core 3.3.0), giving each request its own connection.

## [3.2.2] — 2026-07-03

> **Why the bump.** Rebuilds the redb.Tsak distribution (Docker images + standalone
> archives) on top of **`redb.Route.RabbitMQ` 3.2.2**, which fixes two production bugs in
> the RabbitMQ connector and adds a consumer option. The Tsak binary version moves
> **3.2.0 → 3.2.2** (3.2.1 was a Web/dashboard + archive-only fix that never rebuilt the
> binary distribution). No Tsak API or configuration changed — this is a connector refresh
> plus a full image/archive re-release.

### Changed

#### Bundled connectors — `redb.Route.RabbitMQ` refreshed to 3.2.2

The RabbitMQ connector shipped in the shared-assembly layer
(`redb.Tsak.Worker/Libs/shared/`, built from source by `scripts/build-shared.ps1` /
`publish/scripts/build-shared-multitfm.ps1`) was **3.2.0**. It is rebuilt to **3.2.2**,
which brings:

- **Consumer dispatch concurrency actually works.** The connector previously created every
  channel with the AMQP consumer-dispatch concurrency pinned to `1` (a `RabbitMQ.Client`
  7.2.1 `CreateChannelOptions` ctor-default trap), so a RabbitMQ route processed messages
  strictly one at a time regardless of `ConcurrentConsumers`. `ConcurrentConsumers(N)` is now
  the single knob for consumer parallelism — up to **N** messages processed concurrently.
  **Behaviour note:** a module whose RabbitMQ route sets `ConcurrentConsumers(N > 1)` now runs
  genuinely concurrently, so per-queue ordering is no longer preserved on that route and its
  pipeline must be thread-safe. Routes at the default `1` stay serial, unchanged.
- **No more AMQP channel leak on per-route Stop/Start.** Suspending/resuming a single RabbitMQ
  route from the dashboard (or via the management API) previously leaked one idle channel per
  cycle; the connector now releases its consume channel on stop.
- **New `AutoAck` consumer option** (default off) — broker-side auto-acknowledge / at-most-once,
  the RabbitMQ analogue of the Kafka `EnableAutoCommit` option.

See the `redb.Route` CHANGELOG `[3.2.2]` for the connector-level details.

#### Re-release — bundled `redb.Route.Amqp` / `redb.Route.IbmMq` refreshed to 3.2.1

The `3.2.2` images and archives were **rebuilt and re-published (same version)** to fold in the
**Amqp 3.2.1** and **IbmMq 3.2.1** hotfix: `ConcurrentConsumers(N)` on those two transports was a
no-op (a dead semaphore on a serial receive loop) and now runs **N real competing consumers**.
Same behaviour note as RabbitMQ above — `N > 1` processes concurrently (per-destination ordering
not preserved; default `1` stays serial); IBM MQ topics clamp to a single subscriber. Every other
bundled connector is unchanged. See the `redb.Route` CHANGELOG for details.

### Released

- Docker images `redb-tsak-worker` / `redb-tsak-web` / `redb-tsak-stack` at **3.2.2** (.NET 9;
  tags `:3.2.2-net9`, `:3.2.2`, `:latest`), pushed to `ghcr.io/redbase-app` and cosign-signed.
- Standalone archives `redb-tsak-3.2.2-linux-x64.tar.gz` / `redb-tsak-3.2.2-win-x64.zip` with
  `checksums.txt` and per-archive cosign `.bundle` signatures, attached to the `v3.2.2` GitHub
  release. The bundled route connectors are .NET 9 (same as 3.2.0).

---

## [3.2.1] — 2026-07-02

### Fixed

#### `redb.Tsak.Web` — Endpoints page hid anonymous contexts while still counting them

The Endpoints dashboard page filtered the context list with `!c.IsAnonymous`, so a
module loaded without an explicit `ContextName` — which runs in an anonymous `_dyn_`
context — never appeared in the list, even though its endpoints were still tallied in
the **Total / Active / Consumers** stat cards. The visible rows therefore disagreed
with the counts. The filter is removed: every context now renders, matching both the
stat cards and the other dashboard pages (which never hid anonymous contexts). To
control a module's display name, give it a stable `ContextName` via
`{Module}.config.json`.

#### Standalone archive — Web UI started on port 5000 instead of the documented 8080

The release archive's `sanitize-appsettings.ps1` blanks the Web `Kestrel` section,
and the generated `start-web` / `start-stack` scripts set no `ASPNETCORE_URLS`, so the
standalone dashboard fell back to ASP.NET Core's default `http://localhost:5000` — while
`README.txt` (and the Docker images, which set `ASPNETCORE_URLS=http://+:8080`) advertise
**8080**. The `build-archives.ps1` script generators now default the Web process to
`http://localhost:8080` (respecting a pre-set `ASPNETCORE_URLS`); the Worker still binds
`9090` from its own config. No change to the Docker images.

---

## [3.2.0] — 2026-06-29

> **Why the bump.** Tracks redb.Route 3.2.0 (hosted connector set), adds a
> built-in auth-exempt echo / liveness route to the management facade, wires an
> opt-in OTLP/Jaeger trace exporter, and ships ready-to-use k8s + Grafana +
> local-stack deployment assets. No public API was removed or renamed; both
> observability exporters stay off by default.

### Added

#### `redb.Tsak.Core` / `redb.Tsak.Worker` — SQLite as a first-class storage & scheduler provider

`Tsak:Redb:Provider` now accepts **`sqlite`** alongside `postgres` and `mssql`,
running the whole host (redb storage + Quartz job store) on an embedded SQLite
database. Set `ConnectionStrings:Sqlite` (e.g. `Data Source=redb.db`) and
`Tsak:Redb:Provider: "sqlite"` — `ConfigureRedb` wires `redb.SQLite` / `redb.SQLite.Pro`
(`UseSqlite`), same as the other providers.

- **Quartz AdoJobStore on SQLite.** `QuartzSchemaInitializer` gained a `sqlite`
  branch: it reads the `Sqlite` connection string, opens a `SqliteConnection`, and
  applies the embedded `QuartzSchema.Sqlite` script before the scheduler validates
  its tables — mirroring the existing Postgres/SQL Server paths. The new
  `Quartz/Scripts/tables_sqlite.sql` is embedded (`LogicalName="QuartzSchema.Sqlite"`)
  and is **idempotent** (`CREATE TABLE/TRIGGER IF NOT EXISTS`, no `DROP`), so existing
  Quartz state survives restarts.
- **Clustering note.** Quartz's own `quartz.jobStore.clustered` mode is **not**
  supported on SQLite (Quartz refuses it due to file-locking); set it to `false`.
  This is independent of the Tsak cluster (`Tsak:Cluster:Enabled`), which is
  redb-backed and runs on SQLite unchanged.

#### `redb.Tsak.Core` — `system-echo` auth-exempt echo route on the management facade

`SystemContextBuilder` now registers a second route, **`system-echo`**, in the
`_system` context. It reflects the incoming request straight back as JSON
(`status: "alive"`, `method`, `path`, `url`, `query`, `contentType`,
`remoteAddress`, `body`, `receivedAt`) — a lightweight "is the host reachable,
and what did it receive?" probe for debugging and reverse-proxy header checks.

- **No API key.** The route has its own `Process` pipeline that bypasses the
  facade's `AuthorizeProcessor`, so it answers without authentication —
  deliberately, so a liveness check needs no credentials.
- **`AutoStart=false`.** It ships dormant and only starts when an operator
  starts it from the Routes API / dashboard, keeping a request-reflecting
  endpoint off by default.
- **Shares the main API port.** The concrete path (default `/api/echo`,
  configurable via `Tsak:Api:Echo:Path`) out-ranks the catch-all controller
  dispatcher thanks to redb.Route.Http's new route-specificity ordering, so no
  separate port is needed. While the echo route is stopped, `/api/echo` falls
  through to the dispatcher (404, behind auth) as before.

#### `redb.Tsak.Core` — OTLP trace exporter (Jaeger / collector)

The OpenTelemetry pipeline previously **collected** route/step spans
(`AddSource(...)`) but exported them **nowhere** — the README's "OTLP exporter"
claim was aspirational. `ConfigureMonitoring` now attaches a real
`AddOtlpExporter` when **`Tsak:Tracing:Otlp:Enabled=true`**, sending spans to
`Tsak:Tracing:Otlp:Endpoint` (default `http://localhost:4317`, `Protocol`
`grpc` or `http/protobuf`). Jaeger ingests OTLP natively, so no Jaeger-specific
exporter is needed. The pipeline is also decoupled from the Prometheus toggle:
it now activates when **either** metrics **or** tracing is enabled (previously
tracing only registered if the Prometheus exporter was on). Adds a
`OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.15.0 package reference. Off by
default. Module `ActivitySource`s advertised via
`Tsak:Metrics:Prometheus:AdditionalSources` flow to the same exporter. The OTel
resource `service.name` is configurable via **`Tsak:Tracing:ServiceName`**
(default `redb-tsak-worker`) — without it the collector reports the unhelpful
`unknown_service:redb.Tsak.Worker`.

#### `redb.Tsak.Core` — Prometheus metrics served through the facade (no URL ACL, no extra port)

Metrics are now exposed at **`/metrics` on the facade** (the API port, default 9090),
auth-exempt, instead of on a separate wildcard `HttpListener`. The OTel scrape listener
binds **loopback** (`localhost:<Port>`, default 9464) and a `system-metrics` route on the
facade's Kestrel server proxies it out. Consequences:

- **No Windows URL ACL / admin.** Kestrel binds sockets directly and the loopback OTel
  listener needs no ACL on any OS. The previous `http://*:9464/` bind required
  `netsh http add urlacl` (or admin) on Windows and otherwise threw
  `HttpListenerException (5) Access denied` straight out of hosting startup — taking the
  whole Worker (and every hosted module) down.
- **One port.** Scrape `http://<host>:9090/metrics`; there is no separately-exposed 9464.
  `Tsak:Metrics:Prometheus:Port` now only sets the internal loopback port.
- **Non-fatal.** The bind is pre-flighted: if it ever fails, Tsak logs a warning and runs
  without metrics rather than crashing — an optional exporter must never take the host down.

#### Deployment & observability assets (`deploy/`)

New ready-to-use assets under `redb.Tsak/deploy/`:

- **`k8s/`** — `Deployment` (probes correctly wired to `/api/health/*`, OTLP env,
  graceful-termination headroom), `Service` (9090), and a Prometheus-Operator
  `ServiceMonitor` scraping `/metrics` on the API port.
- **`grafana/redb-tsak-dashboard.json`** — importable dashboard (throughput,
  error ratio, p50/p95/p99 latency, in-flight, plus .NET runtime panels) using
  the **exact** exported series names (e.g.
  `redb_route_exchanges_processed_exchanges_total`,
  `redb_route_exchange_duration_milliseconds_bucket`).
- **`observability/`** — a local `docker compose` stack: Prometheus + Grafana
  (auto-provisioned datasource & dashboard) + Jaeger all-in-one. Prometheus is
  published on host **9091** (container 9090) so it does not collide with Tsak's
  own management API on 9090.
- **`deploy/README.md`** — toggles, the metric-name reference table, and the
  OTLP/Jaeger setup.

### Fixed

#### `redb.Tsak.Core` — `RegisterNamedRedbServices` no longer eagerly opens connections for inactive named instances

`TsakContextManager.RegisterNamedRedbServices` used to iterate every entry in
`Contexts.{ctx}.Redb` and run `InitializeAsync(ensureCreated:true)` on each one
when its `EnsureCreated` flag was true. That meant a dev keeping both
`identity-pg` and `identity-sqlite` in `context.json` (the canonical toggle
example shipped with redb.Identity) would have the host attempt to connect to
**every** registered backend at startup, even when only one was selected via
top-level `RedbInstanceName`. Stopping the non-active backend's server (e.g.
`pg_ctl stop` while debugging SQLite) produced a `Failed to ensure schema` /
connection-refused warning and, on Windows where SYN-retry hangs ~30 s instead
of fast-failing, a 30-second startup stall per non-active backend.

`RegisterNamedRedbServices` now picks the active instance for the context via
the new `ResolveActiveRedbName` helper (top-level `RedbInstanceName`, then
`Identity:RedbInstanceName` as a module-convention fallback). Non-active
entries are still **registered** — `"redb-factory:{name}"` resolves and the
container is built — but their `EnsureCreated` is overridden to `false` so no
schema bootstrap and no eager connection happen. Backends open lazily at first
real use, matching how an external production datastore would behave.
Backward-compatible: when neither key is set, the legacy "init every entry"
behaviour is preserved.

#### Docs — Kubernetes health-probe paths corrected (`/api/system/health/*` → `/api/health/*`)

The README's K8s section pointed `startupProbe` / `livenessProbe` /
`readinessProbe` at `/api/system/health/{startup,live,ready}` — endpoints that
**do not exist** (the probe controller is `[Route("/api/health")]`) and are
**not** in the `Tsak:Api:AuthExempt` default (`/api/health/*`). Following the
old docs with auth enabled meant probes returning 404/401, the startup probe
never succeeding, and the pod CrashLoopBackOff'ing. The probe table, the YAML
snippet, and the "endpoints requiring an API key" note now reference the real
auth-exempt `/api/health/*` paths.

## [3.1.0] — 2026-06-07

> **Why a minor bump (3.0.x → 3.1.0).** Tsak is a runtime container for
> redb.Route contexts — every Tsak module is a redb.Route assembly hosted by
> the Worker. redb.Route 3.1.0 ships **four new packages** (`redb.Route.Llm`,
> `redb.Route.Llm.Abstractions`, `redb.Route.Llm.Tools`, `redb.Route.Exec`),
> a **new URI scheme** (`exec:`), a **second LLM provider** (native
> `AnthropicProvider`) and a **persistence extension** (`AddRedbLlmStorage`,
> 5 stores + 9 REDB schemas). All of this becomes available to Tsak-hosted
> modules **with no changes to the Worker** — drop a `.tpkg` that
> ProjectReferences the new connectors and your module gets agent loops, tool
> dispatch, conversation memory and shell tooling end-to-end. The bump is
> minor because no public Tsak API was removed or renamed.

### Changed
- Updated all `redb.Route.*` connector dependencies to `3.1.0`.
- Updated `redb.Tsak.Core.Pro` dependency to `3.1.0`.

### What this enables for Tsak modules (inherited from redb.Route 3.1.0)
- **Agent routes inside `.tpkg` modules.** A module can now `From("llm:…")`
  or `.To(LlmDsl.Factory("…"))` and get a complete agent loop —
  iterations, tool dispatch, stop-reason mapping — managed by the same
  Worker that already manages your Kafka / SQL / HTTP routes. Hot-reload,
  clustering, OTel and the dashboard work without modification.
- **Tools out of any transport.** `.AsLlmTool("name")` exposes any
  `From(uri)` route as an LLM-callable tool — Kafka, SQL, HTTP, SFTP, even
  the new `exec:` — with **zero connector version bumps** (the descriptor
  contract lives in the small `redb.Route.Llm.Abstractions` package).
- **Two LLM providers shipped.** Universal OpenAI-compatible provider
  covering 14 vendors (OpenAI, Anthropic, Groq, Cerebras, OpenRouter,
  Gemini, GitHub Models, Mistral, Together, HuggingFace, DeepSeek, Ollama,
  LM Studio, custom) **plus** a native Anthropic Messages-API provider
  with full SSE streaming and proper `429 / 529 / 5xx` mapping
  (`LlmRateLimitException` / `LlmTransientException`).
- **Conversation memory + tool idempotency on REDB.** The new
  `AddRedbLlmStorage()` extension wires five stores (`IConversationStore`,
  `IApprovalStore`, `ICostBudgetStore`, `IToolIdempotencyStore`,
  `IAgentObserver`) onto the Tsak host's existing REDB connection — no
  extra database, no extra migration step.
- **Local-process tool (`exec:`) as a first-class transport.** Allowlist
  + working-directory pin + timeout + output caps; usable from any module
  for build/log inspection, file conversions, or scripted ops without
  shelling out by hand.

### Recommended for Tsak module authors
- Reference `redb.Route.Llm` (and optionally `redb.Route.Llm.Tools`) from
  your `.tpkg` csproj at `3.1.0` — the existing `RouteContextLoader` /
  hot-reload path needs no changes.
- For agent-tool exposure of existing endpoints, reference only
  `redb.Route.Llm.Abstractions` — your transport packages can stay on
  whatever version they were on; `.AsLlmTool()` is purely additive.

---



### Changed
- Updated all `redb.Route.*` connector dependencies to `3.0.1`.

### Fixed
- **Web (standalone)**: `NodeDetail` page kept logging
  `[WRN] Node default is not alive, skipping API calls` once per polling tick
  (every 10s) and rendered empty tabs when navigated to via the sidebar.
  Root cause: `StandaloneClientProvider.LoadTopologyAsync` cached the
  topology with `LastHeartbeat = UtcNow` once and never refreshed it,
  so `NodeInfo.IsAlive` (which requires heartbeat within 60s) flipped
  to `false` and `LoadAll()` short-circuited before fetching contexts /
  modules / metrics. Standalone has no real heartbeat channel, so the
  provider now bumps `LastHeartbeat` on every `LoadTopologyAsync` call.
  (`redb.Tsak.Web/Services/StandaloneClientProvider.cs`)

### Known issues
- **Worker hot-reload**: when a `.tpkg` is removed from `Libs/`, the
  module is unloaded but the corresponding route context stays in the
  registry with `autoStart=true`. After Worker restart it tries to
  auto-start a context whose module no longer exists. Workaround:
  delete the context manually from the dashboard before removing the
  package.

---

## [3.0.0] — 2026-05-30

Dependency bump release: redb.Tsak now targets **redb.Core 3.0.0** and
**redb.Route 3.0.0**. No breaking changes in Tsak's own public API.

### Changed
- Updated `redb.Core.*` and `redb.Core.Pro.*` dependencies to `3.0.0`.
- Updated all `redb.Route.*` connector dependencies to `3.0.0`.
- Updated `redb.Tsak.Core.Pro` dependency to `3.0.0`.

### What's new in the ecosystem (inherited from redb.Core 3.0.0 / redb.Route 3.0.0)
- **redb.Core 3.0.0** — full `GROUP BY` / window functions over LINQ, native
  `PIVOT` query engine (v2-pvt), soft-delete via `@@__deleted` scheme-swap,
  cluster-safe background deletion, `redb.Tests.Integration` multi-provider
  test matrix.
- **redb.Route 3.0.0** — EIP pattern set expanded (Aggregate, IdempotentConsumer,
  CircuitBreaker, Loop, Filter, WireTap enrich), compiled route core,
  `IRouteScope`, refactored Demo into per-concern route files.

---

## [2.0.3] — 2026-05-18

Minor release that lets modules hosted on top of Tsak (e.g. `redb.Identity`)
expose their own OpenTelemetry `Meter` and `ActivitySource` names without
taking a runtime dependency on `redb.Tsak.Core`.

Fully backward-compatible: no public API surface added, both new keys default
to empty arrays, and the OTel pipeline is still activated only when
`Tsak:Metrics:Prometheus:Enabled=true`. Deployments that do not opt in to
Prometheus see no behaviour change vs `2.0.2`.

### Added
- `Tsak:Metrics:Prometheus:AdditionalMeters` (`string[]`) — extra Meter names
  subscribed into the OTel metrics pipeline alongside `RouteMetrics.MeterName`.
- `Tsak:Metrics:Prometheus:AdditionalSources` (`string[]`) — extra
  `ActivitySource` names subscribed into the OTel tracing pipeline alongside
  `RouteActivitySource.SourceName`.
- Defaults are empty arrays — existing deployments behave identically.
- Empty / whitespace entries in either array are silently skipped, so commented
  template values do not break startup.
- Example: `"AdditionalMeters": ["RedbIdentity"]` surfaces
  `IdentityMetrics` counters (`identity.login.attempts`,
  `identity.mfa.verifications`, …) on the Prometheus exporter without Identity
  taking a runtime dependency on Tsak or OpenTelemetry packages.

### Changed
- `ConfigureMonitoring` (`redb.Tsak.Core.Extensions.ServiceCollectionExtensions`)
  reads the two new config keys and wires the resulting names into
  `AddOpenTelemetry().WithMetrics(…).WithTracing(…)`. Only invoked when
  `Tsak:Metrics:Prometheus:Enabled=true`; otherwise the entire OTel block is
  skipped as before.
- `redb.Tsak.Worker/appsettings.json` ships both keys as empty arrays so the
  shape is discoverable in default deployments.
- `templates/tsak-worker/appsettings.json` carries a `_comment` documenting
  the `RedbIdentity` example for module authors.

### Not changed
- No new public types or method signatures in `redb.Tsak.Core` / `.Contracts`
  / `.Client` / `.CLI` / `.Web`.
- No dependency changes; `redb.Core` / `redb.Route` references remain on
  `2.0.2`.
- No SQL / storage / cluster behaviour changes.

---

## [2.0.2] — 2026-05-16

Patch release aligning with `redb.Core 2.0.2` and `redb.Route 2.0.2`.

### Changed
- Updated `redb.Core`, `redb.Postgres`, `redb.MSSql`, `redb.Core.Pro` dependencies to 2.0.2.
- Updated `redb.Route.*` dependencies to 2.0.2 (includes Http/Ldap 2.0.1 fixes).
- `EavSaveStrategy` renamed to `PropsSaveStrategy` throughout (`redb.Core` 2.0.2 API change).
- Version bumped to `2.0.2`. No source-level API changes vs `2.0.1`.

---

## [2.0.1] — 2026-05-13

Patch release that picks up critical production fixes from `redb.Route 2.0.1`
(`redb.Route.Http` + `redb.Route.Ldap`). All Tsak distribution artifacts
(Worker / Web / Stack Docker images and standalone archives) ship the updated
shared connector layer.

### Fixed
- **Shared connector layer** now includes `redb.Route.Ldap`,
  `redb.Route.Firebase`, and `redb.Route.Validation.Adapters` for all target
  frameworks (`net8.0` / `net9.0` / `net10.0`). Previous releases silently
  dropped these connectors from `Libs/shared/`, so route definitions that
  used `ldap:`, `firebase:` or FluentValidation/DataAnnotations adapters
  failed to load at runtime in Worker.
- Picks up `redb.Route.Http 2.0.1`: HTTP responses no longer echo back
  request headers, invalid header values are filtered, and body-less InOut
  responses (302 redirects, 204 No Content, Set-Cookie-only) propagate
  `Location` / `Set-Cookie` correctly.
- Picks up `redb.Route.Ldap 2.0.1`: service-account authenticated LDAP
  endpoints (with `bindDn` set) no longer reuse pooled connections, fixing
  intermittent *"successful bind must be completed"* errors against Active
  Directory. `PageSize=0` disables the RFC 2696 paged-results control for
  servers that do not support it.

### Changed
- `redb.Tsak/scripts/build-shared.ps1` and
  `redb.Tsak/publish/scripts/build-shared-multitfm.ps1` connector lists
  expanded from 20 to 23 entries to include the missing Ldap / Firebase /
  Validation.Adapters projects.
- Version bumped to `2.0.1`. No source-level API changes vs `2.0.0`.

## [2.0.0] — 2026-05-08

### Changed
- **License changed from MIT to Apache-2.0** for all OSS Tsak packages
  (`redb.Tsak.Core`, `redb.Tsak.Worker`, `redb.Tsak.Contracts`,
  `redb.Tsak.Client`, `redb.Tsak.CLI`, `redb.Tsak.Web`).
  - Apache 2.0 adds an explicit patent grant (§ 3) and termination clause —
    stronger protection for users and contributors.
  - Pro packages (`redb.Tsak.Core.Pro`, `redb.Tsak.Web.Pro`) remain under the
    commercial license in `LICENSE-PRO.txt`.
  - Every nupkg now ships `LICENSE` + `NOTICE` files (Apache 2.0 § 4 attribution).
  - Contributions are now accepted under Apache-2.0; see `CONTRIBUTING.md` and
    the parent `redb/CONTRIBUTING.md`.
- **Strong-Name signing** is active for Pro Tsak assemblies
  (Public Key Token: `8e6fea371ffeb38e`, shared with the main RedBase repo).
- Version bumped to `2.0.0` to align with the RedBase 2.0 release train
  (root packages and `redb.Route` also moved to 2.0.0). No source-level API
  changes vs 1.0.4.

### Why this is a major version bump
- License change is a downstream-compliance breaking change.
- Pro Strong-Name change is a binary-identity breaking change for Pro consumers.

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
