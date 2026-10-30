# Changelog — redb.Tsak

All notable changes to redb.Tsak are documented in this file.  
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).  
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## Projects

| Project | Description |
|---------|-------------|
| `redb.Tsak.Core` | Kernel: contracts, services, security, cluster, hot-reload, monitoring |
| `redb.Tsak.Core.Pro` | Pro features: redb-backed key store, cluster topology |
| `redb.Tsak.Worker` | Hosted process: DI wiring, Serilog, Quartz, Dockerfile |
| `redb.Tsak.Contracts` | Shared wire DTOs |
| `redb.Tsak.Client` | `ITsakApiClient` / `TsakApiClient` — HTTP client |
| `redb.Tsak.CLI` | `tsak` command-line tool — 30 commands |
| `redb.Tsak.Web` | Blazor Server dashboard — 10 pages |
| `redb.Tsak.Web.Pro` | Pro web extensions |
| `redb.Tsak.Templates` | `dotnet new` template pack — the `tsak-worker` host |

---

> **Note on version history:** redb.Tsak has been running in production since version 1.0.0.
> Versions 1.0.0 – 1.0.3 were not published to NuGet (internal deployments only).
> The first public NuGet release is **1.0.4**.

---

## [Unreleased]

Pending changes live in `changelog/unreleased/` until the release; `pwsh scripts/changelog.ps1 -Preview` shows them.

## [4.5.1] — 2026-10-30

### Fixed

- **A C# module (`InitRoute`) that loads XML resolves `<bean type>` from its own package.** After a hot reload the type
  came from the previous version of the package, and a property the new version added was "not writable". An own
  `IBeanTypeResolver` registered in `InitRoute` to work around this can be removed. Packages: `redb.Tsak.Core`.

## [4.5.0] — 2026-10-29

### Added

- **Requests refused as the caller's fault show on the dashboard and in the CLI.** An endpoint's `Refused` counter (an AS4
  or SOAP message with a bad signature, a broken envelope, an unknown agreement) appears next to `Rejected` on the overview,
  the endpoint and route pages and in `tsak` diagnostics, with the reason of the last refusal; a route that refuses requests
  is listed among the things that need attention. These refusals count no error, so before they did not show at all. Packages: `redb.Tsak.Contracts`, `redb.Tsak.Core`, `redb.Tsak.Web`, `redb.Tsak.CLI`.
- **The dead-letter store keeps the message attachments.** A failed exchange captured at a `.Replayable(...)` checkpoint
  is replayed with its attachments (names, types and content). The `tsak_dlq` table gets an `attachments_json` column on
  startup; entries captured before the upgrade are replayed as before. Packages: `redb.Tsak.Core`.
- **The worker carries the MIME multipart data format.** A module can call `AddMimeMultipartDataFormat()` from
  `redb.Route.DataFormats.Mime` on a Tsak worker without shipping the package in its own `.tpkg`, like the CSV, YAML, Avro
  and Protobuf formats.

### Changed

- **A `{Module}.config.json` beside a `.tpkg` overrides the one inside the package.** The package's own config used to win
  over the file on disk, so a deployment could not change a package's value without repacking it. The context name follows
  the same order. Only a folder with both files holding different values for one key behaves differently. Packages: `redb.Tsak.Core`. Upgrade: [UPGRADING.md](UPGRADING.md#package-config-beside-a-tpkg).

### Fixed

- **A `Contexts` section in the config inside a `.tpkg` is applied.** The `<Module>.config.json` embedded in a package
  was read flat: a `Contexts` section became one property, and its values never reached the context. It is now read like
  `context.json` and the file on disk: `Contexts.default`, then the module's own context. A flat file works as before. Packages: `redb.Tsak.Core`.
- **`<rest>` and `from http://` in an XML module work on the worker.** A module's context had `https` and `wss` but not
  `http` and `ws`, and such a module failed to start with "No component registered for scheme 'http'". The fix is in
  redb.Route.Http and redb.Route.WebSocket, which this release uses. Packages: `redb.Tsak.Core`.
- **A SQL module in XML works on the worker.** Such a module failed with "provider not registered" unless its provider
  happened to be the one of the audit log. The worker now registers `Npgsql`, `Microsoft.Data.SqlClient` and
  `Microsoft.Data.Sqlite` at start; name one of them as `ProviderName` or `provider=`. Packages: `redb.Tsak.Core`.
- **`"AutoStart": false` in a module's config is honoured.** A context's AutoStart was read only from appsettings, so the
  value in a package's `{Module}.config.json`, the file beside it or `context.json` had no effect and the context always
  started. It is now read from the merged layers; `Tsak:Contexts:{name}:AutoStart` in appsettings still overrides them. Packages: `redb.Tsak.Core`.
- **An XML module that reads a file from `resources/` starts on the worker.** The worker extracted `resources/order.xsd` to
  the root of the package's folder, so `file="resources/order.xsd"` in `<validateXsd>`, `<validateJsonSchema>`, `<xslt>` or
  `<payload>` was not found and the module failed to initialize. Resources now keep their path; `file="order.xsd"` works too. Packages: `redb.Tsak.Core`.
- **At start the worker opens the packages of a folder in dependency order.** A package that uses a carrier's shared DLLs
  (`Dependencies` in its manifest, as `redb.Identity.Http` uses `redb.Identity.Contracts`) could be opened first, fail with
  "Failed to scan package" and start only on the next hot-reload scan, seconds later. Now carriers are opened first, as the
  hot-reload scan already did. Packages: `redb.Tsak.Core`.
- **The watchdog does not count a planned wait as a hang.** An exchange waiting for an `onException` redelivery longer than
  `SuspectedThresholdMinutes` was reported as suspected, and past `HungThresholdMinutes` with `AutoRestartHungRoutes` its
  route was restarted in the middle of the wait. Such an exchange is now skipped while it waits, with one log line saying
  why, and measured from the end of the wait afterwards. The same holds for `Retry`, the dead letter channel and `delay`. Packages: `redb.Tsak.Core`.
- **The cluster dashboard seeds its administrator from `Tsak:Web:AdminPasswordHash`.** Cluster mode read only the plaintext
  `AdminPassword`; with the recommended hash alone the admin had no password and nobody could sign in. The hash now wins over
  the plaintext, as in standalone mode; a value that is not a BCrypt hash is reported once and the seed stops. Packages: `redb.Tsak.Web.Pro`.
- **The cluster dashboard no longer crashes on its first start against an empty database.** Started together with the
  worker (the stack image), it read the user table before the worker had created the redb schema and ended with an
  unhandled exception, restarted by supervisord. The administrator is now seeded in the background once the schema is
  there; the dashboard serves meanwhile. Packages: `redb.Tsak.Web.Pro`.

## Earlier versions

Earlier releases are archived by line in [changelog/](changelog/): [4.4.x](changelog/CHANGELOG-4.4.x.md), [4.3.x](changelog/CHANGELOG-4.3.x.md), [4.2.x](changelog/CHANGELOG-4.2.x.md), [4.1.x](changelog/CHANGELOG-4.1.x.md), [4.0.x](changelog/CHANGELOG-4.0.x.md), [3.x](changelog/CHANGELOG-3.x.md), [2.x](changelog/CHANGELOG-2.x.md), [1.x](changelog/CHANGELOG-1.x.md).
The worker's development log of March 2026, before the versioned releases, is
[changelog/worker-devlog-2026-03.md](changelog/worker-devlog-2026-03.md).
