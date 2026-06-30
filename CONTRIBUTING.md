# Contributing to redb.Tsak

Thank you for your interest in contributing to **redb.Tsak** — the runtime container for redb.Route.

> Before contributing, please read the [redb.Route CONTRIBUTING guide](../redb.Route/CONTRIBUTING.md).  
> The conventions there (coding style, PR process, commit messages) apply here as well.

---

## Table of Contents

1. [Reporting Bugs](#reporting-bugs)
2. [Feature Requests](#feature-requests)
3. [Development Setup](#development-setup)
4. [Code Guidelines](#code-guidelines)
5. [Adding a New CLI Command](#adding-a-new-cli-command)
6. [Adding a New REST Endpoint](#adding-a-new-rest-endpoint)
7. [Module Development](#module-development)
8. [Testing](#testing)
9. [Commit Format](#commit-format)

---

## Reporting Bugs

When filing an issue include:

- **Project**: which sub-project (`Worker`, `CLI`, `Web`, `Client`, `Core`, `Cluster`, etc.)
- **Version**: `tsak --version` or the NuGet package version
- **Deployment mode**: `InMemory` / `Redb:Postgres` / `Redb:MSSql` / Cluster
- **.NET version**: `dotnet --version`
- **Minimal reproduction**:
  - Relevant `appsettings.json` excerpt (no credentials — see [DEPLOYMENT_SECRETS.md](DEPLOYMENT_SECRETS.md))
  - The `tsak` CLI command or REST call that triggers the bug
  - The module code (stripped down) if module loading is involved
- **Expected behavior**
- **Actual behavior** — include structured log output from `tsak logs` or the worker console

---

## Feature Requests

Before opening a feature request:

1. Check whether the feature belongs in **redb.Tsak** (runtime container infrastructure) or in **redb.Route** (pipeline DSL and transports). Tsak does not implement connectors or EIP processors.
2. Search existing issues for duplicates.
3. Open an issue with:
   - **Problem statement** — what you cannot do today
   - **Proposed solution** — API shape, config keys, CLI commands
   - **Alternatives considered**
   - Whether this requires a breaking change to any public interface

---

## Development Setup

```bash
# Clone
git clone https://github.com/redbase-app/redb.git
cd redb

# Restore
dotnet restore redb.Tsak

# Build Worker
dotnet build redb.Tsak/src/redb.Tsak.Worker

# Run Worker (standalone, no DB)
dotnet run --project redb.Tsak/src/redb.Tsak.Worker

# Run CLI
dotnet run --project redb.Tsak/src/redb.Tsak.CLI -- help

# Run Web dashboard
dotnet run --project redb.Tsak/src/redb.Tsak.Web
```

For cluster mode, start PostgreSQL and set:

```json
{
  "ConnectionStrings": { "Postgres": "..." },
  "Tsak": {
    "Storage": { "Type": "Redb" },
    "Redb": { "Provider": "postgres" },
    "Cluster": { "Enabled": true }
  }
}
```

---

## Code Guidelines

All guidelines from the redb.Route CONTRIBUTING guide apply. Tsak-specific additions:

- **No business logic in the Worker project** — `redb.Tsak.Worker` is only DI wiring, configuration binding, and hosted service registration. Business logic lives in `redb.Tsak.Core`.
- **`_system` context is sacred** — never let API endpoints stop, delete, or modify the `_system` context. Its protection logic lives in `ContextManager` and must not be weakened.
- **Constant-time comparisons for keys** — any code that compares API key hashes must use `CryptographicOperations.FixedTimeEquals`. Do not use `==` or `string.Equals` for secrets.
- **No distributed state without epoch fencing** — cluster operations that mutate shared state must carry an epoch token to prevent split-brain on network partition recovery.
- **Config changes must preserve 5-layer semantics** — if you add a new config key, define it in the `default` context and document which layers can override it.
- **All new endpoints need auth checks** — use the `[RequireRole("...")]` attribute or the equivalent middleware. No anonymous write endpoints.
- **Ring buffer log** — use structured Serilog logging, not `Console.WriteLine`. The `LogRingBuffer` sink must capture all operational events.

---

## Adding a New CLI Command

1. Create a command file in `redb.Tsak.CLI/Commands/`.
2. Subclass `TsakCommand` (or the appropriate group base).
3. Register the command in `Program.cs` under the correct group.
4. Add table rendering via `Spectre.Console` — match the visual style of existing commands.
5. Add `--output json` support using the shared `OutputFormatter`.
6. Write tests in `redb.Tsak.CLI.Tests/` — cover success path, error responses, and auth failure.
7. Update `README.md` command list.

Command implementation checklist:

- [ ] Handles `--server` / `--key` global options for targeting a remote Tsak instance
- [ ] Propagates `CancellationToken` through all async calls
- [ ] Returns appropriate exit codes (0 = success, 1 = error, 2 = auth failure)
- [ ] Produces machine-readable JSON when `--output json` is passed
- [ ] Includes a `--help` description

---

## Adding a New REST Endpoint

1. Identify which controller the endpoint belongs to (or create a new one if the category is new).
2. Add the corresponding method to `ITsakApiClient` and implement it in `TsakApiClient`.
3. Add a DTO to `redb.Tsak.Contracts` if new request/response shapes are needed.
4. Annotate with the appropriate `[RequireRole(...)]` attribute.
5. Document the endpoint in `README.md` — update the endpoint count in the table.
6. Add unit tests for the service layer and integration coverage for the controller method.

---

## Module Development

A Tsak module is a plain .NET class library. To contribute module examples:

1. Follow the `InitRoute.main(IRouteContext)` entry point convention.
2. Ship `manifest.json`, `context.json`, and `{Module}.config.json` alongside the DLL.
3. Do not assume a specific DB or infrastructure — use `context.json` for defaults so operators can override via Layer 5.
4. Document the module's routes, transports, and config keys in its own `README.md`.
5. See [redb.Route.Demo](../redb.Route.Demo/README.md) as a reference implementation.

---

## Testing

Run the full test suite:

```bash
dotnet test redb.Tsak/tests/redb.Tsak.Tests
dotnet test redb.Tsak/tests/redb.Tsak.CLI.Tests
```

Testing requirements for PRs:

- New services must have unit tests using `NSubstitute` for dependencies and `FluentAssertions` for assertions.
- Cluster logic must be tested with mocked `IClusterCoordinator` — do not write tests that require actual distributed locking.
- CLI commands must have tests covering at least the success path and authentication failure.
- Hot-reload logic must have tests that simulate file-change events without touching the file system (use the `IFileSystemWatcher` abstraction).
- All tests must pass without environment variables, database connections, or network access.

---

## Commit Format

```
<type>(<scope>): <short description>

[optional body]

[optional footer]
```

Types: `feat`, `fix`, `docs`, `refactor`, `test`, `chore`

Scopes: `core`, `worker`, `cli`, `web`, `client`, `contracts`, `cluster`, `hotreload`, `security`, `scheduler`, `monitoring`, `watchdog`

Examples:

```
feat(cluster): add weighted assignment strategy

Adds a WeightedRoundRobin assignment manager that distributes contexts
based on node metric scores (CPU + memory composite).

Closes #142
```

```
fix(security): use constant-time comparison in InMemory key store

The InMemory store was using string.Equals which is susceptible to
timing attacks. Replaced with CryptographicOperations.FixedTimeEquals.
```

```
feat(cli): add `route stop` and `route start` commands
```
