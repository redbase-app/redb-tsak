# redb.Tsak.CLI — `tsak`

Command-line tool for managing a running redb.Tsak runtime. Connects to any Tsak node over HTTP, exposes every REST API operation as a composable noun-verb command, and is scriptable via JSON output and quiet mode.

## Install

```bash
# Global .NET tool
dotnet tool install -g redb.Tsak.CLI

# Verify
tsak --version
```

## Connect

Connection settings are resolved in priority order:

| Priority | Source | Description |
|---|---|---|
| 1 (highest) | `--url` / `--key` flags | Explicit per-invocation |
| 2 | `TSAK_URL` / `TSAK_API_KEY` env vars | CI/CD pipelines, containers |
| 3 (lowest) | Active profile (`~/.tsak/`) | Developer workstation |

### Connection profiles

```bash
# Add a named profile
tsak profile add --name prod --url https://tsak.prod.example.com --key sk-...

# Add a local dev profile
tsak profile add --name dev --url http://localhost:5100

# Switch active profile
tsak profile use prod

# List profiles (* = active)
tsak profile list

# Remove a profile
tsak profile remove dev
```

Profiles are stored as YAML files under `~/.tsak/profiles/`. The active profile name is written to `~/.tsak/active`.

## Global options

These flags apply to every command:

| Flag | Short | Default | Description |
|---|---|---|---|
| `--profile` | `-p` | active | Connection profile name |
| `--url` | `-u` | — | Tsak runtime URL (overrides profile) |
| `--key` | `-k` | — | API key (overrides profile) |
| `--output` | `-o` | `table` | Output format: `table` \| `json` \| `quiet` |
| `--no-color` | | `false` | Disable ANSI color codes |
| `--yes` | `-y` | `false` | Skip confirmation prompts (for destructive ops) |
| `--watch` | | `false` | Poll the command repeatedly (Ctrl+C to stop) |
| `--interval` | | `5` | Polling interval in seconds (with `--watch`) |
| `--timeout` | | `5` | HTTP request timeout in seconds |

## Command reference

### `system` — Health, metrics, info

```bash
tsak system health                        # Health check (live status)
tsak system health --watch --interval 2  # Poll every 2 s
tsak system metrics                       # Current CPU, memory, GC snapshot
tsak system metrics --watch              # Live metrics dashboard
tsak system info                          # Version, uptime, context/module counts
```

### `context` — Route context lifecycle

A context groups one or more modules into a shared lifecycle and property bag.

```bash
tsak context list                  # All contexts (name, status, endpoint count)
tsak context get <name>            # Detailed view of one context
tsak context start <name>          # Start a stopped context
tsak context stop <name>           # Graceful stop (drains in-flight exchanges)
tsak context restart <name>        # Stop + start
tsak context remove <name>         # Remove context (prompts unless --yes)
```

### `module` — Hot-loadable modules

```bash
tsak module list                   # All loaded modules (name, version, status)
tsak module get <name>             # Module details + dependencies
tsak module remove <name>          # Unload and remove a module (--yes to skip prompt)
```

### `route` — Individual routes within a context

```bash
tsak route list <context>          # Routes in a context
tsak route get <context> <routeId> # Route detail (endpoints, inflight count)
tsak route start <context> <id>    # Start a stopped route
tsak route stop <context> <id> [--timeout N]   # Graceful stop
tsak route force-stop <context> <id>            # Immediate stop (no drain)
tsak route inflight <context> <id>             # In-flight exchanges for a route
tsak route inflight <context>                  # All in-flight exchanges in a context
```

### `watchdog` — Hung-exchange monitoring

```bash
tsak watchdog status               # Watchdog state, last check, alert count
tsak watchdog alerts               # Active watchdog alerts
tsak watchdog enable               # Enable watchdog
tsak watchdog disable              # Disable watchdog
```

### `scheduler` — Quartz job scheduler

```bash
tsak scheduler status              # Scheduler status + job counts
tsak scheduler jobs                # All scheduled jobs (cron, next fire time)
tsak scheduler running             # Currently executing jobs
tsak scheduler start               # Start the scheduler
tsak scheduler standby             # Put scheduler in standby mode
tsak scheduler pause <group.name>  # Pause a specific job
tsak scheduler resume <group.name> # Resume a paused job
```

### `cluster` — Multi-node topology

```bash
tsak cluster status                # Cluster enabled?, node ID, leader flag, epoch
tsak cluster nodes                 # All nodes (hostname, status, heartbeat)
tsak cluster rebalance             # Trigger leader election / rebalance
```

### `log` — In-process log ring buffer

```bash
tsak log get                        # Recent buffered log entries
tsak log get --limit 100            # Last N entries
tsak log get --level Warning        # Filter by minimum level
```

### `auth` — API key management

```bash
tsak auth list                                          # All API keys
tsak auth create --name ci-bot --roles admin,readonly  # New key (prints raw key once)
tsak auth revoke <id>                                   # Revoke by ID
```

### `diagnostics` — Deep inspection

```bash
tsak diagnostics dump               # Full system dump (contexts, inflight, alerts, events)
tsak diagnostics route <ctx> <id>   # Detailed dump for one route
tsak diagnostics lifecycle          # Lifecycle audit event log
tsak diagnostics lifecycle --context orders --route in-0 --limit 50
tsak diagnostics dashboard          # Aggregated snapshot (metrics + context states)
```

## Output formats

| Format | Use case |
|---|---|
| `table` (default) | Human-readable; Spectre.Console-rendered |
| `json` | Scripting, piping to `jq` |
| `quiet` | CI pipelines; only success/error messages |

```bash
# Example: get context list as JSON
tsak context list -o json | jq '.[].name'

# Example: watch metrics without prompts (CI)
tsak system metrics --watch --interval 10 -o json
```

## Scripting example

```bash
#!/usr/bin/env bash
# Restart all contexts on a remote node

export TSAK_URL=http://tsak-prod:5100
export TSAK_API_KEY=sk-prod-...

for ctx in $(tsak context list -o json | jq -r '.[].name'); do
    echo "Restarting $ctx..."
    tsak context restart "$ctx" -y
done
```

## Environment variables

| Variable | Description |
|---|---|
| `TSAK_URL` | Runtime base URL (`http://host:port`) |
| `TSAK_API_KEY` | API key for authentication |

## Architecture

```
tsak (System.CommandLine)
├── Global options parser (ProfileManager → TsakApiClient)
├── Rendering layer (IOutputRenderer)
│   ├── TableRenderer  (Spectre.Console)
│   ├── JsonRenderer   (System.Text.Json)
│   └── QuietRenderer  (success/error only)
├── Commands/
│   ├── RouteCommands       (route list/get/start/stop/inflight/force-stop)
│   ├── WatchdogCommands    (watchdog status/alerts/enable/disable)
│   └── DiagnosticsCommands (diagnostics dump/route/lifecycle/dashboard)
└── Config/
    └── ProfileManager      (~/.tsak/profiles/*.yaml)
```

## Dependencies

| Package | Purpose |
|---|---|
| `System.CommandLine` | Argument parsing, help generation |
| `Spectre.Console` | Rich table rendering, colors |
| `YamlDotNet` | Profile serialization |
| `redb.Tsak.Client` | Typed HTTP client |
| `redb.Tsak.Contracts` | Shared DTO models |

## Related projects

| Project | Role |
|---|---|
| [redb.Tsak.Client](../redb.Tsak.Client/README.md) | HTTP client used by the CLI |
| [redb.Tsak.Contracts](../redb.Tsak.Contracts/README.md) | DTO models for all API responses |
| [redb.Tsak.Worker](../redb.Tsak.Worker/) | Runtime the CLI connects to |
| [redb.Tsak](../../README.md) | Full project overview |
