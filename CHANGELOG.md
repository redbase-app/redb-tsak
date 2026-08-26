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

## [3.7.1] — 2026-08-26

> **Почему 3.7.1 и что стало с 3.7.0.** 3.7.0 отозван: он собран на .NET 9 и несёт известные
> уязвимости в зависимостях (см. **Security** ниже). Пакеты 3.7.0 на nuget.org разлистены, релизы
> `v3.7.0` на публичных зеркалах удалены. Разлистованная версия остаётся устанавливаемой по
> точному номеру — но ставить её не нужно, 3.7.1 её полностью заменяет.
>
> Патч, а не минор: публичная поверхность API не меняется. Добавление `net10.0` к списку целевых
> платформ ничего не ломает у существующих потребителей, а `net8.0` и `net9.0` сохранены.

### Changed — сборка переехала на .NET 10

Приложения и артефакты собирались на net9, при том что ядро и `redb.Route` давно мультитаргетились
`net8.0;net9.0;net10.0`. Расхождение вылезло на 3.7.0: образы и архивы уехали как net9.

Библиотеки `redb.Tsak.*` и `redb.Identity.*` теперь объявляют `net8.0;net9.0;net10.0` — ровно как
ядро и Route, вся экосистема стала однородной. Хост-приложения и тесты прибиты к одному `net10.0`.
Образы, архивы и теги `-net10`.

`.NET 8` и `.NET 9` уходят из поддержки Microsoft **10 ноября 2026**, обе версии в один день:
срок STS-девятки выровняли с LTS-восьмёркой. `.NET 10` поддерживается до **14 ноября 2028**.
`net8.0` и `net9.0` пока остаются в списке целевых платформ библиотек.

### Security — шесть уязвимостей высокого уровня

Найдены при переводе на .NET 10: смена TFM заставила пересобрать дерево с нуля, и NuGet-аудит
заговорил. При инкрементальной сборке он молчал, поэтому всё это уехало в 3.7.0.
- Уязвимых зависимостей в самих пакетах `redb.Tsak.*` не найдено. Образы и архивы пересобраны на
  .NET 10 и на исправленных пакетах ядра и `redb.Route`.
- Теги образов: `<version>-net10` вместо `<version>-net9`; Web и Stack ships на net10.

## [3.7.0] — 2026-08-25

> **Why a minor.** Secure-by-default changes the running configuration, not just the code: the
> management API now binds `127.0.0.1` instead of `0.0.0.0`, roleless API keys are denied by default,
> and the dashboard requires a real session with a BCrypt password hash. A deployment that relied on
> the old defaults needs its config updated — read **Changed — secure by default** and the two
> dashboard entries before rolling this out. The ecosystem moves on one number; `redb` core,
> `redb.Route` and `redb.Identity` ship 3.7.0 alongside.

### Changed — the packages now ship XML documentation

`GenerateDocumentationFile` was never enabled for redb.Tsak, so all five library packages shipped a bare
`.dll`: a consumer of `redb.Tsak.Client` or `redb.Tsak.Core` got no IntelliSense at all. It is on now, and
`lib/<tfm>/*.xml` travels with every package.

CS1591 (public member without XML doc) is suppressed, unlike `redb.Route` where it is deliberately left on:
turning the doc file on without that floods the build with warnings on members that predate the policy, and
the doc file is what consumers actually need. The doc-syntax warnings stay visible on purpose — they mark
real defects in existing comments, and there are some to fix.

### Fixed — SOAP connector is now part of the shared layer

`redb.Route.Soap` was missing from `scripts/shared-manifest.psd1` (the single source of truth for the
shared connector layer), so `build-shared.ps1` never staged it into `Libs/shared` — a module using a
`soap://` endpoint would find no component in a Tsak worker. Added it to the `Connectors` list (same gap
that had been fixed earlier for `redb.Route.As2`). Re-run `scripts/build-shared.ps1` to repopulate the
shared layer.

### Added — startup log lines listing every configured port

The worker emits one consolidated port summary at startup — `Tsak ports — management API:
http://{host}:{port} (auth=…); echo: …{echoPath} (AutoStart=false); Prometheus: /metrics on
http://{host}:{port}/metrics (scrapes OTel listener http://localhost:{promPort}/metrics)` (or
`Prometheus: disabled`) — so an operator sees the API, echo and both Prometheus ports in a single line
instead of hunting through the API, echo and metrics setup separately. The dashboard logs the URL(s) it
actually bound to once the server is listening and which Tsak management API it proxies to (`Tsak dashboard
listening on {urls}; proxying to Tsak management API at {node}`).

### Fixed — `.tpkg` hot-deploy no longer skips a package after a transient failure or reads it mid-copy (4.11)

The package scanner recorded a `.tpkg`'s last-write time **before** trying to open/verify it, so a package
that failed on that scan (a still-copying, half-written ZIP, or a signature not yet present) was recorded as
"seen" and then **permanently skipped** until its mtime changed again. The tracked time is now written only
**after** a successful open/verify — on both the new-package path and inside `ReloadPackageAsync` — so a
transient failure is retried on the next scan (and, e.g., a package whose detached `.sig` lands a moment
later now loads). Separately, a new **copy-stability debounce** (`HotReloadOptions.AdditionStabilityScans`,
default 2) requires a new/changed `.tpkg` to hold the same size and mtime for N consecutive scans before it
is opened, so an operator dropping a large file in place is never read while still copying (mirrors
`RemovalDebounceScans` for deletions). Note: this adds up to one scan cycle of latency before a newly
added/updated package loads, including atomic API uploads (which are already safe from mid-copy reads via
temp-write + rename) — set `AdditionStabilityScans=1` to disable the wait where only API uploads are used.
Covered by unit tests for the debounce (streak, size/mtime reset, threshold) and a scan test proving a
failed package is not recorded (red-before verified).

### Changed — dashboard API calls now cancel when the component is disposed (4.9, part 2)

The auto-refresh dashboard pages (Dashboard, Endpoints, Logs, NodeDetail, Routes, RouteView, Watchdog)
already held a component-scoped `CancellationTokenSource` for their refresh timer but did not pass it into
their API calls, so an in-flight request kept running after the user navigated away. Those calls now flow
the component token, so they abort on disposal. Action-only pages without a refresh timer (Auth, Audit, Dlq)
are unchanged — adding cancellation infrastructure there is disproportionate to the benefit. The `Ct`
accessor guards the disposed state (reading `CancellationTokenSource.Token` after `Dispose` throws
`ObjectDisposedException`; it now returns `None` instead — surfaced by an adversarial self-review, which
found `NodeDetail`'s poll loop was the one path that would have leaked that as an unobserved task
exception). The log-download endpoint also now returns a clean `504` if the download client's own timeout
elapses (previously an unhandled `500`), while a browser disconnect stays handled by ASP.NET.

### Fixed — large log-file downloads no longer cut by the 5s request timeout (4.9, part 1)

The dashboard's log-file download went through the API client's shared `HttpClient`, whose 5s default
timeout (`HttpClient.Timeout`) is global and cannot be widened per call — so a large log ZIP (the server
buffers the whole file into a ZIP before responding) was aborted mid-flight. `TsakApiClient` now uses a
**dedicated download client** with its own, longer timeout (`DefaultDownloadTimeout`, 5 min; configurable
via the new `downloadTimeout` constructor argument), independent of the short request timeout that normal
calls keep. The dashboard download endpoint now also passes `HttpContext.RequestAborted` into
`DownloadLogFileAsync`, so a browser-cancelled download aborts the server-side transfer instead of running
to completion. (Component-scoped cancellation-token propagation for the dashboard's other API calls is the
remaining part of 4.9.)

### Fixed — `RouteBuilderModule` now actually registers its routes (issue #3)

`RouteBuilderModule.Initialize` called `_builder.Configure(context)`, which only fills the builder's own
definition list. The context compiles **only** builders registered via `RouteContext.AddRoutes(...)`, so the
routes never reached it: the context started with zero endpoints and no error
(`"started successfully: all 0 endpoints operational"`). `Initialize` now registers a `RouteBuilder` with
the context via `AddRoutes` so its routes are compiled at Start; a non-`RouteContext` target throws a clear
`InvalidOperationException` instead of silently dropping the routes. A hand-rolled `IRouteBuilder` that is not
a `RouteBuilder` still has its `Configure` called directly (it is expected to self-register). The previous
mock-based test asserted only that `Configure` was *called* (not its effect), so it was green while the bug
shipped; replaced with a real test that builds a `RouteBuilder`, starts the context, and asserts a route was
compiled (red-before verified). Reported by @MegasomaWT.

### Fixed — API brute-force throttle now guards the real auth surface + is proxy-aware (4.5)

The per-IP auth throttle only gated requests whose path started with `/api/auth/` (`Tsak:Api:AuthThrottle:PathPrefix`),
but the API-key check runs on **every** non-exempt request — so key-guessing simply used any other endpoint
(e.g. `/api/contexts`) and was never throttled. It also counted *all* requests (not failures), which is why
it could not be applied surface-wide without throttling legitimate authenticated traffic. Replaced with a
**failed-attempt lockout** (`FailedAttemptThrottle`, the API-key analogue of the dashboard's `LoginThrottle`)
that keys on the client IP and counts **key-auth failures across the whole surface**: after
`Tsak:Api:AuthThrottle:Limit` failures within `WindowSeconds` the IP is locked out for the new
`LockoutSeconds` and gets `429`; a valid key clears the counter, so real clients are unaffected. Only a
missing/invalid key (401) feeds the throttle — role denials (403) do not. Added proxy-awareness: the client
IP is the transport peer by default, and honours the **right-most** `X-Forwarded-For` hop (the address the
trusted proxy itself appended — the client cannot forge it) only when
`Tsak:Api:AuthThrottle:TrustProxyHeaders=true` (secure default off — XFF is otherwise spoofable). Config
change: `PathPrefix` is no longer used; `LockoutSeconds` (default 120) and `TrustProxyHeaders` (default false)
are new. The throttle map is now bounded: once large, `RecordFailure` evicts expired entries so a flood of
distinct source IPs (e.g. an IPv6 /64) cannot exhaust memory. Covered by unit tests for the throttle
(lockout / window / success-clears / per-IP / concurrency / eviction) and for client-IP extraction (proxy
trusted vs not, right-most hop, fallbacks), red-before verified for the proxy path.

**Deployment note:** behind a reverse proxy you MUST set `TrustProxyHeaders=true`. With it off, the transport
peer is the proxy's IP for every request, so all clients share one throttle bucket and a single attacker
could lock the whole API out — and the right-most-hop parsing assumes exactly one trusted proxy. Also note
(known limitation): because a success clears the key's counter, principals sharing one IP (NAT / a single
proxy IP) can let a legitimate success reset an attacker's failure count — keying on the real per-client IP
(TrustProxyHeaders behind a proxy) avoids this. (Both surfaced by an adversarial self-review; the earlier
revision used the spoofable left-most hop.)

### Fixed — hot-swap/rollback no longer leak a duplicate module assembly into the Default ALC (F-7)

`HotReloadService.HotSwapAsync` (step 8) and `RollbackAsync` published the module's **own entry assembly**
to the shared `LoadedAssemblyTracker` (`Replace`), which `Assembly.Load`s a second copy into the
non-unloadable **Default** ALC. The swapped module already runs from its isolated `newAlc`, so that copy
served no one: it was a guaranteed memory leak (not even counted in `_leakedAlcCount`) and exposed the
entry-assembly name to `Default.Resolving` — a latent type-identity split-brain vector. This mirrored a
bug the 3.7b discovery-path fix had already removed; hot-swap/rollback were simply left inconsistent. Both
`Replace` calls are removed, so a module's entry assembly now stays ALC-private — exactly as the `.tpkg`
path already treats entry points (`ModulePackage` never tracks them). Cross-module shared types continue to
flow through the tracked shared/companion layer, which is unchanged. As a bonus this also removes a
name-collision hazard where a module entry-assembly name matching a real shared assembly would clobber the
shared tracker entry. Covered by two new tests (hot-swap + rollback) that assert the entry assembly is not
published to the tracker, red-before verified.

### Fixed — Failed cluster assignments are retried with backoff (cluster, 4.4)

A `Failed` assignment on a still-alive node used to count as "assigned" and blocked reassignment forever;
the leader now removes the `Failed` assignment and reassigns the module, gated by a per-module backoff
(`ClusterOptions.FailedRetryBackoffSeconds`, default 60s) so a persistently-failing module is not re-tried
on every rebalance. Backoff state is in-memory on the leader (a new leader retries pending `Failed`
immediately, which is acceptable) and is now pruned each rebalance for modules that are no longer Failed,
so it cannot grow unbounded.

**Note:** an earlier revision of this item also added a read-time *epoch fence* in `ApplyLocalAssignmentsAsync`
(skip any assignment whose `Epoch` is below the current leader epoch). An adversarial self-review found this
fence is harmful and it has been **removed**: assignments are stamped with the epoch of the leader that
created them and are never re-stamped while a module stays put, while a follower's snapshot epoch tracks the
*live* leader — so after any leader failover (E→E+1) every surviving assignment reads `E < E+1`, and the
fence stopped all still-running modules on every failover and never restarted them. Split-brain writes from a
superseded leader are already prevented at the lock layer (epoch-fenced takeover/renew in
`RedbDistributedLock`), which is the correct place to fence. A regression test now asserts a lower-epoch
survivor assignment is still applied.

### Fixed — internal exception detail no longer leaks to API clients / the dashboard UI (4.7, Tsak side)

Exception messages (which can carry internal paths / config) were surfaced verbatim: `ModuleUploadService`
returned `Install failed: {ex.Message}` / `Rollback failed: {ex.Message}` to the API, and the dashboard
`TsakErrorBoundary` rendered `@CurrentException.Message`. These now show a **generic** message and log
the detail server-side (the error boundary logs via `ILogger`; upload logs and returns "see server logs").
The root leak in redb.Route's `ControllerDispatcherProcessor` (returns `ex.Message`) is outside redb.Tsak
and is filed as a bug-report (BOUNDARIES BR-4).

### Fixed — leader state is published as one atomic snapshot (4.3)

`RedbLeaderElection` published `(epoch, leaderId, isLeader)` as three separate field writes, and
`RenewAsync`/`StepDown` cleared `isLeader` without touching the epoch — so a reader calling the separate
getters could observe a torn tuple (e.g. `IsLeader=true` with an epoch from a different election), which
would undermine epoch fencing. The three values are now one immutable `LeaderSnapshot` swapped atomically;
a new `ILeaderElection.GetLeaderSnapshot()` returns the consistent triple in a single read (used by epoch
fencing so `IsLeader` and `Epoch` always belong to the same election).

Tests: `RedbLeaderElectionTests` (consistent snapshot on acquire; renew-failure/step-down drop leadership
but keep the epoch consistent; a concurrent-read stress never sees a torn tuple).

### Fixed — defects found by an adversarial self-review of this release

Independent adversarial review of the session's changes surfaced five real bugs, now fixed (one more,
a hot-swap Default-ALC republish, is documented as follow-up F-7):

- **Coordinator `WaitForIdleAsync` lost-wakeup hang.** `_pending` and `_idleSignal` were mutated with
  `Interlocked` but the counter-transition and the signal reset/complete were not atomic together — the
  consumer could complete a signal a concurrent `Enqueue` had just swapped out, stranding a waiter
  forever. Both are now guarded by one lock so the transition and the signal op are atomic.
- **Cluster rebalance-on-acquire could be skipped on a leadership flap.** With the split loops, the work
  loop detected "became leader" only via a `wasLeader` edge, which a lose→re-acquire happening entirely
  inside the heartbeat loop can hide. The heartbeat loop now sets a `_rebalanceRequested` flag on every
  acquire, which the work loop honours in addition to the edge.
- **`LoadedAssemblyTracker.LoadOrReuse` could still return a duplicate.** When the caller key (file
  basename) differs from the real assembly name AND that real name is already tracked, it returned the
  freshly byte-loaded duplicate instead of the canonical instance. It now resolves the canonical via the
  real name (`GetOrAdd`) and aliases the caller key to it.
- **`LoginThrottle` lost-update race.** The lockout counter was incremented in place inside
  `ConcurrentDictionary.AddOrUpdate` returning the same reference — no serialization, so concurrent
  failures lost increments and slipped under the threshold. The read-modify-write is now under a
  per-entry lock (defeats concurrent brute-force).
- **`DashboardAuth.IsLocalUrl` open-redirect via control chars.** `"/\t/evil.com"` passed the local-URL
  check; browsers strip the tab, collapsing it to a protocol-relative `//evil.com`. Control characters
  are now rejected.

Tests: red-before/green-after verified for the tracker fix; new control-char cases (`DashboardAuthTests`),
concurrency stress for the throttle (`LoginThrottleTests`) and the coordinator drain
(`TsakCoordinatorTests.WaitForIdle_UnderConcurrentEnqueue_CompletesAndDrains`).

### Added — optional authentication for the Prometheus `/metrics` route (4.6)

`/metrics` was always unauthenticated. Its exposure is already contained by default (it rides the Api
port, which now binds loopback by default — review item 2.1). For operators who deliberately expose the
Api port, `Tsak:Metrics:Prometheus:RequireAuth=true` now gates the scrape route through the existing
`AuthorizeProcessor` (a valid API key required; a `401` otherwise). Off by default so Prometheus keeps
scraping out of the box.

### Fixed — hot-reload Dispose is idempotent and coordinated with in-progress scans (4.12)

`HotReloadService.Dispose` unloaded module ALCs but had no guard: a second call re-iterated and
double-unloaded, and a `ScanAndReloadAsync` running concurrently could touch an ALC while Dispose was
unloading it (review item 4.12). Dispose is now idempotent (`_disposed` guard) and takes a scan gate
(`SemaphoreSlim`) that scans also hold for their whole run — so Dispose waits for an in-progress scan
to finish, and a scan starting after disposal bails with `0`.

Tests: `HotReloadServiceTests.Dispose_is_idempotent`, `ScanAndReload_after_dispose_returns_zero`.

### Fixed — control-plane failure is reflected in readiness; cluster timing-margin warning (4.1, 4.2)

- **4.1** When the `_system` control-plane context (REST/management API) failed to start, the host logged
  an error and kept running the business contexts — a node with no management plane could still report
  healthy. The failure is now logged at **Critical** and flips a `control-plane` health check to
  **Unhealthy** (`ControlPlaneHealth` + an `IHealthContributor`), so the readiness probe stops reporting
  a control-plane-less node as ready.
- **4.2** The cluster coordinator now logs a startup **warning** when the timing margins are tight
  (`DeadNodeTimeoutSeconds` < 3× heartbeat, or `LeaderLockTtlSeconds` < 2× heartbeat) — a single slow
  heartbeat then looks like a dead node / expired lease. The defaults (HB 15s, dead-node 60s, TTL 30s)
  are safe; this only fires when an operator tightened them.

Test: `ControlPlaneHealthTests` (healthy by default; unhealthy after the control-plane is marked failed).

### Fixed — dashboard hygiene: DLQ node null-guard, self-hosted chart.js (4.8, 4.10)

- **4.8** The DLQ page called `NodeProvider.GetClient(NodeId)` and used the result without a null check —
  a node missing from the current topology (removed, or not yet discovered) threw a
  `NullReferenceException`. Load now shows a clean "node not available" state and Replay/Discard toast
  the same instead of throwing (matching the Logs page).
- **4.10** `chart.js` was loaded from a public CDN with no integrity checking (and no fallback for
  air-gapped ops). It is now **self-hosted** (`wwwroot/js/chart.umd.min.js`, pinned to Chart.js 4.4.7),
  removing the runtime third-party dependency.

### Changed — module lifecycle is async end-to-end: ordered event queue, no sync-over-async (1.7, 3.8)

The module-lifecycle pipeline was a mix of fire-and-forget `async void` and sync-over-async blocking.
It is now a coherent async chain (review items 1.7 + 3.8):

- **Coordinator event queue (1.7).** `TsakCoordinator` subscribed to the registry's synchronous
  `EventHandler<T>` events with `async void` handlers — unordered, unobservable, and impossible to wait
  on (the reported context count was racy). Events are now **enqueued** and a **single background
  consumer awaits each handler in FIFO order**, with a per-item catch (a bad module still can't crash
  the node). A new `WaitForIdleAsync()` lets startup/tests wait for topology changes to settle instead
  of racing them. `Initialize` is now idempotent (no double-subscription).
- **Registry persistence is async (3.8).** `RegisterModule`/`UnregisterModule`/`ReplaceModuleSilent`
  blocked on the store via `.GetAwaiter().GetResult()`. They are now `RegisterModuleAsync` /
  `UnregisterModuleAsync` / `ReplaceModuleSilentAsync`, awaited by their (already-async) callers
  (hot-reload, the modules controller). `UnregisterModuleSilent` touches no store and stays sync.
- **Heartbeat snapshot is async (3.8).** `ContextInfoCollector.CollectSnapshots` (called every
  heartbeat) blocked on the store-backed auto-start read; it is now `CollectSnapshotsAsync`, awaited by
  `RedbNodeRegistry.HeartbeatAsync`.

Tests: `TsakCoordinatorTests.Integration_static_providers_trigger_coordinator` now drains via
`WaitForIdleAsync` (the old pass depended on fragile sync-until-first-await timing — exactly the race
this removes); the collision test still proves the queue survives a bad module. Registry/controller
tests updated to the async methods. Unit suite 614/614; cluster integration 33/34 (1 pre-existing
stress-test flake, passes in isolation — untouched lock code).

### Fixed — module private deps and own assembly no longer leak into / duplicate in the Default ALC

Two module-isolation defects (review item 3.7):
- **(a)** `ModuleAssemblyLoadContext.Load` byte-loaded a module's PRIVATE probe-path dependency into the
  **Default** ALC (via `LoadedAssemblyTracker.LoadOrReuse`). That pinned it in memory (can't unload with
  a collectible module) and made two modules with different versions of the same private dependency
  silently share whichever loaded first. Private probe deps now load into the **module's own ALC**
  (`LoadFromStream`); only genuine host/shared-contract assemblies (resolved earlier via Default/the
  tracker) stay shared.
- **(b)** Hot-reload's bare-DLL discovery byte-loaded the module's OWN assembly into the Default ALC
  **and** into its isolated ALC — two distinct copies of the module's types while the module actually
  ran as the ALC copy (type split-brain + leak). The redundant Default load is removed; a module's own
  assembly is not a shared dependency.

Tests: `ModuleAssemblyLoadContextTests.PrivateProbeDependency_LoadsIntoModuleAlc_NotDefault` (emits a
real private-dep assembly on disk via `PersistedAssemblyBuilder`; verified to FAIL on the pre-fix code —
the dep landed in Default — and pass after) and `SharedContractAssembly_ResolvesFromDefault_NotModuleAlc`
(shared contracts keep one identity in Default). The startup bare-DLL path (`TsakModuleRegistry`) already
loads once into its target (Default) ALC and discovers from that same copy — left unchanged.

### Fixed — shared-path resolver never throws out of the resolution event

The shared-path `Default.Resolving` handler byte-loads a transitive dependency from `Libs/shared/`, but
caught only `FileLoadException` (review item 3.6). A transient `IOException` (the DLL locked mid-copy by
`build-shared`), `UnauthorizedAccessException`, or a partial/corrupt image (`BadImageFormatException`)
propagated out of the `Resolving` handler — which aborts the runtime's entire resolution instead of
letting it fall through to version-tolerant forwarding or another handler. The handler now soft-skips
any such failure (logs it) and falls through, so a bad file in the shared dir can never abort a JIT
resolution.

Test: `SharedRuntimeResolverTests.Resolver_CorruptDllInSharedDir_DoesNotThrowBadImageOutOfResolution`
(a corrupt DLL in the shared dir yields an ordinary not-found, not a `BadImageFormatException` escaping
the handler).

### Fixed — assembly dedup keys on both the file name and the real assembly name (no duplicate load)

`LoadedAssemblyTracker.LoadOrReuse(assemblyName, bytes)` tracked the loaded assembly only under its
**real** simple-name, but its fast-path and return lookup used the **caller's** key (typically the file
basename). When the two differed, the next `LoadOrReuse` with the same file name missed the cache and
called `Assembly.Load(bytes)` again — two instances of one identity in the Default ALC, i.e. type
split-brain (review item 3.5). `LoadOrReuse` (and `Replace`) now alias the loaded assembly under **both**
the real simple-name (what the runtime's `Default.Resolving` asks for) and the caller's key, so a repeat
call reuses the single instance.

Tests: `LoadedAssemblyTrackerTests` — a file-name-≠-assembly-name reuse returns the same instance (and
resolves by the real name too), plus an idempotency check.

### Fixed — shared-assembly list is now a thread-safe immutable snapshot (no torn read during hot-reload)

`SharedAssemblyLoader` held its loaded assemblies in a plain `List<Assembly>` that
`ReloadSharedAssemblies` cleared and repopulated on the hot-reload thread, while
`TsakContextManager.CreateContext` enumerated `LoadedAssemblies` (`.ToArray()`) concurrently on an API
thread (review item 3.4) — a classic `List` enumerate-vs-mutate race that throws
`InvalidOperationException` or hands a context a torn component set. The list is now an
`ImmutableArray<Assembly>` published under a lock: reads are lock-free snapshots, and reload builds the
fresh set and **swaps it atomically** (no empty/torn window mid-reload). The scan/load logic moved into
a `ScanAndLoad` helper that never touches the published field.

Test: `SharedAssemblyLoaderTests.Concurrent_reload_and_enumeration_never_throws` (500 reloads hammered
against continuous enumeration — no exception).

### Fixed — hot-swap rolls back on ANY start failure, not only timeout (no registry left on a broken module)

`HotSwapAsync` unregistered the old module, replaced it with the new one, then started the new
version — but rolled back only on `OperationCanceledException`/`TimeoutException` (review item 3.2).
Any other failure to start the new version (a `CreateContext` throw, a DI fault) fell through to the
outer catch, which did NOT restore the registry, did NOT unload the new ALC, and logged the misleading
"staying on current version" — leaving the old module unregistered and the registry pointing at a
broken new one. Now:

- **Rollback on ANY exception** while starting the new version (routed through the existing
  `RollbackAsync`, which stops the new module, re-registers and restarts the old one, and reverts the
  shared assembly tracker).
- **The shared `LoadedAssemblyTracker` is switched to the new assembly only AFTER the new version has
  started successfully** (was: before validation), so other modules never resolve an unvalidated,
  unstarted assembly, and a failed swap needs no tracker revert.
- The outer catch now rolls back too whenever the registry was already mutated, and **logs the actual
  outcome** (rolled back vs. staying on current) instead of a hardcoded message.
- Removed the dead `startCts` timeout wiring — `ProcessModuleAddedAsync` takes no `CancellationToken`,
  so the start timeout was never actually enforced (tracked as a follow-up).

Test: `HotReloadServiceTests.HotSwap_LoadFailureBeforeSwitch_LeavesOldModuleUntouched` (a failure
before the switch does not unregister the old module or attempt a rollback). The
rollback-on-start-failure path shares the already-exercised `RollbackAsync` and is verified by review
(a dedicated test needs an on-disk two-version module fixture — see BOUNDARIES_AND_FOLLOWUPS).

### Fixed — CreateContext is serialized with remove, so a context can't be orphaned by a concurrent remove

`CreateContext` mutated both `_contexts` and `_namedRedbContainers` but, unlike start/stop/restart/
remove, ran *outside* `_lifecycleLock` (review item 3.1). A `RemoveContextAsync` racing a coordinator
`CreateContext` on the same name could `TryRemove` before the `TryAdd`, leaving an orphaned context
(and a leaked named-redb container) the remover believed was gone. `CreateContext` now takes
`_lifecycleLock` — with a fail-fast existence check under the lock — so create/remove are atomic with
respect to each other, and concurrent creates of one name no longer build or register anything for the
loser. The lock order is always coordinator-per-context → `_lifecycleLock` (no lifecycle path re-enters
the coordinator, none calls CreateContext), so it cannot invert or re-enter.

Tests: `TsakContextManagerTests.Concurrent_create_same_name_yields_exactly_one_context` and
`Concurrent_create_and_remove_distinct_contexts_leaves_clean_state`.

### Fixed — DLQ replay is atomically claimed, so a failed exchange is never replayed twice

`DlqService.ReplayAsync` read the entry, replayed it, then unconditionally marked it `replayed`
(review item 3.3). Two operators — or a double-click — both read the same `pending` entry and both
replayed it, running the business exchange **twice**. Replay now takes an **atomic claim first**:
`UPDATE tsak_dlq SET status='replaying' … WHERE entry_id=@id AND status='pending'`. The DB serializes
the conditional update, so exactly one caller sees `affected == 1` and proceeds; the rest are told the
entry is already being (or has been) replayed. On success the entry becomes `replayed`; on replay
failure the claim is **released back to `pending`** for retry; a crash between claim and replay leaves
it visibly in `replaying` rather than silently lost.

Tests: `DlqTests.Replay_SecondAttempt_IsRefused_TailRunsOnce` (deterministic double-replay → tail runs
once) and `Replay_Concurrent_NeverProcessesTwice` (two concurrent replays → the tail never runs twice),
both on a real SQLite-backed DLQ with a live replayable route.

### Changed — built-in retention sweeps are now cluster-singletons (`.Cluster(true)`)

The daily audit and DLQ retention cron routes ran in the per-node `_system` context with no cluster
guard, so in a multi-node cluster each node ran the sweep. It was harmless (the prune is an idempotent
`DELETE ... WHERE created < cutoff` — the first node deletes, the rest affect zero rows), but wasteful.
Both routes are now marked `.Cluster(true)`, so they run on the leader only in a cluster and still run
via the AlwaysLeader fallback in standalone. A sweep missed during a leader failover is covered by the
next day's run. These are the first built-in Tsak routes to dog-food the `.Cluster(true)` policy.

Test: `RetentionRouteClusterTests` (both route definitions report `GetCluster() == true`).

### Fixed — cluster coordinator: leader renewal no longer starved by module starts (two-leader window)

The coordinator ran heartbeat, leader-lock renewal, dead-node detection, rebalance AND module
start/stop in one sequential loop with a single delay (review items 1.3, 1.5). A module that took
longer than the lease TTL to start pushed the next renewal past the TTL — the lock expired, a peer
was elected, and **two nodes rebalanced at once**; the same stall also delayed heartbeat, so a live
node could be marked dead. Following the industry canon for lease-based coordination
(Kubernetes leader-election, etcd/Consul lease keepalive, ZooKeeper session ping — a dedicated
renewal timer isolated from work), the loop is now split:

- **A fast, minimal heartbeat/renew loop** on a `PeriodicTimer` whose cadence is `min(interval, TTL/3)`
  — it does only the heartbeat upsert and the leader-lock renewal, so a slow module start can never
  delay it. Renewal is proactive at `TTL/3`.
- **A separate work loop** for the heavy duties (license backstop, dead-node detection, rebalance,
  applying local assignments = module start/stop). Its latency no longer costs the node its leadership.
- **Voluntary step-down on the renew-deadline** (canon: K8s `RenewDeadline`): if renewal keeps
  *throwing* (e.g. the store is unreachable) so leadership can't be confirmed, the node steps down
  locally (`ILeaderElection.StepDown()`) instead of holding stale leadership until the TTL lapses.
  Epoch fencing (from the earlier lock fix) makes any late write from a stale leader a no-op, so this
  shrinks the split-brain window to near zero.
- **Heartbeat is now a light upsert** (1.5): `RedbNodeRegistry.HeartbeatAsync` no longer calls the
  heavy `RegisterAsync` (registration lock + license check) from *inside* the deadlock-retry lambda —
  a nested lock on the hot path. When the node record is missing, re-registration runs *after* the
  retry scope closes.
- The consumer-side epoch fencing that stops a revived node's routes when its epoch is stale
  (`ClusteredRoutePolicy` watch loop: renew → lost → `StopRoute`) was already in place from the lock
  fix; no change needed. `DeadNodeTimeoutSeconds` default (60s) is already ≥ 4× the heartbeat
  interval (15s), per the false-dead guidance.

Tests: `ClusterCoordinatorTests.Heartbeat_and_renew_are_not_starved_by_a_slow_module_start` (heartbeat
and renew keep ticking while a module start blocks past the TTL) and
`Leader_steps_down_when_renew_keeps_throwing_past_the_deadline`. Unit suite 601/601; cluster
integration 34/34 against real PostgreSQL.

### Fixed — revoked API keys are rejected within seconds, not up to 5 minutes (cluster)

`ApiKeyService` cached a validated key for the full `CacheTtl` (5 min) and only its `RevokeKeyAsync`
evicted the local cache. So a key revoked on node A — or straight in the database — stayed accepted
on node B for up to 5 minutes (review item 2.7). The cache now re-confirms a still-cached key against
the store once per `RevocationCheckInterval` (default 30s, `Tsak:Auth:RevocationCheckSeconds`;
`CacheTtl` is `Tsak:Auth:CacheTtlSeconds`). This bounds the cluster-wide revocation window to the
interval — independent of the full TTL and without a store read on every request — and picks up
revocation and expiry that happened on any node or directly in the DB. Set the interval to 0 to
re-check on every request (no window, at the cost of a store read per hit).

Test: `ApiKeyRevocationTests` — revoked-elsewhere rejected within the interval (not the TTL),
zero-interval re-checks every request, still-valid keys refresh once per interval (clock injected).

### Changed — whole-log-file download raised to Admin (bulk-exfil risk)

A full log file can carry raw endpoint URIs with passwords and payload fragments, yet downloading one
was gated only at Operator (review item 2.8). The download action `GET /api/logs/files/{filename}` is
now `[RequiresRole(Admin)]` (the live tail `GET /api/logs` stays Operator — same data, but the
interactive path operators need). The dashboard's log-download proxy is likewise raised to
`RequireAuthorization("Admin")`, and the ZIP link is shown only under `AuthorizedView AdminOnly`.

Test: `RoleAuthorizationTests.DownloadingWholeLogFile_RequiresAdmin` (operator → 403, admin → allow).

> **Bug report (root cause is outside redb.Tsak, not fixed here):** log bodies contain raw secrets
> because the core logging layer writes endpoint URIs and payloads unredacted. The real fix is
> redaction at write time (`docs/SECURITY_URI_REDACTION_PLAN.md`). Raising the gate to Admin is the
> in-Tsak mitigation until that lands.

### Changed — dashboard login hardened: BCrypt hash, constant-time compare, lockout

The standalone dashboard login compared the password against a plaintext config value with `!=` (a
timing oracle) and had no rate limiting, so an exposed dashboard was open to online guessing
(review item 2.6). Now:

- **`Tsak:Web:AdminPasswordHash`** (a BCrypt hash, verified via the core `BcryptPasswordHasher`,
  which also accepts legacy SHA256 hashes) is preferred over the plaintext `Tsak:Web:AdminPassword`.
  Plaintext still works for back-compat but logs a one-time warning to switch to the hash.
- Plaintext comparison is **constant-time** (`CryptographicOperations.FixedTimeEquals` over SHA-256
  of each side), so a wrong password cannot be recovered from response timing.
- A shared **`LoginThrottle`** locks a login out after N failed attempts within a window
  (`Tsak:Web:Lockout:MaxAttempts` / `:WindowSeconds` / `:DurationSeconds`, default 5 / 300s / 60s).
  It applies to both standalone and cluster logins; a success clears the counter. The login page
  shows a distinct "too many attempts" message.

Tests: `LoginThrottleTests` (lockout, expiry, per-key scoping, window roll-off on a controllable
clock) and `ConfigAuthServiceTests` (hash accept/reject, hash-over-plaintext precedence, plaintext
back-compat, case sensitivity). Web suite 48/48.

### Changed — dashboard: real server-side session (cookie auth), no more unauthenticated log download

The dashboard authenticated only inside the Blazor circuit — a per-circuit in-memory `IAuthService`
flag. Anything reached *outside* a circuit was ungated, most importantly the BFF log-download proxy
`GET /api/proxy/{node}/logs/download/{file}`, a plain HTTP endpoint that streamed worker log files
(connection strings, tokens, PII) using the server's admin key to **anyone who could reach the port**
(review item 2.4). And because the circuit flag was not a real security principal, dashboard
authorization was cosmetic — `<AuthorizedView>` only hid markup (2.5).

The dashboard is now a proper BFF with a single, server-verifiable session:

- **ASP.NET Core cookie authentication** (`tsak.auth`, HttpOnly, SameSite=Strict). The browser holds
  only the cookie; the server holds the Tsak keys and uses them only for an authenticated principal.
  The same cookie gates the Blazor circuit **and** plain HTTP endpoints uniformly.
- **Login/logout are real endpoints** (`/auth/login`, `/auth/logout`) that `SignInAsync`/`SignOutAsync`
  — an interactive circuit cannot set a cookie after the response has started. The login page is a
  native form POST (anti-forgery validated; `returnUrl` restricted to local paths — no open redirect).
  Credentials are still validated by `IAuthService` (standalone: config; cluster: redb `_users` via
  `IUserProvider`/BCrypt), now stateless.
- **Routed pages require authentication** via `AuthorizeRouteView` (anonymous → redirect to `/login`);
  `<AuthorizedView>` and the top bar read the role from the **signed principal**, which cannot be
  forged client-side.
- **The log-download proxy is behind `RequireAuthorization("Operator")`** plus a path-traversal guard
  on the filename and node validation via the client provider. An anonymous or viewer caller is
  refused before the admin key is ever used. The download `<a href>` inherits the auth cookie
  automatically, so the UX is unchanged for authorized users.
- Roles are expanded into a ladder at sign-in (`admin` ⊇ `operator` ⊇ `viewer`), so `RequireRole`
  is a simple membership test.

Tests: new `redb.Tsak.Web.Tests` project — `DashboardAuthTests` (traversal guard, open-redirect
guard, role ladder) and `LogDownloadAuthTests` (WebApplicationFactory: anonymous download → redirect
to login, not the file; login page reachable anonymously). 35/35.

Follow-up (documented, not in this change): the BFF still calls the worker with a single admin key,
so the worker's own RBAC is not yet per-user (review item 2.5.3); and the download gate will be
raised from Operator to Admin under review item 2.8.

### Fixed — effective-config endpoint no longer leaks webhook URLs, URI userinfo, embedded passwords

`GET /api/system/config` (admin-only) dumps the whole `Tsak:*` section, but `ConfigRedactor` masked
only `Password=`/`Pwd=` inside values under the `ConnectionStrings:` prefix, plus values whose *leaf*
key name looked sensitive. Several secret-shaped values slipped through: an alert `…Webhook:Url` (the
URL is itself a bearer credential), a `…Webhook:Headers:Authorization` token, a broker/endpoint
`…Endpoint:Uri` of the form `amqp://user:pass@host` (userinfo), and any connection string stored
*outside* the `ConnectionStrings:` prefix. `ConfigRedactor` now:

- matches sensitive markers against the **whole key path**, not just the leaf (so `…:Webhook:Url`
  masks on `webhook`, `…:Headers:Authorization` on `authorization`), and adds `webhook`,
  `authorization`, `accountkey`, `sastoken`, `passwd`, `privatekey` markers;
- scrubs `Password=`/`Pwd=` inside **any** value, not only under `ConnectionStrings:` (host/database
  stay visible for diagnostics);
- masks **URI userinfo** (`scheme://user:pass@host` → `scheme://***@host`) in any value, so broker
  URIs stop leaking credentials while host:port stays visible.

Test: `ConfigRedactorTests` — a `KnownLeaks` regression table plus connection-string / URI-userinfo /
false-positive cases (an `@` in a path/query is not treated as userinfo). Suite 15/15.

### Changed — secure by default: loopback bind + fail-closed roles

Two default flips from the security review so a fresh Tsak is not wide open the moment its port is
reachable. Both are overridable in config; only deployments that *relied on* the old permissive
defaults are affected.

- **Management API binds `127.0.0.1` by default** (was `0.0.0.0`). `Tsak:Api:Host` now defaults to
  loopback, so local runs work out of the box while exposing the management plane on an external
  address is an explicit choice. When the host *is* bound off-loopback with `Tsak:Auth:Enabled=false`,
  `SystemContextBuilder` now logs a loud `SECURITY:` warning that the port grants full unauthenticated
  admin (stop/remove contexts, upload modules, issue keys, dump config).
- **Roleless API keys are denied by default** (`Tsak:Auth:RolelessKeysAreAdmin` now defaults to
  `false`; `RoleAuthorizationProcessor`'s ctor default likewise). A key carrying no roles is refused
  role-gated endpoints (fail-closed) instead of being silently treated as `admin`. Set the switch to
  `true` only as a migration bridge to keep pre-RBAC keys working until they are re-issued with
  explicit roles.

Tests: `RoleAuthorizationTests` updated — `RolelessKey_IsDenied_ByDefault`,
`RolelessKey_IsDeniedMutation_ByDefault` (fail-closed), and
`RolelessKey_IsTreatedAsAdmin_WhenCompatibilitySwitchIsOn` (opt-in bridge). Suite 33/33.

### Fixed — a bad module can no longer crash the node (coordinator event handlers)

`TsakCoordinator` subscribed to the registry's `EventHandler<T>` events with `async` lambdas, i.e.
`async void`: any exception after the first `await` — most easily a **context-name collision**, where
`ValidateContextNameClaim` threw `InvalidOperationException` — surfaced as an *unobserved* exception on
a thread-pool thread and **terminated the whole process**. Two hot-added modules claiming the same
context name would take the node down instead of logging an error. Now:

- Every registry-event handler runs through a `SafeHandle` wrapper that catches and logs, so an
  unobserved fault can never crash the node (same fire-and-forget timing as before).
- Context-name collision is a **logged skip** (`TryClaimContextName` returns `false` instead of
  throwing): the offending module is skipped, the rest of the batch keeps loading, the node stays up.

Test: `TsakCoordinatorTests.Context_name_collision_is_skipped_not_thrown` (collision → no throw, exactly
one context). Full unit suite 579/579. (A Channel-fed, awaited handler — so startup can wait on
completion and the reported context count stops being racy — is tracked as a review follow-up.)

### Fixed — cluster coordinator: lock release and self-renew fencing (Pro, multi-node)

Two deterministic defects from the cluster review that made the coordinator lose routes on planned
drains and double-run them on failover. Fixed and verified against a real PostgreSQL two-node setup.

- **`ReleaseLockAsync` was a guaranteed no-op on cordon-drain and on watch-loop errors.**
  `ClusteredRoutePolicy` cleared `_isLeader` *before* calling `ReleaseLockAsync`, whose
  `if (!_isLeader) return;` then skipped the actual `_lock.ReleaseAsync`. So a cordoned node stopped
  its consumer but **held the lock until TTL** → the route ran on **zero** nodes for up to the TTL on
  every drain; a watch-loop exception left the node consuming without renewing → **two-node run** after
  TTL. (Same no-op also leaked the lock on shutdown-during-acquire.) Release is now unconditional
  (`IDistributedLock.ReleaseAsync` is owner-scoped, so a non-holder is a safe no-op at the store), and
  cordon/error paths **stop the consumer first, then release** — closing the reorder window.
- **Self-renew bypassed epoch fencing → two lock holders.** `RedbDistributedLock.TryAcquireAsync`'s
  "owned by us → renew" branch renewed unconditionally on the initial (out-of-transaction, possibly
  stale) read with a last-writer-wins `SaveAsync`, so a node whose expired lock had just been taken
  over (higher epoch) could clobber that takeover with its old epoch and report `Acquired=true`. Self-
  renew now runs through an atomic `AtomicSelfRenewAsync` (row lock + re-read): it renews only if the
  record is still owned by us and preserves the fresh epoch, otherwise it reports the lock lost.
- **A rapid suspend→resume could leave two watch loops running.** `ClusteredRoutePolicy.StartWatchLoop`
  overwrote `_loopCts`/`_loopTask` without cancelling the previous loop; since `OnResume` also resets
  `_stopping=false`, an old loop still parked in its heartbeat `Task.Delay` would wake and keep running
  alongside the new one (duplicate acquire/start + leaked CTS). Start/stop is now serialized through a
  gate that cancels, awaits and disposes the previous loop first (`StartWatchLoopAsync`/`StopWatchLoopAsync`).
- **A concurrent first-create race could leave two coexisting lock records (both "leader").** With no
  unique constraint on the lock key, two nodes creating the lock at once can both `INSERT` and — if
  neither sees the other yet — both return `Acquired=true`; each then renews its own record forever.
  `RenewAsync` is now dedup-aware: it queries ALL records for the key inside a transaction, keeps the
  deterministic winner (lowest id) under a row lock, deletes the rest, and only the surviving record's
  owner renews. The loser's next renew finds its record gone and stops — the split-brain collapses
  within one heartbeat. (Fully preventing the double-insert would need a redb uniqueness/serializable
  primitive that isn't exposed today; the dedup makes it self-healing instead.)

Tests: `ClusteredRoutePolicyFailoverTests.Cordoned_leader_drains_route_to_follower_within_a_heartbeat`
(follower picks up well under the TTL — fails on the old no-op release) and
`DistributedLockTests.Self_renew_never_rewinds_epoch_under_concurrent_takeover` (epochs never rewind
under concurrent acquire/renew). Existing failover tests still green; full unit suite unchanged.
Note: clustering targets a shared DB (Postgres/SQL Server); the default single-node SQLite path is
unaffected.

---

## [3.6.0] — 2026-08-13

> Ships with the ecosystem. The minor comes from `redb.Route` (new `.PropagateToolHeaders(...)` API);
> redb.Tsak's own changes here are fixes, and the rebuild also carries the redb.Core tree-scope fix
> and the rebuilt SQLite native into every worker.

### Fixed — shared-runtime layer now fail-fasts / compat-gates redb.Route.Http.Hosting

`redb.Route.Http.Hosting` (extracted from `redb.Route.Http` in 3.5.1) was listed in the build manifest
(`scripts/shared-manifest.psd1` Framework) — so `build-shared` puts it in `Libs/shared` — but was
**missing from `SharedRuntimeBootstrap.FrameworkAssemblies`**, the list that drives the early byte-preload
fail-fast and the minor compat-gate. So it was served from the shared layer with **neither guard**: a
version-mismatched copy would be swallowed silently instead of aborting startup — exactly what the
manifest's own note warns against. Declared it.

A new test (`SharedRuntimeManifestConsistencyTests`) guards this both ways so the drift can't return:
every manifest **Framework** assembly must be declared for fail-fast/gate, and every declared assembly
must actually be built into `Libs/shared` (manifest Framework or Connectors) or the host fail-fasts on
every start. Full suite 578/578.

### Fixed — dead-letter store on PostgreSQL (provider-specific binding bugs)

The DLQ was integration-tested only on SQLite, which hid two PostgreSQL binding bugs that made the
dead-letter store **silently non-functional on Postgres**. Both are fixed and verified against a real
Postgres; SQLite and SQL Server behavior is unchanged.

- **Capture never persisted on Postgres.** `DlqService.CaptureAsync` bound `replayable` as an `int`
  `1/0` into a `BOOLEAN` column. Postgres has no implicit `integer → boolean` cast, so the whole INSERT
  threw (`42804: column "replayable" is of type boolean but expression is of type integer`) — and
  capture's own catch swallowed it, so dead-letters were dropped on the floor. Now bound as a real
  `bool` (→ PG `boolean` / SQL Server `bit` / SQLite `0/1`).
- **Retention sweep and date-filtered queries threw on Postgres.** Timestamps (`occurred_at`, `since`,
  `until`, `cutoff`, `replayed_at`) were bound as ISO-8601 **strings** compared against native
  `timestamptz` columns. Postgres has no implicit `text → timestamptz` cast in a comparison operator,
  so `DELETE ... WHERE occurred_at < @cutoff` and `WHERE occurred_at >= @since` threw
  (`42883: operator does not exist: timestamp with time zone < text`) — the daily retention job and any
  date-filtered dashboard query. Timestamps are now bound as native `DateTimeOffset` for Postgres/SQL
  Server (their columns are `timestamptz`/`datetimeoffset`), and kept as ISO-8601 `"o"` strings for
  SQLite (whose column is `TEXT`, relying on lexicographic == chronological order — unchanged on-disk
  format).

Verified: both failures reproduced against the real Postgres with the old bindings and pass with the
new ones; full Tsak suite 576/576 (incl. the SQLite DLQ integration tests).

**Known follow-ups (not in this fix):** replay has no atomic `pending` claim, so a concurrent/repeated
replay can process the same exchange twice; `QueryAsync` returns the page size, not the total match
count; `body_data` has no size cap (base64, +33%); the retention `DELETE` is unbatched; and a
PostgreSQL/SQL Server-backed DLQ integration test should join the SQLite one to keep this from
regressing.

---

## [3.5.1] — 2026-08-07

**No changes in redb.Tsak itself — a rebuild that re-pins the dependency on `redb.Route`.**

`redb.Route` 3.5.1 extracted `SharedHttpServerManager` from `redb.Route.Http` into the new
`redb.Route.Http.Hosting`, so that HTTP-based connectors share **one** Kestrel per `host:port`.
`redb.Tsak.Core` 3.5.0 pins `redb.Route.Http` **3.5.0**, which still carries its own private copy of
that server. Nothing in the graph would have upgraded it: `redb.Route.As2` depends on
`Http.Hosting`, not on `Http`, so NuGet had no reason to lift the pin.

The consequence, had this rebuild not shipped: anyone assembling **their own worker from NuGet**
(`redb.Tsak.Core` + `redb.Route.As2`) would run two independent Kestrel managers, and an AS2 receiver
could not share a port with the worker's own HTTP routes — the exact scenario the extraction exists
to enable. Distributions are unaffected either way: the Tsak archive and images build `Libs/shared`
from a single source tree, so the two assemblies there always match.

Also in this build: `redb.Route.As2` is now declared in `scripts/shared-manifest.psd1`, so the AS2
connector actually reaches `Libs/shared` and an `as2://` endpoint resolves inside a worker;
`redb.Route.Http.Hosting` is declared in the framework set so it is covered by the startup fail-fast
and the minor compat-gate instead of arriving as an undeclared transitive file.

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
