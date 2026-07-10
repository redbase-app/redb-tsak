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

## [3.3.1] — 2026-07-10

> **Why the bump.** Rebuilds the redb.Tsak distribution (Docker images + standalone archives)
> on top of **redb.Route 3.3.1**, which fixes header ↔ property round-tripping across five connectors
> and adds fluent `string` overloads. No `redb.Tsak.Core` code changes — this is a distribution rebuild
> so bundled tsak routes pick up the connector fixes. Binary version moves **3.3.0 → 3.3.1**.

### Changed
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
