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

## [4.7.1] — 2026-11-11

### Security

- **Archives ship the .NET 10.0.15 runtime, SignalR client dependencies are patched.** The self-contained archives
  carried the 10.0.14 runtime, affected by CVE-2026-114147 (ASP.NET Core denial of service); they now carry 10.0.15.
  The container images already run on the patched base. Replace the archive or pull the new image.

### Fixed

- **Dashboard log lines show their date.** The Logs page (Live tab) and the node's Logs tab showed only the time of a
  line; they now show `yyyy-MM-dd HH:mm:ss.fff` in the node's time zone, with the UTC offset in the tooltip. The Files
  tab labels its modification time as UTC. Packages: `redb.Tsak.Web`.
- **The cluster dashboard waits for the redb schema on its first start instead of failing once.** Against an empty
  database it no longer logs a hosting failure before the worker has created the schema: it waits, up to 300 s by
  default (`Tsak:Web:SchemaWaitSeconds`, `0` keeps the old behaviour), and then fails with a message naming the cause. Packages: `redb.Tsak.Web.Pro`.

## [4.7.0] — 2026-11-06

### Added

- **The worker ships the xmljson data format.** A route package can convert XML to JSON and back with
  `<dataFormat type="redb.Route.DataFormats.XmlJson.XmlJsonDataFormat, redb.Route.DataFormats.XmlJson">`, `<marshal type>`
  or `MarshalXmlJson` on the worker without carrying `redb.Route.DataFormats.XmlJson.dll` in the package.

### Deprecated

- **.NET 8 and .NET 9 targets end with 4.7.** Microsoft's support for both ends on 10 November 2026, so 4.7.0 is the
  last release whose packages target them; from 4.8.0 the packages target .NET 10 and later. The images already run on
  .NET 10 and do not change. Fixes on the 4.7.x line are still built for .NET 8, 9 and 10 while those lines can be built
  without known vulnerabilities. Move projects that reference the packages to .NET 10 before taking 4.8.

## Earlier versions

Earlier releases are archived by line in [changelog/](changelog/): [4.6.x](changelog/CHANGELOG-4.6.x.md), [4.5.x](changelog/CHANGELOG-4.5.x.md), [4.4.x](changelog/CHANGELOG-4.4.x.md), [4.3.x](changelog/CHANGELOG-4.3.x.md), [4.2.x](changelog/CHANGELOG-4.2.x.md), [4.1.x](changelog/CHANGELOG-4.1.x.md), [4.0.x](changelog/CHANGELOG-4.0.x.md), [3.x](changelog/CHANGELOG-3.x.md), [2.x](changelog/CHANGELOG-2.x.md), [1.x](changelog/CHANGELOG-1.x.md).
The worker's development log of March 2026, before the versioned releases, is
[changelog/worker-devlog-2026-03.md](changelog/worker-devlog-2026-03.md).
