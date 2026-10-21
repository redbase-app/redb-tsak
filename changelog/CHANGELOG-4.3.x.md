# redb.Tsak changelog: 4.3.x

Releases of the 4.3 line, moved here unchanged from CHANGELOG.md, which keeps the latest released line only.
Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [4.3.0] — 2026-10-14

> **`redb.Tsak.Templates` — why the bump.** No template change of its own beyond its README, which no longer
> describes the start-up warning about the built-in retention routes (they stopped declaring `.Cluster(true)`,
> see below). The pack moves to 4.3.0 because the `tsak-worker` host pins `redb.Tsak.Core` of its release: a
> host generated from it now runs a Quartz scheduler per route context, and a clustered installation upgrades
> with a full stop of every node.

### Changed — a Quartz scheduler per route context, as Apache Camel has one per CamelContext

**Clustered installations upgrade with a full stop of every node** (see `DEPLOYMENT.md`, *Upgrading to the
per-context scheduler*). There is no compatibility mode with the old shared scheduler.

Tsak used to run one `IScheduler` for the whole node and inject it into every context. Job keys are unique within
a scheduler only, so two modules that knew nothing of each other and both said `cron://nightly` claimed the same
job: one schedule silently replaced the other, and stopping one module deleted the other's job. Every context now
builds its own scheduler with its first `cron:` / `qtimer:` route (redb.Route.Quartz), named
`{instanceName}-{context}` — `TsakScheduler-{cluster}-{context}` by default — with its own job namespace and, on a
clustered job store, a Quartz cluster made of the nodes that run that context. The scheduler starts after the
context has started and shuts down with it.

- **Configuration.** The `Quartz` section is unchanged and applies to the scheduler of every context. The thread
  pool is per context now: `quartz.threadPool.maxConcurrency` (or its older name `threadCount`, which the shipped
  `appsettings.json` uses with `10`) sizes each context's pool; without either, a context gets `4`.
- **DI.** `IScheduler` is no longer a service of the host and `QuartzHostedService` is gone; the host registers
  `QuartzSchedulerOptions`, which every context receives. Code that took the scheduler from DI reads the context's
  own, `context.GetService<IScheduler>()`, once a Quartz route has started.
- **Database (clustered `AdoJobStore`).** The rows of the old scheduler name, `TsakScheduler-{cluster}`, are no
  longer read. A node never deletes or pauses what a shared store holds; removing those rows is an operator
  command (`DEPLOYMENT.md`).
- **API — additive, nothing removed or renamed.** `GET /api/scheduler/status` keeps its fields as the node's
  summary (counts are sums, `isStarted` = all started, `inStandbyMode` = at least one) and adds `schedulers[]`,
  one entry per context (`context`, `schedulerName`, `schedulerInstanceId`, `isStarted`, `inStandbyMode`,
  `isShutdown`, `totalJobs`, `runningJobs`, `status`). Jobs in `/scheduled` and `/jobs` carry `context`.
  `pause-job`, `resume-job` and `fire-job` take an optional `context`: without it a key held by one context is
  acted on, a key held by several is refused with the list of contexts — never guessed. `start` and `standby`
  take an optional `context`; without it they act on every scheduler of the node.
- **Client.** `ITsakApiClient` gains overloads with `string? context`; the existing calls compile and behave as
  before.
- **CLI.** `tsak scheduler status` lists the contexts; `jobs` and `running` show a Context column; `start`,
  `standby`, `pause`, `resume` and `fire` take `--context`.
- **Dashboard.** The Scheduler tab of a node lists the scheduler of every context with its own start / standby,
  the job grid has a Context column, and a row's pause / resume act in that row's context.

### Changed — `.Cluster(true)` is not applied to a Quartz route

A clustered job store already fires each job on one node of the cluster. A leader-only route policy on top kept
the consumer on the leader alone, and every fire another node took found no consumer there and was lost.
`ClusteredRoutePolicyFactory` now leaves a route consuming from `cron:` / `qtimer:` on every node and logs a
Warning naming the route; drop `.Cluster(true)` from such routes.

The built-in retention sweeps `tsak-audit-retention` and `tsak-dlq-retention` are such routes and no longer declare
`.Cluster(true)` (they had since the sweeps became cluster-singletons): every clustered worker logged the Warning
for both at each start, about routes its operator did not write. On a clustered job store each sweep still runs on
one node; on a store that is not shared every node runs it, which the idempotent prune makes harmless.

### Fixed — dashboard: a page above the user's role answered with the login form

Logs and Dead-letter need the operator role, Audit and Auth & Users the admin role. A signed-in `viewer` who
opened one of them was sent to the login form, which read as a logout, and signing in again led back to the same
refusal. The menu offered all four pages to every role. Now such a user gets an "Access denied" page that names
the role the pages need (`/denied`, also where the auth cookie sends a refused request), stays signed in, and the
menu shows a page only to a role that opens it. An anonymous visitor is sent to the login form as before.
Tests: `ViewerAccessTests`.

### Changed — the checkpoint DLQ under redb.Route's error model, and a module's own dead letter channel

- The DLQ handler is the context's error handler, so it sees the failures no `OnException` took, whatever the
  route's consumer — a timer or an HTTP route as well as a Quartz one. An `OnException` that takes a failure owns
  it, as in Apache Camel: a logging `OnException` without `Handled()` keeps that failure out of the DLQ.
- The handler is installed when Tsak starts the context, after the module configured it. A module that declares
  `<deadLetterChannel>` in its `context.xml` (or sets an error handler in code) no longer fails to load: the
  failure is captured first, then the module's handler sends it on and marks it handled. A restart does not wrap
  the handler twice.

### Added — `tsak-dlq:`

The checkpoint DLQ as an endpoint, for an `OnException` that takes a failure and still wants it dead-lettered —
Camel's `onException(...).to(deadLetterUri)`:
`OnException<HttpRequestException>().Log("upstream down").To("tsak-dlq:")`. It captures what the context's handler
would: the route must have left a replay checkpoint (`.Replayable("…")` before the failing step); without one
nothing is captured and a Warning names the route. Sent to outside an `OnException` body it fails the exchange.

### Fixed — scheduler actions that were refused were reported as done

`tsak scheduler start | standby | pause | resume` printed success whatever the node answered (only `fire`
checked); they now print the node's message and exit with `1`. The dashboard's scheduler actions likewise showed
a success toast for a refused action.
