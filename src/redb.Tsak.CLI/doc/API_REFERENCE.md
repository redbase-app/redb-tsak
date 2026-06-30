# API Reference

Complete list of REST API endpoints used by the `tsak` CLI.

All endpoints are served by the Tsak runtime on the configured host/port (default `http://localhost:5100`).

## Authentication

When API key authentication is enabled, include the key in the `X-API-Key` header:

```
X-API-Key: sk-1234567890abcdef
```

The CLI handles this automatically via `--key`, profile, or `TSAK_API_KEY` environment variable.

## Endpoints

### System

| Method | Path | CLI Command | Description |
|--------|------|-------------|-------------|
| `GET` | `/api/system/health` | `tsak system health` | Health check with per-component statuses |
| `GET` | `/api/system/metrics` | `tsak system metrics` | CPU, memory, GC, threading snapshot |
| `GET` | `/api/system/info` | `tsak system info` | Version, uptime, machine info |

### Contexts

| Method | Path | CLI Command | Description |
|--------|------|-------------|-------------|
| `GET` | `/api/contexts` | `tsak context list` | List all route contexts |
| `GET` | `/api/contexts/{name}` | `tsak context get <name>` | Get context details |
| `POST` | `/api/contexts/{name}/start` | `tsak context start <name>` | Start a context |
| `POST` | `/api/contexts/{name}/stop` | `tsak context stop <name>` | Stop a context |
| `POST` | `/api/contexts/{name}/restart` | `tsak context restart <name>` | Restart a context |
| `DELETE` | `/api/contexts/{name}` | `tsak context remove <name>` | Remove a context |

### Modules

| Method | Path | CLI Command | Description |
|--------|------|-------------|-------------|
| `GET` | `/api/modules` | `tsak module list` | List all modules |
| `GET` | `/api/modules/{name}` | `tsak module get <name>` | Get module details |
| `DELETE` | `/api/modules/{name}` | `tsak module remove <name>` | Remove a module |

### Scheduler

| Method | Path | CLI Command | Description |
|--------|------|-------------|-------------|
| `GET` | `/api/scheduler` | `tsak scheduler status` | Scheduler state |
| `GET` | `/api/scheduler/jobs` | `tsak scheduler jobs` | List scheduled jobs |
| `GET` | `/api/scheduler/running` | `tsak scheduler running` | Currently executing jobs |
| `POST` | `/api/scheduler/start` | `tsak scheduler start` | Start scheduler |
| `POST` | `/api/scheduler/standby` | `tsak scheduler standby` | Set standby mode |
| `POST` | `/api/scheduler/jobs/{group}/{name}/pause` | `tsak scheduler pause <group> <name>` | Pause a job |
| `POST` | `/api/scheduler/jobs/{group}/{name}/resume` | `tsak scheduler resume <group> <name>` | Resume a job |

### Cluster

| Method | Path | CLI Command | Description |
|--------|------|-------------|-------------|
| `GET` | `/api/cluster` | `tsak cluster status` | Cluster state and topology |
| `GET` | `/api/cluster/nodes` | `tsak cluster nodes` | List cluster nodes |
| `POST` | `/api/cluster/rebalance` | `tsak cluster rebalance` | Trigger rebalance |

### Logs

| Method | Path | CLI Command | Description |
|--------|------|-------------|-------------|
| `GET` | `/api/logs?limit={n}&level={level}` | `tsak log get` | Get buffered log entries |

### Auth (API Keys)

| Method | Path | CLI Command | Description |
|--------|------|-------------|-------------|
| `GET` | `/api/auth/keys` | `tsak auth list` | List API keys |
| `POST` | `/api/auth/keys` | `tsak auth create` | Create a new API key |
| `DELETE` | `/api/auth/keys/{id}` | `tsak auth revoke <id>` | Revoke an API key |

## Error Responses

All API errors return a JSON body:

```json
{
  "message": "Context 'foo' not found",
  "statusCode": 404
}
```

The CLI maps these to `ApiException` with the HTTP status code as the exit code.
