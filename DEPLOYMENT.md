# redb.Tsak — Deployment Guide

How to run the published redb.Tsak containers: images, configuration, passwords,
storage modes, the dashboard, deploying your own modules, and reverse-proxy setup.

> This guide is for **operators deploying the released images** from `ghcr.io/redbase-app/`.

---

## 1. Images

Published to `ghcr.io/redbase-app/` (public, cosign-signed):

| Image | What it is | Ports |
|-------|------------|-------|
| `redb-tsak-worker:<version>` | Process-automation runtime (route contexts, modules, cluster). | `9090` (management/REST API) |
| `redb-tsak-web:<version>` | Blazor dashboard (monitoring, routes, logs). Talks to a worker. | `8085` |
| `redb-tsak-stack:<version>` | Worker **+** dashboard in one container (supervisord). | `9090`, `8085` |

Tags: `:<version>` (= `:<version>-net10`), `:latest`; `<version>` is the release you deploy, as listed on the [releases page](https://github.com/redbase-app/redb-tsak/releases). The worker, web and stack images run on .NET 10; a worker image
can also be built for another target framework, and then carries that suffix (`:<version>-net9`). The standalone
archives additionally bundle the shared route connectors for net8 / net9 / net10, so user modules (`.tpkg`)
can target any of the three.
Pro features (cluster, advanced storage) are on in the image and need no license token (§9).

```bash
docker pull ghcr.io/redbase-app/redb-tsak-stack:<version>
```

---

## 2. Quick start

```bash
# Stack (worker + dashboard) — full Pro on embedded SQLite, one-node cluster, no key needed.
docker run --rm -p 9090:9090 -p 8085:8085 ghcr.io/redbase-app/redb-tsak-stack:<version>
# dashboard:  http://localhost:8085   (login admin / admin)
# REST API:   http://localhost:9090/api/health/live
```

The shipped `appsettings.json` boots with **SQLite + Pro enabled + a one-node cluster** (see §6) —
zero external dependencies and **no license key**: Pro runs free and unrestricted (§9). Override anything via env vars
(§4).

### What the image ships with — and what to change before you leave it running

The image is built for one command, so it starts with no secrets of its own: the packaging strips the
development secret, and authentication is **off** — with authentication on and no secret the worker refuses to
start, which would defeat the point. The consequences, all deliberate:

| As shipped | Means | Before it faces anyone else |
|---|---|---|
| `Tsak:Auth:Enabled=false` | The management API on `9090` answers **without a key**: stop contexts, upload modules, issue keys, dump config. The node logs a `SECURITY:` warning at every start saying exactly this. | Set `Tsak__Auth__Enabled=true` **and** your own `Tsak__Auth__Secret`, then add a key (see "The first API key" in §4). |
| `Tsak:Api:Host=0.0.0.0` | The API listens on every interface of the container — it has to, or a published port would reach nothing. Outside a container the code default is loopback (`127.0.0.1`). | Publish the port only where it belongs: `-p 127.0.0.1:9090:9090`, a private network, or behind a proxy that terminates TLS (§5). |
| Dashboard login `admin` / `admin` | Anyone reaching `8085` administers the node. | `Tsak__Web__AdminLogin` / `Tsak__Web__AdminPassword`. |
| SQLite at `/app/redb.db` | State lives inside the container and goes away with it. | Mount it: `-v tsak-data:/app` keeps the store and the logs, or point `ConnectionStrings__Sqlite` at a path you mount (`Data Source=/data/redb.db` with `-v tsak-data:/data`). |

```bash
# The same quick start, but reachable only from this machine and with a key required.
docker run --rm -p 127.0.0.1:9090:9090 -p 127.0.0.1:8085:8085 \
  -e Tsak__Auth__Enabled=true \
  -e Tsak__Auth__Secret="$(openssl rand -hex 32)" \
  -e Tsak__Web__AdminPassword="$(openssl rand -hex 16)" \
  -v tsak-data:/app \
  ghcr.io/redbase-app/redb-tsak-stack:<version>
```

With authentication on, the dashboard needs a key of its own: see "The first API key" in §4 — the CLI cannot
issue it, because `tsak auth create` is a client of the API that key unlocks.

---

## 3. Storage modes

`Tsak:Storage:Type` + `Tsak:Redb:Provider` decide where state lives.

| Mode | Config | Use |
|------|--------|-----|
| **SQLite (default)** | `Storage:Type=Redb`, `Redb:Provider=sqlite`, `ConnectionStrings:Sqlite=Data Source=redb.db` | Single box, embedded, full Pro + one-node cluster out of the box. |
| **Postgres / MSSql** | `Redb:Provider=postgres\|mssql` + `ConnectionStrings:Postgres\|MSSql` | Multi-node cluster, shared store. |
| **InMemory** | `Storage:Type=InMemory`, `Redb:Provider=` (empty) | Stateless demo, no Pro/cluster, nothing persists. |

```bash
# Postgres-backed, persistent:
docker run -p 9090:9090 \
  -e Tsak__Storage__Type=Redb \
  -e Tsak__Redb__Provider=postgres \
  -e ConnectionStrings__Postgres="Host=pg;Port=5432;Username=tsak;Password=secret;Database=redb" \
  -v tsak_data:/app  \
  ghcr.io/redbase-app/redb-tsak-worker:<version>
```

> SQLite file: mount a volume to persist `redb.db` (it's written in the worker's working dir,
> `/app` for the worker image, `/app/worker` for the stack image).

---

## 4. Worker configuration reference

Every `appsettings.json` key maps to an env var by replacing `:` with `__` (arrays use the
index). All keys below live under `Tsak:` unless noted; defaults are the **shipped image values**.

```
Tsak:Api:Port          ->  Tsak__Api__Port
ConnectionStrings:Postgres   ->  ConnectionStrings__Postgres
Tsak:Redb:License:0    ->  Tsak__Redb__License__0
```

### `Tsak:Storage` — state backend
| Key | Default | Meaning |
|-----|---------|---------|
| `Type` | `Redb` | `Redb` = persistent (uses `Redb:Provider`); `InMemory` = demo, nothing persists, no Pro/cluster. |

### `Tsak:Redb` + `ConnectionStrings` — store & license
| Key | Default | Meaning |
|-----|---------|---------|
| `Redb:Provider` | `sqlite` | redb provider: `sqlite` / `postgres` / `mssql`. |
| `Redb:UsePro` | `true` | Turn on Pro components. Free and unrestricted, no key. |
| `Redb:License` | *(empty)* | Array of license token(s). **Leave empty** — Pro needs no key (§9). |
| `Redb:PropsSaveStrategy` | `ChangeTracking` | Property write mode: `ChangeTracking` (Pro, what the image ships) or `DeleteInsert` (Free). Switching to Free means switching this too — the Free provider throws on a `ChangeTracking` batch, and the cluster bootstrap saves in a batch at startup, so the worker stops on boot. |
| `ConnectionStrings:Sqlite` | `Data Source=redb.db` | SQLite file (Provider=sqlite). Persist via a volume. |
| `ConnectionStrings:Postgres` | — | Npgsql connection string (Provider=postgres). |
| `ConnectionStrings:MSSql` | — | SQL Server connection string (Provider=mssql). |

### `Tsak:Cluster` — redb-backed clustering (see §6)
| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | `true` | Join/form a cluster. `false` = standalone single node. |
| `ClusterName` / `GroupName` | `default` | Nodes sharing both names + the same store form one cluster. |
| `NodeId` | `""` | Empty = self-assign a unique id. Set a stable id for a fixed node. |
| `ApiEndpoint` | `http://localhost:9090` | How peers reach this node's management API. |
| `HeartbeatIntervalSeconds` | `15` | Heartbeat write cadence. |
| `DeadNodeTimeoutSeconds` | `60` | No heartbeat for this long ⇒ node is dead. |
| `LeaderLockTtlSeconds` | `30` | Leader lease TTL (re-election after expiry). |
| `Strategy` | `round-robin` | Work distribution across nodes. |

### `Tsak:Api` — management / REST API
| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | `true` | Serve the management + route-dispatch API. |
| `Host` / `Port` | `127.0.0.1` / `9090` in code, **`0.0.0.0` in the image** | Bind address/port. Loopback is the code default, so a worker you run yourself is not exposed until you say so; the image binds every interface because a container must, and relies on you publishing the port deliberately (§2). |
| `Echo:Path` | `/api/echo` | Auth-exempt echo probe path (route ships `AutoStart=false`). |

### `Tsak:Auth` — management-API authentication
| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | `false` in code, **`false` in the image** | Require an API key on management endpoints (metrics/echo stay exempt). The image ships it off on purpose — see §2. Turn it on together with `Secret`: with authentication on and no secret the worker refuses to start. |
| `Secret` | *(stripped in image)* | HMAC secret of the API keys — the key that `KeyHash` is computed with. **Set in prod.** |
| `Keys[]` | `svc-web-dashboard` | Service keys: `Id`, `KeyHash`, `Name`, `Roles`, `UserId`. The Web dashboard presents `Tsak:Web:ServiceApiKey`; its hash must match a key here. See below — this is where the **first** key comes from. |

#### The first API key

`tsak auth create` talks to the worker's API and therefore needs a key already: the first one can only come
from `Tsak:Auth:Keys`. Pick any random string as the key, store its hash here, and hand the key itself to
whoever calls the API (`Tsak:Web:ServiceApiKey` for the dashboard).

`KeyHash` is **HMAC-SHA256 of the key, keyed with `Tsak:Auth:Secret`**, as 64 hexadecimal characters:

```bash
printf %s "$KEY" | openssl dgst -sha256 -hmac "$TSAK_AUTH_SECRET" | awk '{print $NF}'
```

```powershell
[Convert]::ToHexString([Security.Cryptography.HMACSHA256]::new(
  [Text.Encoding]::UTF8.GetBytes($secret)).ComputeHash([Text.Encoding]::UTF8.GetBytes($key)))
```

Case does not matter — the worker normalises the hash — but a value that is not 64 hexadecimal characters is
refused with an error naming the key, instead of being seeded into a key that answers 401 forever. `Roles`
takes either a string (`"admin,ops"`) or an array (`Roles:0`, `Roles:1` — the only shape environment variables
can express: `Tsak__Auth__Keys__0__Roles__0=admin`).

### `Tsak:Modules` — module loading (see §7)
| Key | Default | Meaning |
|-----|---------|---------|
| `AssemblyPaths` | `[ "modules" ]` | Dirs scanned for `.tpkg` (and their `context.json`). Mount your modules here, **never into `Libs/`**. |

> Per-context / per-module settings (the 5-layer merge: `Tsak:Contexts`, `context.json`,
> `{Module}.config.json`, `Override`) are documented in **[CONFIG_GUIDE.md](CONFIG_GUIDE.md)**.
> Those module config files now live next to the `.tpkg` in the **`modules/`** dir.

### `Tsak:HotReload` — live module deploy
| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | `true` | Watch module dirs and reload on change. |
| `ScanIntervalSeconds` | `10` | Scan cadence. |
| `KeepVersions` | `2` | Old versions retained for rollback. |
| `StartupTimeoutSeconds` | `60` | Max wait for a module to start. |
| `RollingUpdate` | `false` | Reserved, not implemented: nodes reload independently. |
| `Collectible` | `false` | Unload replaced module versions. Off: they stay in memory until restart. |
| `MaxOrphanedContexts` | `KeepVersions + 3` | Left-over module versions in memory before the `hotreload` health check turns Degraded and the deploy advice says `cold-deploy` (see "After a series of hot swaps"). |
| `MaxWorkingSetMb` | none | Working set (MB) above which the deploy advice says `cold-deploy`. |
| `RemovalDebounceScans` | `2` | Scans a file must stay gone before unload (avoids false positives mid-replace). |

### `Tsak:Watchdog` — stuck-route detection
| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | `true` | Monitor route liveness. |
| `CheckIntervalSeconds` | `10` | Check cadence. |
| `SuspectedThresholdMinutes` / `HungThresholdMinutes` | `0.5` / `1.5` | Suspect → hung escalation. |
| `AutoRestartHungRoutes` | `false` | Auto-restart hung routes (off = alert only). |

### `Tsak:Metrics` / `Tsak:Tracing` — observability
| Key | Default | Meaning |
|-----|---------|---------|
| `Metrics:CollectionIntervalSeconds` | `10` | Sampling cadence. |
| `Metrics:HistoryHours` | `12` | In-memory metric retention. |
| `Metrics:Prometheus:Enabled` | `true` | Expose `/metrics` on the API port (auth-exempt). |
| `Metrics:Prometheus:Port` | `9464` | Internal OTel listener (loopback). |
| `Tracing:ServiceName` | `redb-tsak-worker` | OTLP service.name. |
| `Tracing:Otlp:Enabled` | `true` | Export route/step spans via OTLP. |
| `Tracing:Otlp:Endpoint` / `Protocol` | `http://localhost:4317` / `grpc` | Collector/Jaeger endpoint. |

These feed any OTLP collector (Jaeger/Tempo) and a Prometheus scrape of `<host>:<Api:Port>/metrics`.

### Other
| Section | Key | Default | Meaning |
|---------|-----|---------|---------|
| `Tsak:Logs` | `BufferSize` | `2000` | In-memory log ring buffer (dashboard tail). |
| `Tsak:Contexts:default` | `AutoStart` | `true` | Auto-start contexts on boot. |
| `Quartz` | `quartz.jobStore.*`, `threadCount` | AdoJobStore, `10` | The scheduler of every context; the pool is per context (§6). Quartz clustering stays `false` on SQLite. |
| `Serilog` | `MinimumLevel`, `WriteTo` | Console + `Logs/log-.txt` (7-day roll) | Logging sinks/levels. |
| `ASPNETCORE_PATHBASE` | — | — | Host under a sub-path, e.g. `/tsak` (see §5). |

---

## 5. Dashboard & passwords

The dashboard (Web / Stack) is a **Blazor Server** app on port `8085`.

| Secret | Key | Default in image | Change in prod? |
|--------|-----|------------------|-----------------|
| Dashboard login | `Tsak:Web:AdminLogin` / `Tsak:Web:AdminPassword` | `admin` / `admin` | **Yes** |
| Management-API auth secret | `Tsak:Auth:Secret` | *(stripped — empty)* | set if `Auth:Enabled=true` |
| Web→worker service key | `Tsak:Web:ServiceApiKey` | *(stripped)* | set when Web and Worker are separate |
| Pro license | `Tsak:Redb:License:0` | *(empty — none needed)* | no: Pro is free (§9) |

```bash
docker run -p 8085:8085 \
  -e Tsak__Web__AdminLogin=admin \
  -e Tsak__Web__AdminPassword='CHANGE_ME' \
  ghcr.io/redbase-app/redb-tsak-stack:<version>
```

### Dashboard config (`Tsak:Web` + `Kestrel`)

| Key | Default | Meaning |
|-----|---------|---------|
| `Tsak:Web:AdminLogin` / `AdminPassword` | `admin` / `admin` | Dashboard login. **Change in prod.** |
| `Tsak:Web:Mode` | `standalone` | How the dashboard finds workers: `standalone` (fixed list) or `cluster` (auto-discover via the redb store). |
| `Tsak:Web:Nodes[]` | — | **standalone mode:** explicit worker list — each `{ Id, Url, ApiKey }`. `ApiKey` must match a worker `Tsak:Auth:Keys` entry. |
| `Tsak:Web:ServiceApiKey` | *(stripped)* | **cluster mode:** the key the dashboard uses to call workers' management API (worker validates its SHA-256 against `Tsak:Auth:Keys`). |
| `Kestrel:Endpoints:Http:Url` | `:8085` (in image) | Dashboard bind URL (the image already binds `:8085`). |
| `Kestrel:Endpoints:Https:Url` | — | Optional HTTPS bind (provide a cert). |
| `ASPNETCORE_PATHBASE` | — | Serve under a sub-path, e.g. `/tsak` (see below). |

Standalone vs cluster — pick how the dashboard reaches workers:

```bash
# Standalone: dashboard talks to ONE named worker.
-e Tsak__Web__Mode=standalone
-e Tsak__Web__Nodes__0__Id=local
-e Tsak__Web__Nodes__0__Url=http://worker:9090
-e Tsak__Web__Nodes__0__ApiKey=<key matching worker Tsak:Auth:Keys>

# Cluster: dashboard auto-discovers nodes from the shared redb store.
-e Tsak__Web__Mode=cluster
-e Tsak__Web__ServiceApiKey=<service key>
```

> In the **stack** image the dashboard and worker share one container — it's wired to the local
> worker out of the box; you only set `AdminLogin`/`AdminPassword`.

### Behind a reverse proxy (nginx / traefik) — read this

The dashboard needs its **Blazor SignalR WebSocket** (`<pathbase>/_blazor`) proxied with the
upgrade headers. If the proxy doesn't upgrade WebSockets, the page renders and the login form
shows, **but the login button does nothing** (the Blazor circuit never connects).

Hosting under a sub-path (e.g. `https://host/tsak/`)? Set `ASPNETCORE_PATHBASE=/tsak` on the
container and proxy that path:

```nginx
location /tsak/ {
    proxy_pass http://tsak-container:8085;
    proxy_http_version 1.1;
    proxy_set_header Upgrade    $http_upgrade;   # <-- required for Blazor
    proxy_set_header Connection "upgrade";        # <-- required for Blazor
    proxy_set_header Host       $host;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

Without `Upgrade`/`Connection` headers the login button is dead — it is **not** a credentials
problem. Access the dashboard at the path matching `ASPNETCORE_PATHBASE` (e.g. `/tsak/`), not `/`.

---

## 6. Clustering — one node out of the box

The Tsak cluster is **redb-backed** (leader election + heartbeats live in the redb store; this
is *not* Quartz clustering, which stays off on SQLite). The shipped config already runs it:
`Tsak:Cluster:Enabled=true`, and clustering needs no license — the `tsak.cluster` feature is free (§9),
with no node cap.

So **one worker = a working one-node cluster** (self-elected leader). To scale, run more nodes
pointing at the **same redb store** (use Postgres/MSSql, §3) with the same
`ClusterName`/`GroupName` and an empty `NodeId` (each self-assigns); they join automatically, as
many as you need. Run standalone with `Tsak__Cluster__Enabled=false`.

### Quartz: a scheduler per context

Each context runs its own Quartz scheduler, named `{instanceName}-{context}` (`TsakScheduler-{ClusterName}-{context}`
with the shipped config), as Apache Camel runs one per CamelContext. On a clustered `AdoJobStore` the nodes that
run a context form that context's Quartz cluster: a job fires on one of them. Two consequences:

- Don't put `.Cluster(true)` on a `cron:` / `qtimer:` route: Quartz already fires it once per cluster, and Tsak
  leaves such a route on every node (with a Warning) rather than lose the fires other nodes take.
- The `Quartz` section configures every context's scheduler; `threadCount` / `maxConcurrency` is the pool of each
  context, not of the node.

### Upgrading to the per-context scheduler

Releases before the per-context scheduler ran one scheduler per node, named `TsakScheduler-{ClusterName}`. A node
of the new release does not read that scheduler's rows, and a node of the old release does not know the new names:
the two cannot share a cluster. **Upgrade with a full stop** — there is no compatibility mode:

1. Stop every worker of the cluster.
2. Deploy the new release on all of them.
3. Start them. Each context schedules its jobs again from its routes under its own scheduler name.

The old scheduler's rows stay in the database: a node never deletes or pauses what a shared store holds. Removing
them is an operator command, run once with every worker stopped (child tables first). PostgreSQL, with the
shipped table prefix:

```sql
BEGIN;
DELETE FROM qrtz_fired_triggers      WHERE sched_name = 'TsakScheduler-<ClusterName>';
DELETE FROM qrtz_simple_triggers     WHERE sched_name = 'TsakScheduler-<ClusterName>';
DELETE FROM qrtz_simprop_triggers    WHERE sched_name = 'TsakScheduler-<ClusterName>';
DELETE FROM qrtz_cron_triggers       WHERE sched_name = 'TsakScheduler-<ClusterName>';
DELETE FROM qrtz_blob_triggers       WHERE sched_name = 'TsakScheduler-<ClusterName>';
DELETE FROM qrtz_triggers            WHERE sched_name = 'TsakScheduler-<ClusterName>';
DELETE FROM qrtz_job_details         WHERE sched_name = 'TsakScheduler-<ClusterName>';
DELETE FROM qrtz_calendars           WHERE sched_name = 'TsakScheduler-<ClusterName>';
DELETE FROM qrtz_paused_trigger_grps WHERE sched_name = 'TsakScheduler-<ClusterName>';
DELETE FROM qrtz_scheduler_state     WHERE sched_name = 'TsakScheduler-<ClusterName>';
DELETE FROM qrtz_locks               WHERE sched_name = 'TsakScheduler-<ClusterName>';
COMMIT;
```

The same statements apply to SQL Server (`BEGIN TRANSACTION; … COMMIT;`). If `quartz.scheduler.instanceName` was
configured to a name other than `TsakScheduler`, the old scheduler had exactly that name, and each context's
scheduler is now `<that name>-<context>`.

---

## 7. Deploying your own modules (`.tpkg`)

Modules are hot-deployable `.tpkg` packages. The worker scans **`Tsak:Modules:AssemblyPaths`**
(default `[ "modules" ]`) for them — a **dedicated `modules/` dir, separate from `Libs/shared`**.

```
/app[/worker]/
├── Libs/shared/     ← image's connector DLLs. DO NOT mount over this.
└── modules/         ← YOUR bind-mount: drop .tpkg + its context.json here.
```

> **Never bind-mount `Libs/`** — it hides the image's `Libs/shared` connector layer. Mount the
> dedicated `modules/` dir instead. A module's external config goes in the **same** dir as a
> `context.json` (TsakCoordinator reads it next to the `.tpkg`).

```yaml
services:
  worker:
    image: ghcr.io/redbase-app/redb-tsak-stack:<version>
    environment:
      - Tsak__Modules__AssemblyPaths__0=/app/worker/modules
    volumes:
      - ./modules:/app/worker/modules:ro   # contains MyModule.tpkg (+ context.json)
```

Or hot-deploy at runtime (hot-reload watches the dir): copy a new `.tpkg` into `modules/` and the
worker loads it without a restart.

### After a series of hot swaps

Each hot swap leaves the old module version in memory with `Tsak:HotReload:Collectible=false` (the default), and with
`true` a version that something still holds stays too. Once more of them pile up than
`Tsak:HotReload:MaxOrphanedContexts` allows, the `hotreload` health check turns Degraded with "restart the node to
reclaim memory". A restart is the only way to give that memory back.

What to watch: `OrphanedModuleContexts` in `GET /api/system/info` (or `tsak system info`), the `hotreload` entry of
`GET /api/system/health`, and the working set of the process. With `Collectible=true` a version that did not unload is
also logged as a warning: `module context <name> still alive after 10 collections`.

Restart the nodes one at a time, so the cluster keeps serving:

1. Drain the node. In a cluster, cordon it (`POST /api/cluster/nodes/{nodeId}/cordon`): it finishes its work, takes no
   new work and hands its locks to the peers. A single node has no peers to take over; restart it in a quiet window.
2. Stop the process (SIGTERM, `docker stop`): every context stops with its own graceful-stop timeout for in-flight
   exchanges.
3. Start it again and wait for `GET /api/health/ready` to answer `200`. The node loads the current module versions
   only, so `OrphanedModuleContexts` is `0` again.
4. Uncordon it, then go on with the next node.

If the count grows again after every release, the modules are swapped faster than nodes restart: let the pipeline ask
for the deploy advice below before each release, or try `Collectible=true` on a test node and watch whether the old
versions go. When a release is due anyway, do not restart first and hot swap after: deploy it cold (next section), the
restart and the new packages in one step.

### Deploy advice in a pipeline

`GET /api/health/deploy` tells a pipeline how to deploy a new package on a node; `GET /api/health/deploy/cluster`
asks every alive node in one call. Both are open like the probes, and the status code is the whole answer
(API_GUIDE.md, "Deploy advice"):

- `200`, `hot-swap`: copy the `.tpkg` into `modules/` of the running node.
- `409`, `cold-deploy`: cordon the node, stop it, replace the packages in `modules/` while it is down, start it, wait
  for `/api/health/ready`, uncordon. A restart with the old packages and a hot swap after it would leave one more old
  version in memory for nothing.

The endpoint only advises: the pipeline does the deploy. `tsak system deploy-advice [--cluster]` asks the same from a
script and exits `0` for hot-swap, `3` for cold-deploy, `1` or `2` on an error, so a step can branch on the exit code;
`--output json` prints the advice and nothing else.

#### Kubernetes

The packages reach the pod either through a writable volume mounted at `modules/` (hot swap possible) or inside the
image or a volume filled at pod start (every deploy is a rollout). With a writable volume:

1. Ask: `curl -s -o advice.json -w '%{http_code}' http://tsak:9090/api/health/deploy/cluster` from a Job or a pipeline
   step inside the cluster (or `kubectl exec` into one pod). `200` → step 2, `409` → step 3.
2. Hot swap: copy the package into each pod's `modules/`, through a temporary name and a rename so the worker never
   reads a half-written file (`kubectl cp app.tpkg tsak-0:/app/modules/app.tpkg.part`, then
   `kubectl exec tsak-0 -- mv /app/modules/app.tpkg.part /app/modules/app.tpkg`). Check the new version with
   `tsak module list`.
3. Cold deploy, one pod at a time (`maxUnavailable: 1`):
   1. Put the new package where the pods take it from (a new image tag, a new ConfigMap or volume version).
   2. Cordon the node: `POST /api/cluster/nodes/{nodeId}/cordon` with an admin key from a Secret. A `preStop` hook
      that sends the same call cordons each pod the rollout stops, without a step of its own. Give the pod a
      `terminationGracePeriodSeconds` above the contexts' graceful-stop timeout.
   3. `kubectl rollout restart statefulset/tsak` (or `kubectl set image`): Kubernetes stops the pod, the new one starts
      with the new packages, and the readiness probe `/api/health/ready` holds the rollout until it serves.
   4. Uncordon the node once it is ready: `POST /api/cluster/nodes/{nodeId}/uncordon`. With a StatefulSet the node id
      stays the same across the restart, so the registry still has it cordoned until this call.

#### Compose with TeamCity

One worker per host, `modules/` a bind mount on the host. A build configuration with these steps, run host by host:

1. Ask (command line step):

   ```sh
   tsak system deploy-advice --url http://$HOST:9090 --output json > advice.json
   code=$?
   if [ $code -eq 0 ]; then echo "##teamcity[setParameter name='deploy.mode' value='hot']"
   elif [ $code -eq 3 ]; then echo "##teamcity[setParameter name='deploy.mode' value='cold']"
   else exit $code; fi
   ```

2. Hot swap (runs when `deploy.mode` is `hot`): copy under a temporary name and rename, so the worker never reads a
   half-written file:

   ```sh
   scp app.tpkg deploy@$HOST:/srv/tsak/modules/app.tpkg.part
   ssh deploy@$HOST 'mv /srv/tsak/modules/app.tpkg.part /srv/tsak/modules/app.tpkg'
   ```

3. Cold deploy (runs when `deploy.mode` is `cold`):

   ```sh
   curl -fsS -X POST -H "X-Api-Key: $TSAK_ADMIN_KEY" http://$HOST:9090/api/cluster/nodes/$NODE_ID/cordon   # cluster only
   ssh deploy@$HOST 'cd /srv/tsak && docker compose stop tsak'
   scp app.tpkg deploy@$HOST:/srv/tsak/modules/app.tpkg
   ssh deploy@$HOST 'cd /srv/tsak && docker compose start tsak'
   for i in $(seq 60); do   # up to 5 minutes
     [ "$(curl -s -o /dev/null -w '%{http_code}' http://$HOST:9090/api/health/ready)" = 200 ] && break
     [ $i -eq 60 ] && { echo "worker not ready"; exit 1; }
     sleep 5
   done
   curl -fsS -X POST -H "X-Api-Key: $TSAK_ADMIN_KEY" http://$HOST:9090/api/cluster/nodes/$NODE_ID/uncordon # cluster only
   ```

   A single node without a cluster has nobody to take its work while it is down: run the cold deploy in a quiet
   window, and leave out the two cordon calls.

---

## 8. Compose examples

**Stack (worker + dashboard), embedded SQLite, persistent:**

```yaml
services:
  tsak:
    image: ghcr.io/redbase-app/redb-tsak-stack:<version>
    ports:
      - "9090:9090"      # management / REST API
      - "8085:8085"      # dashboard
    environment:
      - Tsak__Web__AdminPassword=CHANGE_ME
    volumes:
      - tsak_data:/app/worker            # persists redb.db (SQLite)
      - ./modules:/app/worker/modules:ro # your .tpkg modules (optional, §7)
    restart: unless-stopped
volumes:
  tsak_data:
```

**Worker + Postgres (multi-node-ready store):**

```yaml
services:
  worker:
    image: ghcr.io/redbase-app/redb-tsak-worker:<version>
    ports: ["9090:9090"]
    environment:
      - Tsak__Redb__Provider=postgres
      - ConnectionStrings__Postgres=Host=pg;Username=tsak;Password=secret;Database=redb
      - Tsak__Redb__License__0=<your-jwt>   # §9
    depends_on: [pg]
    restart: unless-stopped
  pg:
    image: postgres:16
    environment: [POSTGRES_USER=tsak, POSTGRES_PASSWORD=secret, POSTGRES_DB=redb]
    volumes: ["pg_data:/var/lib/postgresql/data"]
volumes:
  pg_data:
```

```bash
docker compose up -d
```

---

## 9. Licensing — nothing to activate

**There is no key to buy, request or install.** Pro is proprietary but runs **free and unrestricted**:
`redb.*.Pro`, `redb.Tsak.Core.Pro`, the dashboard and
clustering, in production, with no node cap and no request limit. The images ship with
`Tsak:Redb:License` **empty**, and that is the correct state — leave it empty.

Pro packages are proprietary (closed source) but free of charge. **Larger companies that need the
Pro sources — for audit, escrow, or to build in-house — can request them; we hand them over.**
Write to <https://redbase.app/pro>.

> If you inherited a config carrying an old trial JWT, delete it. It is not needed, and
> shipping a JWT in an image is exactly what the release pipeline blocks.

---

## 10. Verifying signatures

All images and release archives are [cosign](https://docs.sigstore.dev/cosign/)-signed. The
cosign **public key** (`cosign.pub`) is attached to each [GitHub Release](https://github.com/redbase-app/redb-tsak/releases).

```bash
# grab cosign.pub from the release assets, then:
cosign verify --key cosign.pub ghcr.io/redbase-app/redb-tsak-worker:<version>
```

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Dashboard login button does nothing | Blazor WebSocket not proxied | §5 — add `Upgrade`/`Connection` headers; use the `ASPNETCORE_PATHBASE` path |
| `failed to ensure schema` / Npgsql connection refused | worker pointed at a Postgres that isn't reachable | check `ConnectionStrings`, or use SQLite (§3) |
| Connectors missing after mounting a volume | bind-mounted `Libs/` and hid `Libs/shared` | §7 — mount `modules/`, never `Libs/` |
| `REDB.PRO TRIAL MODE` / 1024 req/day | a build that is not a released redb package (a local or modified build) | use the released packages and images, which run Pro without a key (§9) |
