# tsak CLI

Command-line interface for managing [redb.Tsak](../README.md) runtime instances.

## Installation

```bash
dotnet tool install -g redb.Tsak.CLI
```

After installation the `tsak` command is available globally.

## Quick Start

```bash
# Configure a connection profile
tsak profile add --name local --url http://localhost:5100

# Set it as the active profile
tsak profile use local

# Check system health
tsak system health

# List route contexts
tsak context list

# View recent logs
tsak log get --limit 20 --level Warning
```

## Global Options

Every command accepts these options:

| Option | Alias | Description |
|--------|-------|-------------|
| `--profile` | `-p` | Connection profile name |
| `--url` | `-u` | Override runtime URL |
| `--key` | `-k` | Override API key |
| `--output` | `-o` | Output format: `Table` (default), `Json`, `Quiet` |
| `--no-color` | — | Disable colored output |
| `--yes` | `-y` | Skip confirmation prompts on destructive operations |
| `--watch` | — | Poll repeatedly (Ctrl+C to stop) |
| `--interval` | — | Polling interval in seconds (default: 5, used with `--watch`) |

### Connection Resolution

The CLI resolves the target URL and API key in this order:

1. Explicit flags (`--url`, `--key`)
2. Environment variables (`TSAK_URL`, `TSAK_API_KEY`)
3. Active profile (`~/.tsak/active`)
4. Named profile (`--profile <name>`)

## Commands

### system — System Health, Metrics, Info

```bash
tsak system health            # Health check with individual check statuses
tsak system health --watch    # Live health monitoring (every 5s)
tsak system metrics           # CPU, memory, GC, thread stats
tsak system metrics --watch --interval 10  # Metrics every 10s
tsak system info              # Version, uptime, machine info
```

### context — Route Contexts

```bash
tsak context list             # List all contexts (name, status, endpoints)
tsak context get <name>       # Detailed context info
tsak context start <name>     # Start a stopped context
tsak context stop <name>      # Stop a running context
tsak context restart <name>   # Restart a context
tsak context remove <name>    # Remove (asks confirmation)
tsak context remove <name> -y # Remove without confirmation
```

### module — Runtime Modules

```bash
tsak module list              # List modules (name, version, status)
tsak module get <name>        # Module details and dependencies
tsak module remove <name>     # Remove (asks confirmation)
tsak module remove <name> -y  # Remove without confirmation
```

### scheduler — Quartz Scheduler

```bash
tsak scheduler status                  # Scheduler state overview
tsak scheduler status --watch          # Live scheduler monitoring
tsak scheduler jobs                    # Scheduled jobs with cron and next fire time
tsak scheduler running                 # Currently executing jobs
tsak scheduler running --watch         # Live running jobs view
tsak scheduler start                   # Start the scheduler
tsak scheduler standby                 # Put scheduler in standby
tsak scheduler pause <group> <name>    # Pause a specific job
tsak scheduler resume <group> <name>   # Resume a paused job
```

### cluster — Cluster Topology

```bash
tsak cluster status           # Cluster state, node ID, leader, epoch
tsak cluster nodes            # All nodes with heartbeat info
tsak cluster rebalance        # Trigger rebalance (asks confirmation)
tsak cluster rebalance -y     # Rebalance without confirmation
```

### log — Runtime Logs

```bash
tsak log get                            # Recent log entries (default limit)
tsak log get --limit 50                 # Last 50 entries
tsak log get --level Error              # Only errors and above
tsak log get --limit 100 --level Warning
```

Valid log levels: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`.

### auth — API Key Management

```bash
tsak auth list                # List all API keys
tsak auth create --name "deploy-key" --roles admin,reviewer
tsak auth create --name "temp" --expires-at "2026-12-31T23:59:59"
tsak auth revoke <id>         # Revoke (asks confirmation)
tsak auth revoke <id> -y      # Revoke without confirmation
```

The generated key value is displayed **only on creation** — store it securely.

### profile — Connection Profiles

Profiles are stored locally in `~/.tsak/profiles/` as YAML files. No API calls are made.

```bash
tsak profile list                                          # List profiles (* = active)
tsak profile add --name prod --url https://tsak.example.com --key sk-xxx
tsak profile remove prod
tsak profile use prod                                      # Set active profile
```

## Output Formats

### Table (default)

Human-readable tables with aligned columns. Use `--no-color` for plain text.

```bash
tsak context list
```

### JSON

Machine-readable output for scripting and piping:

```bash
tsak context list --output json | jq '.[] | .name'
```

### Quiet

Minimal output — only success/error messages, suitable for scripts:

```bash
tsak context start my-ctx --output quiet && echo "Started"
```

## Profiles

### File Structure

Profiles live in `~/.tsak/profiles/<name>.yml`:

```yaml
url: https://tsak.example.com
apiKey: sk-1234567890abcdef
```

The active profile name is stored in `~/.tsak/active`.

### Environment Variables

| Variable | Description |
|----------|-------------|
| `TSAK_URL` | Runtime base URL |
| `TSAK_API_KEY` | API key for authentication |

Environment variables override profile values but are overridden by explicit `--url`/`--key` flags.

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Success |
| `1` | Runtime/application error |
| `≥2` | HTTP error from API (status code mapped) |

## Examples

### Monitor in a script

```bash
#!/bin/bash
export TSAK_URL=http://localhost:5100

# Check health
STATUS=$(tsak system health --output json | jq -r '.status')
if [ "$STATUS" != "Healthy" ]; then
  echo "ALERT: Tsak is $STATUS"
fi

# Count running jobs
RUNNING=$(tsak scheduler running --output json | jq '.jobs | length')
echo "Running jobs: $RUNNING"
```

### Manage multiple environments

```bash
# Setup profiles
tsak profile add --name dev --url http://localhost:5100
tsak profile add --name staging --url https://staging.example.com --key sk-stg
tsak profile add --name prod --url https://prod.example.com --key sk-prod

# Compare contexts across environments
tsak --profile dev context list
tsak --profile staging context list
tsak --profile prod context list
```

### Scheduler operations

```bash
# View scheduled jobs
tsak scheduler jobs

# Pause a job, do maintenance, resume
tsak scheduler pause DEFAULT cleanup-job
# ... maintenance ...
tsak scheduler resume DEFAULT cleanup-job
```

## Confirmation Prompts

Destructive commands ask for confirmation before executing:

```
$ tsak context remove payment-gateway
Remove context 'payment-gateway'? [y/N]: y
Context 'payment-gateway' removed.
```

Commands with confirmation: `context remove`, `module remove`, `cluster rebalance`, `auth revoke`.

Skip with `--yes` / `-y`:

```bash
tsak context remove old-context --yes
```

Confirmation is also skipped automatically when output format is `Json` or `Quiet` (implies scripting).

## Watch Mode

Add `--watch` to monitoring commands for continuous polling:

```bash
tsak system health --watch                # Poll every 5 seconds
tsak system metrics --watch --interval 10 # Poll every 10 seconds
tsak scheduler status --watch
tsak scheduler running --watch
```

Press `Ctrl+C` to stop. Default interval is 5 seconds.

## Shell Auto-Complete

The CLI supports tab completion via `dotnet-suggest` (built into System.CommandLine):

```bash
# Install dotnet-suggest globally
dotnet tool install -g dotnet-suggest
```

### Bash / Zsh

Add to `~/.bashrc` or `~/.zshrc`:

```bash
eval "$(dotnet-suggest register)"
```

### PowerShell

Add to `$PROFILE`:

```powershell
Register-ArgumentCompleter -Native -CommandName tsak -ScriptBlock {
    param($wordToComplete, $commandAst, $cursorPosition)
    dotnet-suggest get -e "tsak" -- "$commandAst" |
        ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
}
```

### Fish

```fish
dotnet-suggest register --shell fish | source
```

After setup, tab completion works for all commands, subcommands, options, and enum values:

```
tsak sch<TAB>         → tsak scheduler
tsak scheduler <TAB>  → status  jobs  running  start  standby  pause  resume
tsak --output <TAB>   → Table  Json  Quiet
```
