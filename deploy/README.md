# redb.Tsak — deployment & observability assets

Ready-to-use manifests and configs for running and monitoring the Tsak Worker.

```
deploy/
├── k8s/                         Kubernetes manifests
│   ├── deployment.yaml          Deployment (correct /api/health/* probes, metrics port, OTLP env)
│   ├── service.yaml             Service (REST + /metrics on 9090)
│   └── servicemonitor.yaml      Prometheus Operator ServiceMonitor (alternative to scrape annotations)
├── grafana/
│   └── redb-tsak-dashboard.json Importable dashboard (routes + .NET runtime)
└── observability/               Local Prometheus + Grafana + Jaeger stack
    ├── docker-compose.yml
    ├── prometheus.yml
    └── grafana/provisioning/…   Auto-provisioned datasource + dashboard
```

## Clustering — one node out of the box

The Tsak cluster is **redb-backed**: leader election, heartbeats and round-robin route
distribution live in the redb store (this is **not** Quartz clustering, which stays off on
SQLite). The shipped `appsettings.json` is already wired for it, so there is nothing to turn on:

- `Tsak:Redb:UsePro: true` + a bundled **1-year trial license** whose features include
  `tsak.cluster` (`max_nodes: 3`);
- `Tsak:Cluster:Enabled: true`, on an embedded **SQLite** store (zero external dependencies).

So a **single Worker process is already a working one-node cluster** — start it and it elects
itself leader and begins distributing routes. Add more nodes (up to the license `max_nodes`)
pointing at the **same redb database**, same `ClusterName`/`GroupName`, each with an empty
`NodeId` (self-assigned), and they join automatically.

```jsonc
"Tsak": {
  "Redb":    { "Provider": "sqlite", "UsePro": true, "License": [ "<trial JWT — bundled>" ] },
  "Cluster": { "Enabled": true, "ClusterName": "default", "GroupName": "default", "Strategy": "round-robin" }
}
```

Run **standalone** (no cluster) by flipping one flag:

```jsonc
"Tsak": { "Cluster": { "Enabled": false } }
```

> The bundled trial is a real, time-limited Pro key — fine for evaluation and single-box
> clusters. For production or more than `max_nodes`, drop your own license into
> `Tsak:Redb:License` and point the cluster at a shared Postgres/MSSql redb store.

## What Tsak exposes

| Signal   | How                                   | Toggle                              | Port |
|----------|---------------------------------------|-------------------------------------|------|
| Metrics  | Prometheus `/metrics` on the facade   | `Tsak:Metrics:Prometheus:Enabled`   | 9090 |
| Traces   | OTel → OTLP exporter (Jaeger/collector)| `Tsak:Tracing:Otlp:Enabled`         | →4317|
| Health   | K8s probes (auth-exempt)              | always on                           | 9090 |

Both observability exporters are **off by default**. Enable via config or env-var
(`Tsak__Metrics__Prometheus__Enabled=true`, `Tsak__Tracing__Otlp__Enabled=true`).

> **Metrics are served through the facade** at `http://<host>:9090/metrics` (auth-exempt) — the
> same Kestrel port as the API. The OTel scrape listener binds **loopback** (`localhost:9464`)
> internally and is proxied out by the `/metrics` route, so there is **no separate exposed port
> and no Windows URL ACL** to set up. `Tsak:Metrics:Prometheus:Port` only changes the internal
> loopback port.

### Health probe paths (auth-exempt)

- `GET /api/health/startup` — startup probe
- `GET /api/health/live` — liveness probe
- `GET /api/health/ready` — readiness probe

> These live under `/api/health/*` (the `Tsak:Api:AuthExempt` prefix). The rich aggregate
> `GET /api/system/health` is a separate, **auth-gated** endpoint for CLI/Web — do **not**
> point K8s probes at it.

## Prometheus metric names

The OTel Prometheus exporter sanitises names and appends unit + `_total`. The exact series
exported by `redb.Route` (verified against `OpenTelemetry.Exporter.Prometheus.HttpListener`
1.15):

| Instrument (OTel)                  | Type      | Prometheus series                                  |
|------------------------------------|-----------|----------------------------------------------------|
| `redb.route.exchanges.processed`   | counter   | `redb_route_exchanges_processed_exchanges_total`   |
| `redb.route.exchanges.failed`      | counter   | `redb_route_exchanges_failed_exchanges_total`      |
| `redb.route.exchange.duration` (ms)| histogram | `redb_route_exchange_duration_milliseconds_{bucket,sum,count}` |
| `redb.route.exchanges.inflight`    | gauge     | `redb_route_exchanges_inflight_exchanges`          |

Labels: `redb_route_id`, `redb_route_endpoint`, `redb_route_scheme`, plus `otel_scope_name`.
Runtime/process series use the `dotnet_*` and `process_*` prefixes.

## Local stack (Docker)

```bash
cd deploy/observability
docker compose up -d
```

- Grafana   → http://localhost:3000 (anonymous admin; dashboard auto-loaded)
- Prometheus → http://localhost:9091  (host 9091 → container 9090; avoids Tsak's API port 9090)
- Jaeger UI → http://localhost:16686

Then run the Worker on the host with both exporters on:

```bash
Tsak__Metrics__Prometheus__Enabled=true Tsak__Tracing__Otlp__Enabled=true dotnet run --project src/redb.Tsak.Worker
```

Prometheus scrapes the facade's `:9090/metrics`; the Worker pushes traces to Jaeger's OTLP `:4317`.

## Kubernetes

```bash
kubectl apply -n redb -f deploy/k8s/deployment.yaml -f deploy/k8s/service.yaml
# With the Prometheus Operator (kube-prometheus-stack) instead of scrape annotations:
kubectl apply -n redb -f deploy/k8s/servicemonitor.yaml
```

Edit the image, `terminationGracePeriodSeconds` / `Tsak__Shutdown__TimeoutSeconds`, the Redb
connection (use a Secret), and the OTLP `Endpoint` (point at your in-cluster Jaeger collector)
before applying.

## Jaeger / OTLP notes

Jaeger ingests OTLP natively — no Jaeger-specific exporter needed. Set:

```jsonc
"Tsak": { "Tracing": { "Otlp": {
  "Enabled": true,
  "Endpoint": "http://localhost:4317",   // gRPC; use :4318 with "Protocol": "http/protobuf"
  "Protocol": "grpc"
} } }
```

Module-level `ActivitySource`s (e.g. `RedbIdentity`) are picked up when listed under
`Tsak:Metrics:Prometheus:AdditionalSources` — they then flow to the same OTLP exporter.
