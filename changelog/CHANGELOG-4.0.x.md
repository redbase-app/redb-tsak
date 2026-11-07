# redb.Tsak changelog: 4.0.x

Releases of the 4.0 line, moved here unchanged from CHANGELOG.md, which keeps the latest released line only.
Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [4.0.1] — 2026-09-18
### Added — async `InitRoute.main`, and an explicit error for a `main` Tsak cannot use

A module's `InitRoute.main` may now be `public static async Task<IRouteContext> main(IRouteContext context)`
next to the synchronous form. A module that syncs redb schemes or reads reference data in `main` no longer
needs `.GetAwaiter().GetResult()`: the coordinator awaits it, and the context starts only after it
completes. `ITsakModule` gains `InitializeAsync`, a default interface method that calls `Initialize`, so
existing module classes keep working without a rebuild; the coordinator now calls `InitializeAsync`. For
an async `main`, `StaticMethodModule.Initialize` refuses to block and says to use `InitializeAsync`. A
module class that gets `ITsakModule` from a base class and adds `InitializeAsync` must list the interface
again (or override a virtual `InitializeAsync` of the base): C# maps the interface where it is declared,
and the default would run instead. A test double that substitutes `ITsakModule` intercepts the default
too, so it has to configure `InitializeAsync` itself.

A `public static main` on an exported `InitRoute` that fits neither form (a `Task` without a result,
`ValueTask<IRouteContext>`, `void`, other parameters), or more than one `main`, is now an error naming the
type, the signature found and the two supported forms. Before, such a module was skipped without a word,
and two `main` methods threw an `AmbiguousMatchException` that named nothing. A bare DLL now logs the
reason while it is scanned, and a `.tpkg` reload is refused with it instead of "no modules discovered in
the package"; the error drops the whole assembly, other modules in it included. An exception from `main`
now reaches the log and the activation report as the module threw it, not wrapped in
`TargetInvocationException`. Pinned by red-before tests: discovery of an async `main`, the errors for each
unsupported shape and for both forms, the package validation reason, the unwrapped exception, the
synchronous refusal, and on the real coordinator the context starting only after an async initialization
and a failure after `await` reported with its own type.

### Fixed — reloading one package no longer drops the other modules of its named context

A named context can hold modules from several packages, grouped in one appsettings
`Tsak:Contexts:*:Modules[]` section. A package reload hands the coordinator only that package's
modules, and the batch path recreated the context from the batch alone: every reload of one
package silently dropped its neighbours' modules from the shared context until the worker
restarted, and a rollback inherited the same loss. The batch now recreates a named context with
its full membership, the reloaded modules replacing their predecessors, exactly as the
single-module path always did. Startup is unchanged — nothing runs yet, so the membership is the
batch. Pinned by a red-before test.

### Changed — several clusters on one database stay apart (stop every node of the database to upgrade)

`Tsak:Cluster:ClusterName` has always named the root of the cluster tree, and the tree kept equal
group names of different clusters apart, but the coordination records below it did not: node, lock
and assignment records were keyed and looked up by the group name alone. Two clusters on one
database with a group of the same name (`default` in both, say) shared one leader lock and every
singleton-route lock, saw each other's nodes as alive peers and overwrote each other's module
assignments, and a node id repeated in the other cluster re-registered the first cluster's record.
The keys now carry the cluster (`{cluster}:{group}:{node}`, `{cluster}:{group}:{lock}`,
`{cluster}:{group}:{module}`), the records carry a `ClusterName`, and every lookup is scoped to the
node's cluster and group. `RedbDistributedLock` takes the cluster name from `ClusterOptions`; an
instance built by hand without options uses the default cluster name.

The major-version gate added earlier in this cycle is replaced by a protocol gate. A node record
carries the coordination protocol its node speaks (2 as of this release), and a node refuses to
register (critical log, shutdown) while an alive peer of its cluster group speaks another one. That
includes a 4.0.x node of the group: it has neither a protocol nor a cluster on its record, and is
told apart from a same-named group of another cluster by its place in the tree. A rolling deploy
that keeps the protocol is not blocked, whatever the version numbers; the stale record of a stopped
node does not count.

**Upgrade from 4.0.x:** stop every Tsak node of the database, of every cluster, roll out, start.
There is no data migration. Once past the gate, a node's first registration removes the records its
group left before this release (node records under its group, locks and assignments of its group
name), and the cluster rebuilds leadership, locks and assignments on its own. Old locks and
assignments stay while an alive pre-release node with the same group name still runs in another
cluster, since they may be its live records. The key backfill passes of the lock, node and
assignment services are gone: a pre-4.0 keyless record has no cluster either and is removed the
same way. A rollback to 4.0.x also needs every node stopped: the old build drops the unknown
`ClusterName` field when it saves a record, and the next upgrade removes such a record as an old
one, which is harmless for coordination state.

The dashboard in cluster mode reads every cluster of the database, while a node id is unique only
within one cluster group. Cordon, uncordon and node removal from the dashboard now refuse a node id
carried by more than one node record (an error in the log) instead of acting on whichever record
came first; the worker API of the node's own cluster still performs them. The dashboard views still
merge equally named groups of different clusters.

The state and module stores, the DLQ and the audit log, and the Quartz scheduler are scoped to the
cluster as well: see the next entries. Pinned by integration tests on
PostgreSQL and SQLite; red-before: a leader and a singleton-route lock in each of two clusters with one group
name, no shared nodes, a record per cluster for one node id, per-cluster assignments of one module,
the refusal next to an alive 4.0.x node of the group, the removal of the group's old records, and
the dashboard refusal. Guards: an alive 4.0.x node of another cluster's same-named group neither
blocks registration nor loses its locks, the dashboard still cordons an unambiguous node, and the
protocol gate's refusal, same-protocol and dead-peer cases. The lock test that expected keyless
duplicates to be keyed and renewed now pins that such records hold nothing.

### Changed — persisted route and context state and module records are kept per cluster

With `Tsak:Storage:Type = Redb` the state store persists route and context state (autostart
flags, manually suspended and started routes), and the module store keeps a record per module.
Both keyed their entries by the bare key, so on one database an equally named context shared its
flags across clusters (a route stopped in one cluster stayed stopped in the other after a restart),
and two clusters overwrote each other's record of an equally named module. Both stores now store
`{cluster}:{key}`, with the cluster from `Tsak:Cluster:ClusterName` (default `default`, also
without cluster mode) and a `ClusterName` on the record. Prefix queries still return keys in the
form the caller wrote them.

No migration: an entry written by 4.0.x or older has no cluster and is still read, by key and by
prefix, until the node's cluster writes that key, so an upgraded node keeps its flags and module
records. Removing a key or a module removes the old entry too, so a removed value does not come
back through that read. Once one cluster writes a key, the other clusters keep reading the old
entry until they write their own. Code that queries `TsakStateProps` or `TsakModuleProps` by
`ValueUnique` directly sees the new key form. Both stores take `IConfiguration` as a new optional
constructor parameter; an instance built without it uses the default cluster name. Pinned by
red-before integration tests on PostgreSQL and SQLite: the state of one key, prefix queries and
the record of one module per cluster, and the read of old entries with its write and removal rules.
Two existing tests that pinned the stored key now pin the cluster-scoped form.

### Changed — the dead-letter store and the admin audit log are kept per cluster

`tsak_dlq` and `tsak_audit_log` are flat tables shared by every Tsak node of a database. With several
clusters on one database, an operator of one cluster listed, replayed and discarded the dead letters
of all of them (a replay resolves the context by name on the calling node, so another cluster's
failed exchange could run through a same-named route), the audit page showed every cluster's admin
actions, and each cluster's daily retention swept the rows of the others. Both tables get a nullable
`cluster_name` column, written from `Tsak:Cluster:ClusterName`. Listing, replay, discard and retention
of dead letters, and listing and retention of audit entries, now see only the node's cluster; another
cluster's entry is "not found". The schema initializers add the column and an index to an existing
table on startup: `ADD COLUMN IF NOT EXISTS` on PostgreSQL, a `COL_LENGTH` guard on SQL Server, a
`pragma_table_info` check on SQLite.

Entries written by 4.0.x or older have no cluster. They stay visible to every cluster, and every
cluster prunes them by age. Replaying such a dead letter is refused while the database holds more
than one cluster, because it cannot be attributed; with a single cluster, or outside cluster mode, it
replays as before. Discarding it stays allowed. The cluster count comes from the new
`ITsakClusterDirectory` contract in Core, implemented in Core.Pro from the cluster roots and
registered by `AddTsakCluster`. `DlqService` takes the directory as a new optional constructor
parameter, and `TsakAuditRouteBuilder` takes the cluster name as one (the default cluster when
omitted). Pinned by red-before tests on SQLite: per-cluster listing, replay, discard and retention
of dead letters, per-cluster listing and retention of audit entries. Guards: old dead letters are
listed and replayed, old audit entries are listed by every cluster. New-API tests: the refusal with
two clusters, the replay with one, and the cluster count on PostgreSQL. On PostgreSQL and SQL Server,
integration tests write through the real audit route and DLQ capture and read back, discard and
prune per cluster. The upgrade of an existing table was also run with the exact SQL Server statements
in `tempdb`: a 4.0.x table with an old row, the column added twice, then insert, list, load, claim,
update, discard and retention as two clusters, and a fresh database.

### Changed — each Tsak cluster runs its own Quartz cluster

Quartz clusters AdoJobStore nodes by scheduler name, and the shipped `appsettings.json` names the
scheduler `TsakScheduler` for everyone. Several Tsak clusters on one database therefore formed one
Quartz cluster: a node of one Tsak cluster fired the cron triggers of another, and the scheduler
page and API paused, resumed and listed them. With AdoJobStore, a scheduler name that is not set or
is still `TsakScheduler` now becomes `TsakScheduler-{cluster}`, the cluster taken from
`Tsak:Cluster:ClusterName` (default `default`, so a single-cluster install gets
`TsakScheduler-default`). A name set to anything else is a deliberate choice and is kept.
RAMJobStore is not affected.

After the upgrade the scheduler works under its new name. Cron routes register their jobs and
triggers again on start (the Quartz consumer replaces them), so they keep running; the rows of the
old `TsakScheduler` name stay in the `QRTZ_` tables unused. To remove them, delete the rows whose
`SCHED_NAME` is `TsakScheduler` from every `QRTZ_` table while no node runs, the trigger detail
tables and `QRTZ_FIRED_TRIGGERS` before `QRTZ_TRIGGERS`, and `QRTZ_TRIGGERS` before
`QRTZ_JOB_DETAILS`. Pinned by red-before tests of the resolved Quartz options: the shipped name and
no name become cluster-scoped, an explicit name is kept.

### Changed — the audit writer statement follows the connector's `:#name` placeholders

redb.Route.Sql now reads SQL placeholders as `:#name` and leaves `@` to the database, so the audit
insert — the one Tsak statement that runs through that connector — is written with `:#name`; the
PostgreSQL casts ride on the placeholder (`:#event_id::uuid`). Every other statement of the audit log
and of the dead-letter store runs on raw ADO.NET and keeps `@name`. Without this the writer route sent
`@event_id` to the server verbatim: PostgreSQL answered `42703 column "event_id" does not exist`, SQL
Server `Must declare the scalar variable "@event_id"`, and SQLite refused the command outright.

### Fixed — the admin audit trail is persisted on PostgreSQL

With `Tsak:Redb:Provider = postgres`, every admin audit event failed to insert: the writer route
binds the timestamp as ISO-8601 text, and PostgreSQL refuses text for the `timestamptz` column
(`42804`). The audit sink logs each failure and falls back to the log, so the Audit page stayed
empty without anything looking broken. The insert now casts the timestamp on PostgreSQL, as it
already cast the event id and the payload. SQLite (a text column) and SQL Server (implicit
conversion) were not affected. Found by the new PostgreSQL test of the writer route, red before the
cast; the statement test pins the cast.

### Fixed — a module that fails to start is actually rolled back

The 3.7.0 promise that a hot swap "rolls back on ANY start failure" was unreachable for the
failures that matter. `TsakCoordinator` swallows the exception from a module's `Initialize` and
from starting its context — deliberately, so that a bad module cannot crash the node — and the
swap only ever saw success: a new version that never came up was kept and logged as
"Hot-swapped". A `.tpkg` redrop had no rollback at all (its version history was even dropped
before the swap), and a cluster assignment whose module failed was marked Running, so the retry
with backoff never fired.

The coordinator still swallows, but now reports which modules did not come up. A DLL hot swap
rolls back to the previous version, and a rollback whose previous version fails too says so
(critical) instead of "Rolled back". A package reload restores the previous package: the old
module instances return to the registry, their contexts are recreated, the new package is
disposed only afterwards, and the broken file is not re-read on every scan until it changes. The
destructive cleanup of the old version (version history, a replaced DLL module's load context)
now waits until the new one is confirmed up, and a package that fails to open no longer leaves
its old modules missing from the registry. Modules that differ between the two versions are no
longer left running on a disposed package: a module only the new version added has its context
stopped on rollback, and a module the new version no longer ships has its context stopped on a
successful reload (before, it was only unregistered). A failed cluster assignment goes Failed and
into the existing retry.

Found along the way and fixed because a correct rollback depends on it: re-adding a module that
already runs in an anonymous context — every hot swap and package reload does — created a second
context (the anonymous name carries a timestamp and a GUID) and left the old version running next
to the new one, while a package reload disposed the old package under it. A re-added module now
gets its own anonymous context back and replaces it, as named contexts always did.

**Source-breaking:** `ITsakCoordinator.ProcessBatchAsync` and `ProcessModuleAddedAsync` return
`Task<ModuleActivationReport>` instead of `Task`. `await` call sites compile unchanged; a custom
implementation or a test double that returns a plain `Task` must return a report
(`ModuleActivationReport` is a struct whose default means "nothing failed"). Not covered: endpoint
start failures that redb.Route isolates inside an otherwise started context stay invisible to
Tsak, and `HotReload:StartupTimeoutSeconds` is still not enforced — its doc comment now says so.
Pinned by red-before tests: the coordinator report (initialize and context-start failures), the
anonymous-context replacement, swap and rollback outcomes, the cluster assignment state, modules
added or dropped between package versions, and an end-to-end package redrop on the real
coordinator, registry and context manager.

### Fixed — the Dashboard auto-refreshes again and draws its node-status donut

Since the 2026-09-02 review the Dashboard page never refreshed after load, and its NODE STATUS
donut stayed empty. The review moved every page's poll loop onto a shared helper, `PollLoops`,
whose `ConfigureAwait(false)` runs the tick on the thread pool. The Dashboard's tick refreshes
the Watchdog and Lifecycle panels, and their `StateHasChanged()` throws off the Blazor
dispatcher, so every tick died before redrawing the donut or re-rendering the page, and the
helper's catch swallowed it without a log line. The Dashboard tick now runs on the dispatcher
(as NodeDetail's always did), and the panels refresh through `InvokeAsync`, safe from any
thread. Two more roads to an empty donut are closed: the initial draw keyed on `firstRender`,
which in interactive mode is always the loading spinner, and every reload (the Refresh button
included) re-entered that spinner, swapping out the whole topology block and destroying the
chart's canvas mid-refresh. The spinner now belongs to the initial load only.

`PollLoops` itself stops hiding failures: a failed tick is logged as a warning, and only the
loop's own cancellation ends it. Before, any `OperationCanceledException` did, so a single
`HttpClient` timeout (a `TaskCanceledException`) silently killed a page's auto-refresh for the
rest of the circuit. The logger is a required parameter, so no page can opt out. Both loop
defects are pinned by red-before tests; the render lifecycle of the donut needs a live circuit
(the suite has no bUnit) and is not covered by an automated test.

### Docs — the deployment and security guides describe the image as it actually ships

The guides claimed the opposite of what the packaged image does. `DEPLOYMENT.md` listed `Tsak:Api:Host` as
`0.0.0.0` and `Tsak:Auth:Enabled` as `true` "by default", while the code binds loopback and ships
authentication off, and the packaging deliberately turns authentication off in the image (it strips the
development secret, and a worker with authentication on and no secret refuses to start — the quick start would
not start at all). So the image answers the management API on `9090` without a key, on every interface, with
the dashboard on `admin` / `admin` and SQLite inside the container at `/app/redb.db`. All of it is deliberate,
none of it was written down.

The quick start now carries a table of exactly that, with what to set before the port faces anyone else, and a
one-command variant that binds the ports to localhost, requires a key and keeps the store on a volume. The
configuration reference states both defaults as they are — code and image — and says that authentication must
be enabled together with a secret. `SECURITY.md` gains the same summary, including the `SECURITY:` warning the
node logs at every start. The advice to mount `/app/data` was wrong: the store is `/app/redb.db`.

### Fixed — an API key written in configuration works whatever the case of its hash, and its roles may be an array

Two shapes of the same `Tsak:Auth:Keys` entry were seeded, logged as seeded, and then matched nothing — the
worker answered 401 to every request, with nothing in the log to say why. Found by a first-time user who lost
an evening to it.

`ApiKeyService.HashKey` produces uppercase hex, while the seeded value was whatever configuration said. A
lowercase hash — what `openssl dgst -hmac` prints, so the common way to compute it — matched no lookup: the
redb store compares the hash exactly, which is case-sensitive on PostgreSQL and case-insensitive on SQL
Server's default collation, so the same configuration behaved differently per database. The hash is now
normalised (trimmed, uppercased) on the way in and on the way out, in one place both stores share, and a
`KeyHash` that is not 64 hexadecimal characters is refused with an error naming the key instead of being
stored as a key that can never authenticate.

Roles given as an array — `Tsak__Auth__Keys__0__Roles__0=admin`, the only shape environment variables can
express — read as a section, not a value, so the key ended up with no roles at all: under
`Tsak:Auth:EnforceRoles=true` it was refused everywhere, with a message about permissions rather than about
its own configuration. Both forms are now accepted, and a `Roles` section that yields nothing is reported as a
warning. Pinned by red-before tests of the reader; the deployment guide gains the "first API key" section (the
CLI cannot create it — `tsak auth create` is a client of the very API the key unlocks) and the README's CLI
table drops the `keys` level that never existed: the commands are `auth list`, `auth create`, `auth revoke`.

### Fixed — the dashboard routes a node action to the node's own cluster

`RemoveNodeAsync` and `SetCordonedAsync` of the dashboard's `ClusterClientProvider` run through a worker API
first (the audited path): the node itself when alive, otherwise an alive peer. The peers were picked by group
name from a topology that merges every cluster of the database, while a worker's registry is scoped to its
own cluster since the cluster isolation — so removing a dead node of cluster B could be sent to an alive
worker of cluster A with the same group name, whose "not found" (400) was taken as a final refusal: the
dashboard reported failure without asking B's own worker or falling back to the database. Candidates are now
the alive nodes of the node's own cluster and group (records written before cluster isolation, which carry no
cluster, stay together); a node missing from the cached topology is looked up once more before the audited
path is given up. `NodeInfo` in `redb.Tsak.Contracts` gains `ClusterName`. Pinned by red-before tests of the
candidate selection.

### Fixed — a module file Tsak cannot use is reported once, not loaded again on every scan

Since `main` shapes Tsak cannot use became an explicit discovery error, a bare DLL or a package with such a
`main` dropped into a running worker threw out of the hot-reload scan before the file was marked ignored and
its load context unloaded: every scan (10 s by default) loaded the file into one more permanent assembly load
context, extracted the package again, and logged the same error — until the file was removed. Discovery now
runs inside the scan's guard: the reason is logged once, the load context is unloaded (the package disposed),
and the file is skipped until its write time changes, exactly as a non-module dependency is. Pinned by a
red-before test on the bare-DLL path; the package path takes the same guard.

### Fixed — a failed in-place DLL swap rolls back onto the version that was running

The standard deployment overwrites a module DLL discovered at startup in place. Tsak keeps no copy of the old
bytes, so when the new version failed to start, the rollback refused ("overwritten in place and no live
instance remains") and left the module down — and, since the file stayed newer than the tracked version, the
scan swapped it in again on every pass, tearing the module down once per interval with a fresh load context
each time. The swap now keeps the instance that ran the old version and the rollback re-activates it; the
failed file is left alone until it changes. Rollback of a version that was itself hot-loaded is unchanged.
Pinned by a red-before test: an in-place overwrite whose new version is reported failed re-activates the old
instance and is not retried.

### Fixed — a context in which no module initialized is not started

When the only module of a context failed in `main` (or in `Initialize`), the coordinator logged the error and
then started the context anyway, with no routes: the next log line read "Context … started successfully: all
0 endpoints operational", and the dashboard showed the context Running. Such a context is now left created and
stopped, with an error naming the modules that failed; it stays visible and can be started by hand once the
module is fixed. The activation report is unchanged, so a hot swap, a package reload or a cluster assignment
still reacts to the failure. A context in which some modules came up still starts, as before. Pinned by a
red-before coordinator test, with a guard test for the partial failure.

### Fixed — a stopped echo probe says it is stopped instead of asking for an API key

The echo probe (`/api/echo`, route `system-echo`) needs no API key but ships stopped. While it was stopped the
request fell through to the API's catch-all, ran into the API key check and answered `401`, which read as "the
echo needs a key". It now answers `503` naming the route and how to start it; a running echo answers without a
key as before. The README gains an "Echo probe" section and, for module authors, "A module's database": an
unnamed `IRedbService` in a module is the worker's own database, chosen by `Tsak:Redb:Provider` (`sqlite` by
default) — `ConnectionStrings:MSSql` alone does not switch it — while a module's own database is a named
instance under `Redb:{name}`. Pinned by a red-before API test that also checks the running echo needs no key.

### Fixed — a skipped heartbeat says so in the log

`RedbNodeRegistry.HeartbeatAsync` skips the write when the node row vanished between its lookup and the row
lock (a peer removed the dead record, or the node was deregistered) and lets the next tick re-register — but
it did so silently: the method returned as after a persisted heartbeat, and nothing in the log told a
skipped one from a written one. That blindness turned the investigation of a lost write in the core's
hash canon into a long chase. Both branches — the lock not acquired, and the defensive "locked but not
re-read" — now log a warning with the node, group, cluster and record id. Behaviour is unchanged. Pinned by a
red-before integration test that injects the lock refusal through a proxied `IRedbService` (the race itself
cannot be timed reliably) and asserts the warning on a capturing logger.

### Changed — the shared layer filters both frameworks and refuses two versions of one file

`scripts/build-shared.ps1` filtered project output against `Microsoft.NETCore.App` only, while the worker runs
on `Microsoft.AspNetCore.App` as well, so ASP.NET framework assemblies (`System.Security.Cryptography.Pkcs`
among them) landed in the layer in whichever version each project happened to publish — dead weight at runtime,
where the framework copy wins, but a layer that differed from run to run. The filter now covers both frameworks.

When two projects published the same assembly in different versions, the script copied them over each other and
the last project in the manifest won: `Microsoft.IdentityModel.*` 8.0.0 from `redb.Licensing` overwrote 8.16.0
from `redb.MSSql` in the publish layer until the package was pinned. A duplicate is now compared by assembly and
file version, and by its bytes where there is no file version to order by (native files, and resource
assemblies such as `System.Diagnostics.EventLog.Messages.dll`): an equal copy is skipped, a different one fails
the run with the list of files and projects, and `-TakeHighest` keeps the newest and warns instead. The same
rule covers `runtimes/`. `-SelfTest` proves the policy on builds from the local NuGet cache without publishing a
project: a conflict fails by default, equal versions pass, `-TakeHighest` keeps the newest, equal bytes without a
file version pass.

### Changed — Microsoft.Data.SqlClient 7.0.3 and Microsoft.IdentityModel 8.16.0 in the worker

The worker runs on `Microsoft.Data.SqlClient` 7.0.3 (was 5.2.2), the version `redb.MSSql` ships. The driver
needs `Microsoft.IdentityModel.*` 8.16.0 or newer, so the worker's `Microsoft.IdentityModel.*` and
`System.IdentityModel.Tokens.Jwt` pins moved from 8.4.0 to 8.16.0; `redb.Identity.Core.Module` and
`redb.Identity.Http` build on the same version. The driver's two new companion assemblies,
`Microsoft.Data.SqlClient.Extensions.Abstractions` and `Microsoft.Data.SqlClient.Internal.Logging`, sit in the
application bin next to the driver. In the net10.0 layer the RID-specific driver builds in
`Libs/shared/runtimes/<rid>/lib/` are now under `net9.0` (the build the 7.0 line uses on .NET 10), not `net8.0`.
Checked on the DEV layer and in publish mode for net8.0, net9.0 and net10.0 (one driver version in the app bin,
the layer and `runtimes/`); `TsakSqlDialectTests` and `AuditDlqSqlServerTests` pass on a live SQL Server.

Behaviour to know:
- Rebuild a DEV shared layer with `scripts/build-shared.ps1 -Clean -IncludeFramework`. Without `-Clean` the
  5.2.2 builds under `runtimes/<rid>/lib/net8.0` stay in `Libs/shared` next to the new ones.
- A `.tpkg` built before this change still carries `Microsoft.IdentityModel.*` 8.4.0 companions. The host's
  8.16.0 wins in the default load context, but rebuild such packages so the worker and its modules ship one version.
- SQL Server connection strings with `Authentication=Active Directory ...` (the audit and DLQ stores among them)
  need `Microsoft.Data.SqlClient.Extensions.Azure` next to the driver; the 7.0 driver no longer contains
  Microsoft Entra ID authentication, and the worker does not ship that package.

## [4.0.0] — 2026-09-12

### Added — the dashboard can tell shedding from silence

redb.Route's `IEndpointStatistics` grew a `Rejected` counter: requests turned away by an
admission limit (`maxConcurrentRequests` on Http/Soap/As2/Grpc, `maxConnections` on the SignalR
hub) BEFORE a pipeline ran — the transport answered 429/503, no exchange was created, so they
land in neither `MessagesIn` nor `Errors`. The collector read neither that counter nor the
long-existing `BytesOut`, which meant a node shedding load looked exactly like an idle one.
`TsakEndpointInfo` now carries `Rejected` and `BytesOut`, `RouteMetricsSummary` carries
`Rejected`, and they reach the endpoints grid, the endpoint details (Bytes Out beside Bytes In),
the route view and the `tsak diagnostics` tables (`dashboard`, `route`).

The dashboard gained a separate **Shedding Routes** panel instead of folding rejections into
Error-Prone Routes: a route sitting at its concurrency ceiling with zero errors is healthy but
under-provisioned, the operator response is to raise the limit or add nodes rather than go
bug-hunting, and one panel serving both would make its own label lie. Busiest Routes gained a
Rejected column too, for the case where the shedding route is already in the top five by
throughput. Red-before proven: the mapping tests and the dashboard-selection test failed on the
un-fixed collector/controller. The dead snapshot path was deliberately left alone:
`ContextInfoCollector.CollectSnapshotsAsync` and `ContextMetricsCollector` have had no callers
since the heartbeat stopped propagating context snapshots (review 2026-09-02, И12) — the stale
doc-comment on `TsakContextInfo` claiming otherwise now says so instead of lying.

### Added — About page

`/about` shows which build is running and where it came from: the dashboard version, the
selected node's version, machine, uptime and context/module counts, and links to the repository
(github.com/redbase-app/redb-tsak), releases, redb.Route and the redbase.app site. It sits in
the Settings group of the sidebar, and the brand header carries a version link to it. The page
is authenticated like every other one — a build version tells an attacker what surface they are
looking at — and the page-auth guard covers `/about` too.

For admins the page also answers "which redb is REALLY running": a Loaded redb assemblies card
on top of `GET /api/system/assemblies`, which along the way gained a typed contract
(`AssembliesResponse` / `LoadedAssemblyInfo`) and a client method (`GetAssembliesAsync`) — the
endpoint used to return an anonymous object no typed client could consume.
`GET /api/system/info` gained `InformationalVersion` ("3.7.2") alongside the four-part
`Version` ("3.7.2.0"), sourced the same way the startup banner already sources it.

### Fixed — the config binder no longer flags JSON-comment keys as typos

The stock appsettings document keys with the `"//Key": "..."` JSON-comment convention (the
format has no comments), and `RedbConfigBinder` reported every such key as "not a redb setting
and does nothing" — so every deployment running the SHIPPED config started with false
`Tsak:Redb://Provider` / `Tsak:Redb://Cache` warnings, training operators to ignore the very
diagnostic that exists to catch real typos. The real `Provider` and `Cache` keys were always
applied; only the comment keys tripped the check. `//`-prefixed keys are skipped now, and a
genuine unknown key still warns — both pinned by red-before tests.

### Fixed — monitoring charts no longer freeze under the cursor

A NodeDetail poll tick crashed whenever the mouse hovered the CPU or Threads chart (found
live, 2026-09-09): those datasets legitimately pass no fill color, JSInterop serializes the
C# null verbatim, and the interop mapper's `PascalCase || camelCase` fallback turned "absent"
into an explicit JS null. Chart.js v4 derives `hoverBackgroundColor` by parsing
`backgroundColor`, and its color parser routes null into the object branch
(`typeof null === 'object'`) — a TypeError on every `update()` with active hover elements, so
a hovered chart froze for as long as the cursor sat on it while the poll loop retried tick
after tick. The mapper now omits absent colors entirely; Chart.js falls back to its own
defaults, hover or not.

### Fixed — the login form actually logs in: antiforgery token rendered, error redirects reach the page

Two stacked defects made the dashboard sign-in a dead end the moment the page-level auth gate
went live (found live, 2026-09-08). First, `<AntiforgeryToken/>` in the login and logout forms
was not resolved as a component — `_Imports.razor` lacked `Microsoft.AspNetCore.Components.Forms`
— so it rendered as inert markup, the forms carried no token, and EVERY login POST failed
antiforgery validation before credentials were even read; the only build-time symptom was an
RZ10012 warning. Second, the error redirects were relative (`login?error=1`): resolved against
`/auth/`, they sent the browser to GET `/auth/login` — a POST-only mapping — so instead of an
error message the user hit a bare HTTP 405; logout had the same bug. Redirects are absolute now,
a stale/missing token reports its own error code ("The form expired") instead of masquerading as
wrong credentials, and three red-before tests pin the flow: the rendered token, the redirect
targets, and that the redirect target itself answers 200.

### Fixed — redb.Route.Xml declared for the shared-layer fail-fast and compat-gate

The Route-XML wave added `redb.Route.Xml` to the manifest's Framework section (it is a
compile-ref of redb.Tsak.Core and ships in Libs/shared) with a comment saying it is "declared so
the preload fail-fast and the compat-gate see it" — but those gates read the C# mirror,
`SharedRuntimeBootstrap.FrameworkAssemblies`, which was not updated. The assembly therefore got
neither the byte-preload fail-fast nor the minor compat-gate: a shared layer missing or
mismatching `redb.Route.Xml.dll` would start the worker and fail later (FileNotFound deep in
AddTsak, or a MissingMethodException on the first XML module) instead of aborting at startup
with the precise "rebuild the shared layer" message. Exactly the drift class the guard test was
written for (redb.Route.Http.Hosting, 3.5.1) — and the test did catch this one; the name is now
declared and both consistency tests are green.

A `.tpkg` can now carry declarative XML routes beside (or instead of) assemblies. The manifest
gained four optional fields — `Artifacts` (explicit ordered list of `.route.xml` entries),
`SchemaVersion`, `Resources` (in-package directory file-references resolve from) and
`RequiredConfigKeys` — a package without them behaves exactly as before (pinned by test).
`ModulePackage.Open` reads the artifacts from the SAME verified bytes the signature gate
checked and extracts the resources directory to a per-content temp folder (the package itself
is never unpacked; XSLT/schema references need real file paths), cleaned up on `Dispose`.
Discovery registers an `XmlRouteModule` per package with artifacts, AFTER the assembly scan —
so `bean:#name` objects registered by module code are visible to the XML — and an XML-only
package is now a full module (before, it died on the no-modules-found path and was disposed).
`XmlRouteModule.Initialize` fails fast when the MERGED context configuration (which the
coordinator writes into context properties before initialization) lacks a required key —
module-isolated by owner decision: the module faults with the key names, its routes never
register, the context lives on. Bean types resolve package-first (entry-point ALC →
companions → the default resolver's view of shared/Default), file references resolve from the
package's extracted resources — both as chained context services, so several packages share
one context without stepping on each other. The embedded `{Name}.config.json` now reaches the
coordinator through the new `IEmbeddedConfigModule` contract instead of a concrete-type match
(`StaticMethodModule` implements it; `XmlRouteModule` too), so every packaged module kind gets
the same layer-4 treatment.

### Added — XML modules see every markup contribution; context.xml ships in the package

`XmlRouteModule` now discovers the Р21 markup contributions worker-wide (the package's own
assemblies plus every loaded `redb.*` assembly — the preloaded shared layer above all) and
feeds them to the loaders, so a packaged XML route can use `<cache>`, `<rest>`, `<redbGet>`
and any other package element — before this the Tsak path knew only the core elements. The
optional `context.xml` at the package root (the Ф5.1 layout has always staged it) is read
from the verified bytes and applied BEFORE the route artifacts: components, beans, `<onInit>`
and context-level blocks such as `<redb>` (scheme sync at startup, named idempotent
repositories).

### Fixed — hot reload learned the third convention too (Route-XML Ф5, такт 5 E2E)

The live-worker E2E caught what the unit suite could not: the whole hot-reload pipeline still
knew only the two assembly conventions. An XML-only `.tpkg` dropped into a RUNNING worker was
opened, found module-less, disposed and ignored; one adopted at startup lost its tracking the
same way, so a redrop never reached `ReloadPackageAsync`; and the staged-validation probe
refused any XML-only package with "no entry-point assemblies loaded". All three now run the
same artifacts branch discovery runs (probe-side the staged check is well-formedness of each
artifact — schema/compile errors stay load-time module-isolated faults). Two lifecycle bugs
fell out of the same session: the extracted-resources directory was keyed by content, so a
transient open's `Dispose` (the probe, a rescan) deleted the resources out from under the live
module — each `ModulePackage` instance now extracts to its own directory; and on reload the
old package was disposed BEFORE the context swap, deleting resources the still-live old routes
could be reading — it is disposed after `ProcessBatchAsync` now (and kept tracked when opening
the replacement fails, since the old context stays live). Verified end-to-end: redrop of a
changed XML package → staged validation → silent replace → batch context recreation → the new
route ticking within one debounce window, config placeholders in the endpoint URI resolving
through the merged package config (`{{demo.period:2000}}`).

### Fixed — the worker starts on SQLite again: clustered Quartz is forced off there

The В11 fix shipped `quartz.jobStore.clustered=true` unconditionally, and Quartz hard-refuses
that on SQLite at startup ("SQLite cannot be used as clustered mode due to locking problems" —
its clustered mode serializes nodes on `QRTZ_LOCKS` row locks, which SQLite does not have; the
3.2.x changelog note and `sanitize-appsettings.ps1` both knew this, but a dev run never passes
through the publish sanitizer). `ConfigureQuartz` now forces `clustered=false` on the SQLite
provider — the AdoJobStore and its persistence stay — with a startup warning, and the В11
critical no longer demands clustering there: a SQLite database is a local file no second node
can share, so non-clustered is safe by construction, and the double-fire scenario the В11
check guards against cannot arise. The startup summary reports the EFFECTIVE clustered value
rather than the raw config. The shared-database providers keep the В11 behavior unchanged
(pinned by test both ways).

### Changed — the BR-7/8/9 mitigations are gone: Tsak sits on the core's new primitives

The core closed all three reports filed from the 2026-09-02 review the same day, and Tsak now
uses the first-class mechanisms instead of its local workarounds. `GetByPrefixAsync` no longer
falls back to a full load for prefixes with LIKE metacharacters — StartsWith operands are
literals on every provider now (BR-7); the ordinal re-check stays, because it serves the
case-sensitivity contract (BR-6), not escaping. The state/module stores dropped their local
retry around `SaveByUniqueAsync` — the core itself retries an object-key race including the
Remove+Set+Set interleave — and the api-key rotation branch (and the backfill's index
attribution that guards against deleting live keys) now discriminates via
`RedbUniqueViolationException.Kind` instead of parsing constraint names and driver text (BR-8).
Every CAS site (lock renew/self-renew/takeover/release, assignment fence, heartbeat, cordon)
uses the strict `LockForUpdateRequiredAsync`: a deleted or re-created row throws
`RedbLockNotAcquiredException` instead of silently locking nothing, replacing the hand-rolled
`fresh.id == locked id` checks (BR-9). Full matrix re-run: unit 652/652, Web 63/63,
integration 65/65 on PostgreSQL, MSSQL and SQLite — the LIKE-metacharacter prefix test now
passes through the server-side path, pinning the core fix from the consumer side.

### Security — the dashboard actually requires a login now, and five more holes from the 2026-09-02 review

A review of the dashboard found that the cookie auth added in August never gated a single Blazor page: `AuthorizeRouteView`
enforces only the `[Authorize]` metadata of the routed page itself, no page carried the attribute,
there was no fallback policy — so an anonymous visitor got a live circuit and read the topology,
live logs with stack traces, the DLQ, the audit trail and the API-key inventory through the
server-held service key; three code comments documented the false belief that the component blocks
anonymous access by itself. Every page now inherits `[Authorize]` from `_Imports.razor` (Login
keeps `[AllowAnonymous]`), and the invariant is pinned by `PageAuthTests` — ten pages, each proven
red-before (an anonymous 200 with data) and green-after (302 to /login). On top of the gate, page
tiers now match the worker API's: Audit and Auth are Admin, DLQ and the log tails (page and
NodeDetail tab) are Operator — a viewer could previously replay dead-lettered business flows and
read the admin trail through the BFF's admin key.

The rest of the security batch: plaintext passwords no longer reach the admin audit trail —
`[AuditSensitive]` moved to Contracts (it lived in Core, which the DTOs cannot reference, so it
was applied to NOTHING) and now marks the user-management password fields, with a name-based
mask (`password`/`secret`/`token`/`keyhash`) as defense-in-depth, byte[] arguments recorded as
`byte[N]` instead of ~1.3x-package-size base64, and the log-fallback line truncated. The module
signature trust anchor holds on every load path now: startup discovery verifies packages (it
executed them unverified on every process restart), a bare .dll is refused outright under
`Signature:Required=true` (it was arbitrary code execution past the gate), and the verified bytes
are the loaded bytes — `ModulePackage.Open(byte[])` closed the verify-then-reopen swap window.
Session lifecycle: cookie principals are revalidated every 5 minutes and Blazor circuits carry a
`RevalidatingServerAuthenticationStateProvider`, so a deleted or demoted user no longer rides an
8-hour sliding cookie; behind the documented nginx deployment `Tsak:Web:TrustProxyHeaders=true`
turns on forwarded headers so the cookie gets its Secure flag and the login throttle sees real
client addresses. That throttle now keys on (username, client IP) with a wider per-IP spray
bucket — keying on the username alone let anyone lock the real administrator out from anywhere
with five requests a minute — and sweeps its map (it grew unboundedly on attacker-chosen names).
Rebalance and node removal carry `[AuditAdminAction]` like their cordon siblings, and the
dashboard's cordon/remove go through a worker's audited API whenever one is reachable, falling
back to the direct DB write only with an explicit warning.

### Fixed — the review's correctness batch: cluster protocol, stores, hot-reload, dashboard plumbing

Cluster protocol. Every CAS in the lock and the assignment manager verifies that the row it
re-reads under `FOR UPDATE` is the row it locked: a lock on a deleted id is a silent no-op on all
three providers, so a delete+recreate in the window let a stale writer pass the epoch fence on an
unlocked row (and, via `AutoSwitchToInsert`, resurrect a just-released lock); `ReleaseAsync` is
fenced like every other transition. The heartbeat writes only its own fields under a row lock —
its whole-object read-modify-write (widened by a contexts snapshot nobody reads, now dropped)
silently reverted a concurrent cordon within one heartbeat. Cordon actually means "no new work":
rebalance no longer assigns modules to cordoned nodes. A clustered route stopped and restarted
through the API works again (`OnResume` could never restart the watch loop after `OnStop`). A
`:` in ClusterName/GroupName/NodeId fails fast at bootstrap with an actionable message instead of
a per-tick crash-loop, one module named with `:` no longer freezes the whole group's rebalance,
and legacy `:`-named node records stay removable through the API via a props fallback. A node
record stuck Online in a leaderless group (nobody left to mark it Dead) can be removed once its
heartbeat is older than the dead-node timeout.

Stores. The api-key store survives rotation (same id, new hash updates the record in place —
previously an unhandled unique violation into the auth path and a permanently dead key), latches
`_seeded` only after the backfill/seeding succeeded (the early latch let concurrent first
requests 401 valid legacy keys, and one transient error disabled them until restart), and
`RevokeAsync` waits for the ensure step. The V4 backfill distinguishes WHICH unique index fired
(`StructureId`/`PropertyName`/constraint/driver text): a violation of the `[RedbUnique]` Id index
— the trace of a pre-V4 key rotation — no longer deletes the live credential (red-before: the
row was destroyed), and an unstorable key (e.g. >440 chars) reports critical instead of crashing
the worker at every start. State/module stores run their own one-time ensure (scheme sync +
backfill), so a read racing `TsakHostedService` startup — or a host without it — no longer sees
legacy state as absent (which ended in the backfill deleting the pre-upgrade value); state keys
are normalized via `RedbUniqueKey.Normalize` (440-cap; long keys used to throw where
value_string accepted them), and a prefix containing LIKE metacharacters falls back to a full
scan — on MSSQL `[` opened a character class and the matching keys silently vanished from
`GetByPrefixAsync` (red-before on MSSQL). Group backfill keys a group only under a KEYED root
and bootstrap verifies the parent chain, so a tree damaged by a pre-4.0 race can no longer get
its live group keyed under the "loser" root while the log advises deleting exactly that subtree.

Hot reload. Rollback of an in-place-overwritten bare DLL no longer "restores" the broken bytes
it just failed on: a live previous ALC is reused, and with none left the rollback refuses loudly.
A shared-layer change on disk is reported once as critical requiring a process restart — the old
path drained and rebuilt every context on the SAME old assemblies while logging a completed
reload. Staged validation loads companion DLLs into the throwaway ALC, so a rejected package no
longer publishes its dependencies into the process-wide tracker. Bare-DLL tracking is keyed by
the real module name (file basename differed → removal unloaded the live ALC while unregistering
the wrong key). The watchdog restarts a context at most once per scan, never `_system`, and a
restart that timed out between stop and start attempts a recovery start instead of leaving the
context down.

Dashboard plumbing. All eight polling pages share one loop body that survives a transient error
(one timeout used to kill auto-refresh silently for the circuit's lifetime — frozen "Online"
under a live Auto-refresh label) and create their CTS before the initial load (a navigation
during a slow load used to leak an unstoppable orphan poll loop). The leader is resolved per
group by the exact leader-lock identity — the old `EndsWith(":leader")` scan showed an arbitrary
group's leader everywhere and a route named `leader` could spoof it. Node clients are cached
with `GetOrAdd` (no leaked sockets on a race), recreated when a node re-registers with a new
endpoint (the dashboard used to call the old address until a Web restart), and refuse an
ambiguous NodeId instead of first-match; control operations (force-stop, context restart/stop)
use a patient client (`Tsak:Web:ControlTimeoutSeconds`, default 60s) instead of failing falsely
at the 5s polling timeout; `IsAlive` respects the configured dead-node timeout instead of a
hardcoded 60s; deep links self-heal the topology; the node-selector no longer shows the previous
node's numbers under the new node's label; the Audit page got the same null-guard as DLQ.

### Changed — honest defaults and contracts (2026-09-02 review)

The shipped cluster config no longer registers `http://localhost:9090` as every node's
ApiEndpoint (empty = auto-detect, with a loud warning when a loopback address is registered in
cluster mode; the k8s manifest passes the pod IP). The shipped Quartz config is
`clustered=true` — a persistent AdoJobStore on the shared redb database with clustering off
double-fires triggers across nodes and corrupts the job store; the forbidden combination is
now flagged critical at startup. `HotReload:RollingUpdate` defaults to false and is documented
as RESERVED: nothing ever implemented it (`RollingUpdateCoordinator` has no callers), while
three READMEs promised sequential node updates — enabling the flag now logs a warning and the
READMEs tell the truth. `ITsakStateStore` pins ordinal, case-sensitive keys as the CONTRACT and
`InMemoryTsakStateStore` complies (it folded case, so standalone and cluster answered prefix
queries differently). Reported to redb core:
StartsWith does not escape LIKE metacharacters, the `SaveByUniqueAsync` retry gap, and
`LockForUpdateAsync` on a missing id being a silent no-op.

Verification: unit 652/652, Web 63/63 (10 new page-auth checks), integration 65/65 on each of
PostgreSQL, MSSQL and SQLite; the key invariants proven red-before against the pre-fix code in a
worktree (all four storage tests and all ten page-auth checks failed there).

### Changed — cluster and store uniqueness is enforced by the database (V4 keys). BREAKING: no mixed-version cluster across this upgrade

Everywhere Tsak needed "exactly one record per key" it used check-then-insert with self-healing:
the distributed lock's own comment admitted the first-create race ("no unique constraint on the
lock key") and swept duplicates on renew; the cluster bootstrap had no healing at all, so two
nodes starting on a fresh database could create two cluster roots — two disjoint trees, forever;
node registration hid behind a lock that itself raced; assignments were a plain save loop, so a
stale leader could duplicate one module onto two nodes; the state/module/api-key stores raced on
seeding and concurrent writes. All of it now rests on the V4 core key `_objects._value_unique`
(one partial unique index per scheme, PostgreSQL/MSSQL/SQLite alike): locks key on
`"{group}:{lockName}"`, roots on the cluster name, groups on `"{cluster}:{group}"`, nodes on
`"{group}:{nodeId}"`, assignments on `"{group}:{module}"`, the stores on their natural keys.
A lost creation race is a synchronous `RedbUniqueViolationException` — the loser adopts the
winner instead of leaving a duplicate (first-wins for the lock and the tree; the stores upsert
via `SaveByUniqueAsync`, which is their honest last-writer-wins). Assignment writes carry an
epoch fence on top: a stale leader (lower epoch) is refused under a row lock, so it can no
longer clobber the current leader's placement. The renew-time duplicate sweep, the
verify-after-write in lock creation and the in-memory parent filter in bootstrap are gone; the
hot paths (heartbeat, lock renew, api-key check) are single probes of the unique index. The
api-key store grew a second key — `[RedbUnique]` on the new `Props.Id` — so revocation resolves
by one indexed probe instead of loading every key; the state store's `GetByPrefixAsync` is
server-side now and **case-sensitive** (owner decision; an ordinal re-check keeps the contract
identical on SQLite/MSSQL, whose `LIKE` folds case). The dead `TsakModulePlacementProps` model
(`_tsak_modules` — never synced, never read, never written) is removed.

Existing databases migrate themselves: an idempotent backfill runs from the same ensure hooks
that sync the schemes, stamps the key onto legacy rows (winner = lowest id, the same winner the
old self-healing picked), deletes operational duplicates (locks, nodes, assignments, store
entries) and never deletes a tree anchor — a duplicate root or group is reported critical for
manual cleanup instead. **Upgrade note (the reason this is a major):** a keyless record written
by an old node is invisible to the new key lookups, so nodes of ONE cluster group must not mix
versions across this boundary — stop the whole group, roll out, start; the first start runs the
backfill. On a large shared database prefer the core's DBA path: start nodes with
`Tsak:Redb:AutoApplyDatabaseUpgrades = false` (the config binder already reaches it) and apply
`redb schema --upgrade` from one place. Covered by 13 new integration tests — key contract,
epoch fence, upsert semantics, backfill (winner/losers, anchors, idempotence), and barrier races
(6× parallel bootstrap, 8× state writers, parallel module discovery and node registration) —
green on the three providers (61/61 each), with the four key invariants proven red-before
against the pre-refactoring code in a worktree; the pre-existing suites pass unchanged
(unit 652/652).

### Security — the API-key throttle now keys on the real client behind a chain of proxies

`Tsak:Api:AuthThrottle:TrustProxyHeaders` took the **right-most** `X-Forwarded-For` hop as the client.
That hop is what the nearest proxy appended, so a client cannot forge it, and it is the client when
exactly one proxy stands in front of the worker. Behind a chain, Anti-DDoS to nginx to worker, it is the
Anti-DDoS gateway's address: every caller behind that gateway landed in one throttle bucket. Ten failed
key checks a minute from anyone locked everyone out for two minutes, and, because a valid key clears the
counter, any legitimate login through the same gateway reset the attacker's count. Too coarse and too
weak at once. The single-proxy assumption was stated in the 3.7.0 notes; a reader of the release article
pointed out what it costs behind a chain.

The resolution now happens where it belongs, on the shared Kestrel host, from
**`Tsak:Http:TrustedProxies`** (addresses or CIDR networks). The host walks `X-Forwarded-For` from the
right past every listed proxy to the first address that is not one, ignores the header from a peer that
is not listed, stops on an entry it cannot parse, and rewrites the connection before any consumer runs.
So `redbHttp.RemoteAddress` is the client for the management API and for every module route on Http,
Soap, As2 and gRPC alike, and `redbHttp.Url` carries the scheme the client used. `SystemContextBuilder`
no longer parses the header at all. A value in the list that does not parse fails the start rather than
being dropped, for the reason 3.7.2 gave: a silent drop from a security list makes a typo and a working
configuration look the same.

**Deprecated:** `Tsak:Api:AuthThrottle:TrustProxyHeaders`. It still works as before when
`Tsak:Http:TrustedProxies` is empty, with a warning naming the replacement, and is ignored with a log
line once the list is set. Behind one proxy the two are equivalent; behind a chain only the list is
right. See `PARAMETERS.md`.

### Fixed — the Audit and Dead-letter pages never loaded on PostgreSQL

Both pages, and the `/api/audit` and `/api/exchanges` calls behind them, answered
`42P08: could not determine data type of parameter $1` on every request that carried no filter, which
is every first open. The statements spell each optional filter as `(@x IS NULL OR col = @x)`, and a
parameter whose value is `DBNull` and whose type was never set reaches PostgreSQL as `unknown`. The
parser fixes the type of the first `@x` it meets, the one inside `IS NULL`, before the comparison to
its right can say what the type is; the two occurrences then disagree and
`check_parameter_resolution_walker` refuses the statement. Position 255 in the audit error and 190 in
the dead-letter one are exactly those first occurrences. SQLite and SQL Server never showed it,
because neither infers parameter types from the statement, and the tests for both services ran on
SQLite only, so the Postgres branch had never executed.

Two more defects sat in the same audit query and only reached Postgres. Date filters were bound as
ISO-8601 text and compared against a `timestamptz` column, for which no operator exists, so a
`since` / `until` filter failed the moment it was set; and the daily retention sweep bound its
cutoff the same way, so `tsak_audit_log` has not been pruned on Postgres since the sweep shipped. The
dead-letter store had already learned the timestamp lesson and bound a real `DateTimeOffset`, but
its null strings were untyped all the same.

Every parameter of both services now goes through one `DbParameterBinding`: strings and null strings
as `text`, integers as `int4`, timestamps as the column's own type, a UTC `DateTimeOffset` on
PostgreSQL and SQL Server and the ISO `"o"` string SQLite stores and orders by. A duplicate binding
of `limit` / `offset` in the dead-letter query went with it. Pinned by typing tests on an
`NpgsqlCommand` without a server and by five round trips against a live PostgreSQL in
`redb.Tsak.Tests.Integration`, all five red on the previous code: first open of each page, a date
filter, a status filter, and the retention sweep. Reported from a node that moved 3.1.0 to 3.7.2; the
pages did not exist on 3.1.0, and on Postgres they had not worked since they did.
