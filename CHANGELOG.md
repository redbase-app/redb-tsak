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

## [4.7.7] — 2026-11-28

### Changed

- **Tsak 4.7.7 and its images run on redb 4.7.7, which makes fewer round trips per Pro list call and runs several
  groupings faster.** Apart from the two fixes below, Tsak's code is unchanged. A database needs no conversion step. Packages: `redb.Tsak.Core`, `redb.Tsak.Worker`.

### Fixed

- **A node's start time and uptime count from the start of the node.** The monitoring page took them from the first
  request for system info, so a node up for hours showed a few seconds after the dashboard first opened it.
  `/api/system/info` and the cluster topology now report the same start time, the start of the node's process, which
  also no longer moves when a node registers again. Packages: `redb.Tsak.Core`, `redb.Tsak.Core.Pro`.
- **A node page of the dashboard shows the node in its address after an in-app change of node.** Following a link to
  the same page of another node kept the previous node's data on screen until the page's next refresh, and for good on
  the Storage, Dead-letter and Audit pages; the page is now built anew for the new node. Packages: `redb.Tsak.Web`.

## [4.7.6] — 2026-11-27

### Changed

- **Tsak 4.7.6 and its images run on redb 4.7.6, which loads pages with less memory and plans more queries through the
  indexes.** Tsak's own code is unchanged. A database needs no conversion step. Packages: `redb.Tsak.Core`, `redb.Tsak.Worker`.

## [4.7.5] — 2026-11-24

### Changed

- **Tsak 4.7.5 runs on redb 4.7.5, which stores `TimeSpan` and `TimeOnly` as ticks.** Tsak's own data holds no such
  values, so an existing Tsak database needs no conversion before the first start. Every node and image that shares the
  database still moves to 4.7.5 together; there is no way back except a backup. Packages: `redb.Tsak.Core`, `redb.Tsak.Worker`.
- **The start-up log no longer mentions a licence.** The Pro banner is a plain welcome, without a link to buy anything
  or a free-tier notice, and the cluster dashboard no longer warns that it runs without a licence: Pro is free of
  charge and needs no key. Packages: `redb.Tsak.Core.Pro`, `redb.Tsak.Web.Pro`.

### Fixed

- **The dashboard writes a node id from the page address into its log with line breaks escaped.** A `%0D%0A` in the
  address could start a forged line in a plain-text log.

## [4.7.2] — 2026-11-13

### Changed

- **Built on redb 4.7.2 and redb.Route 4.7.2.** The images and archives take the SQLite fix of redb 4.7.2: a save repeated
  after a rolled-back transaction no longer overwrites another object. redb.Tsak itself has no change.

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
