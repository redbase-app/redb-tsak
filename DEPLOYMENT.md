# redb.Tsak — Deployment Guide

How to run the published redb.Tsak containers: images, configuration, passwords,
storage modes, the dashboard, deploying your own modules, and reverse-proxy setup.

> This guide is for **operators deploying the released images** from `ghcr.io/redbase-app/`.

---

## 1. Images

Published to `ghcr.io/redbase-app/` (public, cosign-signed):

| Image | What it is | Ports |
|-------|------------|-------|
| `redb-tsak-worker:3.2.2` | Process-automation runtime (route contexts, modules, cluster). | `9090` (management/REST API) |
| `redb-tsak-web:3.2.2` | Blazor dashboard (monitoring, routes, logs). Talks to a worker. | `8080` |
| `redb-tsak-stack:3.2.2` | Worker **+** dashboard in one container (supervisord). | `9090`, `8080` |

Tags: `:3.2.2` (= `:3.2.2-net9`), `:latest`. The worker, web and stack images run on .NET 9;
the standalone archives additionally bundle the shared route connectors for net8 / net9 / net10
so user modules (`.tpkg`) can target any of the three.
Pro features (cluster, advanced storage) activate from a **license JWT** in config; without
one the container runs the OSS feature set.

```bash
docker pull ghcr.io/redbase-app/redb-tsak-stack:3.2.2
```

---

## 2. Quick start

```bash
# Stack (worker + dashboard) — boots as a Pro trial on embedded SQLite, one-node cluster.
docker run --rm -p 9090:9090 -p 8080:8080 ghcr.io/redbase-app/redb-tsak-stack:3.2.2
# dashboard:  http://localhost:8080   (login admin / admin)
# REST API:   http://localhost:9090/api/health/live
```

The shipped `appsettings.json` boots with **SQLite + the bundled trial license + a one-node
cluster** (see §6) — zero external dependencies. Override anything via env vars (§4).

---

## 3. Storage modes

`Tsak:Storage:Type` + `Tsak:Redb:Provider` decide where state lives.

| Mode | Config | Use |
|------|--------|-----|
| **SQLite (default)** | `Storage:Type=Redb`, `Redb:Provider=sqlite`, `ConnectionStrings:Sqlite=Data Source=redb.db` | Single box, embedded, Pro trial + one-node cluster out of the box. |
| **Postgres / MSSql** | `Redb:Provider=postgres\|mssql` + `ConnectionStrings:Postgres\|MSSql` | Multi-node cluster, shared store. |
| **InMemory** | `Storage:Type=InMemory`, `Redb:Provider=` (empty) | Stateless demo, no Pro/cluster, nothing persists. |

```bash
# Postgres-backed, persistent:
docker run -p 9090:9090 \
  -e Tsak__Storage__Type=Redb \
  -e Tsak__Redb__Provider=postgres \
  -e ConnectionStrings__Postgres="Host=pg;Port=5432;Username=tsak;Password=secret;Database=redb" \
  -e Tsak__Redb__License__0="<your-license-jwt>" \
  -v tsak_data:/app  \
  ghcr.io/redbase-app/redb-tsak-worker:3.2.2
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
| `Redb:UsePro` | `true` | Turn on Pro components (still gated by the license). |
| `Redb:License` | *(trial JWT)* | Array of license token(s). Replace with your own for prod (§9). |
| `Redb:PropsSaveStrategy` | `DeleteInsert` | Property write mode: `DeleteInsert` (Free) or `ChangeTracking` (Pro). |
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
| `Host` / `Port` | `0.0.0.0` / `9090` | Bind address/port. |
| `Echo:Path` | `/api/echo` | Auth-exempt echo probe path (route ships `AutoStart=false`). |

### `Tsak:Auth` — management-API authentication
| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | `true` | Require an API key on management endpoints (metrics/echo stay exempt). |
| `Secret` | *(stripped in image)* | HMAC secret for issued tokens. **Set in prod.** |
| `Keys[]` | `svc-web-dashboard` | Service keys: `Id`, `KeyHash` (SHA-256 of the key), `Name`, `Roles`, `UserId`. The Web dashboard presents `Tsak:Web:ServiceApiKey`; its hash must match a key here. |

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
| `RollingUpdate` | `true` | Reload without dropping in-flight work. |
| `Collectible` | `false` | Unloadable ALCs (off = more compatible). |
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
| `Quartz` | `quartz.jobStore.*`, `threadCount` | AdoJobStore, `10` | Scheduler (Quartz clustering stays `false` on SQLite). |
| `Serilog` | `MinimumLevel`, `WriteTo` | Console + `Logs/log-.txt` (7-day roll) | Logging sinks/levels. |
| `ASPNETCORE_PATHBASE` | — | — | Host under a sub-path, e.g. `/tsak` (see §5). |

---

## 5. Dashboard & passwords

The dashboard (Web / Stack) is a **Blazor Server** app on port `8080`.

| Secret | Key | Default in image | Change in prod? |
|--------|-----|------------------|-----------------|
| Dashboard login | `Tsak:Web:AdminLogin` / `Tsak:Web:AdminPassword` | `admin` / `admin` | **Yes** |
| Management-API auth secret | `Tsak:Auth:Secret` | *(stripped — empty)* | set if `Auth:Enabled=true` |
| Web→worker service key | `Tsak:Web:ServiceApiKey` | *(stripped)* | set when Web and Worker are separate |
| Pro license | `Tsak:Redb:License:0` | bundled trial | replace with your own for prod |

```bash
docker run -p 8080:8080 \
  -e Tsak__Web__AdminLogin=admin \
  -e Tsak__Web__AdminPassword='CHANGE_ME' \
  ghcr.io/redbase-app/redb-tsak-stack:3.2.2
```

### Dashboard config (`Tsak:Web` + `Kestrel`)

| Key | Default | Meaning |
|-----|---------|---------|
| `Tsak:Web:AdminLogin` / `AdminPassword` | `admin` / `admin` | Dashboard login. **Change in prod.** |
| `Tsak:Web:Mode` | `standalone` | How the dashboard finds workers: `standalone` (fixed list) or `cluster` (auto-discover via the redb store). |
| `Tsak:Web:Nodes[]` | — | **standalone mode:** explicit worker list — each `{ Id, Url, ApiKey }`. `ApiKey` must match a worker `Tsak:Auth:Keys` entry. |
| `Tsak:Web:ServiceApiKey` | *(stripped)* | **cluster mode:** the key the dashboard uses to call workers' management API (worker validates its SHA-256 against `Tsak:Auth:Keys`). |
| `Kestrel:Endpoints:Http:Url` | `:8080` (in image) | Dashboard bind URL (the image already binds `:8080`). |
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

### ⚠️ Behind a reverse proxy (nginx / traefik) — read this

The dashboard needs its **Blazor SignalR WebSocket** (`<pathbase>/_blazor`) proxied with the
upgrade headers. If the proxy doesn't upgrade WebSockets, the page renders and the login form
shows, **but the login button does nothing** (the Blazor circuit never connects).

Hosting under a sub-path (e.g. `https://host/tsak/`)? Set `ASPNETCORE_PATHBASE=/tsak` on the
container and proxy that path:

```nginx
location /tsak/ {
    proxy_pass http://tsak-container:8080;
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
`Tsak:Cluster:Enabled=true` + the bundled trial license (feature `tsak.cluster`, `max_nodes: 3`).

So **one worker = a working one-node cluster** (self-elected leader). To scale, run more nodes
pointing at the **same redb store** (use Postgres/MSSql, §3) with the same
`ClusterName`/`GroupName` and an empty `NodeId` (each self-assigns); they join automatically up
to the license `max_nodes`. Run standalone with `Tsak__Cluster__Enabled=false`.

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
    image: ghcr.io/redbase-app/redb-tsak-stack:3.2.2
    environment:
      - Tsak__Modules__AssemblyPaths__0=/app/worker/modules
    volumes:
      - ./modules:/app/worker/modules:ro   # contains MyModule.tpkg (+ context.json)
```

Or hot-deploy at runtime (hot-reload watches the dir): copy a new `.tpkg` into `modules/` and the
worker loads it without a restart.

---

## 8. Compose examples

**Stack (worker + dashboard), embedded SQLite, persistent:**

```yaml
services:
  tsak:
    image: ghcr.io/redbase-app/redb-tsak-stack:3.2.2
    ports:
      - "9090:9090"      # management / REST API
      - "8080:8080"      # dashboard
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
    image: ghcr.io/redbase-app/redb-tsak-worker:3.2.2
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

## 9. License activation

```bash
docker run -e Tsak__Redb__License__0="eyJhbGci...your-jwt..." ghcr.io/redbase-app/redb-tsak-worker:3.2.2
```

The images ship a **time-limited trial** key so Pro/cluster work out of the box for evaluation.
For production, request a key at <https://redbase.app/pro> and set `Tsak:Redb:License`.

---

## 10. Verifying signatures

All images and release archives are [cosign](https://docs.sigstore.dev/cosign/)-signed. The
cosign **public key** (`cosign.pub`) is attached to each [GitHub Release](https://github.com/redbase-app/redb-tsak/releases).

```bash
# grab cosign.pub from the release assets, then:
cosign verify --key cosign.pub ghcr.io/redbase-app/redb-tsak-worker:3.2.2
```

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Dashboard login button does nothing | Blazor WebSocket not proxied | §5 — add `Upgrade`/`Connection` headers; use the `ASPNETCORE_PATHBASE` path |
| `failed to ensure schema` / Npgsql connection refused | worker pointed at a Postgres that isn't reachable | check `ConnectionStrings`, or use SQLite (§3) |
| Connectors missing after mounting a volume | bind-mounted `Libs/` and hid `Libs/shared` | §7 — mount `modules/`, never `Libs/` |
| `REDB.PRO TRIAL MODE` / 1024 req/day | running on the bundled trial | replace with your own license (§9) |
