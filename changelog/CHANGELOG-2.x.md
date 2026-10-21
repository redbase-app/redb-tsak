# redb.Tsak changelog: 2.x

Releases of the 2.x line, moved here unchanged from CHANGELOG.md when the main file started to
follow the current major only. Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [2.0.3] — 2026-05-18

Minor release that lets modules hosted on top of Tsak (e.g. `redb.Identity`)
expose their own OpenTelemetry `Meter` and `ActivitySource` names without
taking a runtime dependency on `redb.Tsak.Core`.

Fully backward-compatible: no public API surface added, both new keys default
to empty arrays, and the OTel pipeline is still activated only when
`Tsak:Metrics:Prometheus:Enabled=true`. Deployments that do not opt in to
Prometheus see no behaviour change vs `2.0.2`.

### Added
- `Tsak:Metrics:Prometheus:AdditionalMeters` (`string[]`) — extra Meter names
  subscribed into the OTel metrics pipeline alongside `RouteMetrics.MeterName`.
- `Tsak:Metrics:Prometheus:AdditionalSources` (`string[]`) — extra
  `ActivitySource` names subscribed into the OTel tracing pipeline alongside
  `RouteActivitySource.SourceName`.
- Defaults are empty arrays — existing deployments behave identically.
- Empty / whitespace entries in either array are silently skipped, so commented
  template values do not break startup.
- Example: `"AdditionalMeters": ["RedbIdentity"]` surfaces
  `IdentityMetrics` counters (`identity.login.attempts`,
  `identity.mfa.verifications`, …) on the Prometheus exporter without Identity
  taking a runtime dependency on Tsak or OpenTelemetry packages.

### Changed
- `ConfigureMonitoring` (`redb.Tsak.Core.Extensions.ServiceCollectionExtensions`)
  reads the two new config keys and wires the resulting names into
  `AddOpenTelemetry().WithMetrics(…).WithTracing(…)`. Only invoked when
  `Tsak:Metrics:Prometheus:Enabled=true`; otherwise the entire OTel block is
  skipped as before.
- `redb.Tsak.Worker/appsettings.json` ships both keys as empty arrays so the
  shape is discoverable in default deployments.
- `templates/tsak-worker/appsettings.json` carries a `_comment` documenting
  the `RedbIdentity` example for module authors.

### Not changed
- No new public types or method signatures in `redb.Tsak.Core` / `.Contracts`
  / `.Client` / `.CLI` / `.Web`.
- No dependency changes; `redb.Core` / `redb.Route` references remain on
  `2.0.2`.
- No SQL / storage / cluster behaviour changes.

---

## [2.0.2] — 2026-05-16

Patch release aligning with `redb.Core 2.0.2` and `redb.Route 2.0.2`.

### Changed
- Updated `redb.Core`, `redb.Postgres`, `redb.MSSql`, `redb.Core.Pro` dependencies to 2.0.2.
- Updated `redb.Route.*` dependencies to 2.0.2 (includes Http/Ldap 2.0.1 fixes).
- `EavSaveStrategy` renamed to `PropsSaveStrategy` throughout (`redb.Core` 2.0.2 API change).
- Version bumped to `2.0.2`. No source-level API changes vs `2.0.1`.

---

## [2.0.1] — 2026-05-13

Patch release that picks up critical production fixes from `redb.Route 2.0.1`
(`redb.Route.Http` + `redb.Route.Ldap`). All Tsak distribution artifacts
(Worker / Web / Stack Docker images and standalone archives) ship the updated
shared connector layer.

### Fixed
- **Shared connector layer** now includes `redb.Route.Ldap`,
  `redb.Route.Firebase`, and `redb.Route.Validation.Adapters` for all target
  frameworks (`net8.0` / `net9.0` / `net10.0`). Previous releases silently
  dropped these connectors from `Libs/shared/`, so route definitions that
  used `ldap:`, `firebase:` or FluentValidation/DataAnnotations adapters
  failed to load at runtime in Worker.
- Picks up `redb.Route.Http 2.0.1`: HTTP responses no longer echo back
  request headers, invalid header values are filtered, and body-less InOut
  responses (302 redirects, 204 No Content, Set-Cookie-only) propagate
  `Location` / `Set-Cookie` correctly.
- Picks up `redb.Route.Ldap 2.0.1`: service-account authenticated LDAP
  endpoints (with `bindDn` set) no longer reuse pooled connections, fixing
  intermittent *"successful bind must be completed"* errors against Active
  Directory. `PageSize=0` disables the RFC 2696 paged-results control for
  servers that do not support it.

### Changed
- `redb.Tsak/scripts/build-shared.ps1` and
  `redb.Tsak/publish/scripts/build-shared-multitfm.ps1` connector lists
  expanded from 20 to 23 entries to include the missing Ldap / Firebase /
  Validation.Adapters projects.
- Version bumped to `2.0.1`. No source-level API changes vs `2.0.0`.

## [2.0.0] — 2026-05-08

### Changed
- **License changed from MIT to Apache-2.0** for all OSS Tsak packages
  (`redb.Tsak.Core`, `redb.Tsak.Worker`, `redb.Tsak.Contracts`,
  `redb.Tsak.Client`, `redb.Tsak.CLI`, `redb.Tsak.Web`).
  - Apache 2.0 adds an explicit patent grant (§ 3) and termination clause —
    stronger protection for users and contributors.
  - Pro packages (`redb.Tsak.Core.Pro`, `redb.Tsak.Web.Pro`) remain under the
    commercial license in `LICENSE-PRO.txt`.
  - Every nupkg now ships `LICENSE` + `NOTICE` files (Apache 2.0 § 4 attribution).
  - Contributions are now accepted under Apache-2.0; see `CONTRIBUTING.md` and
    the parent `redb/CONTRIBUTING.md`.
- **Strong-Name signing** is active for Pro Tsak assemblies
  (Public Key Token: `8e6fea371ffeb38e`, shared with the main RedBase repo).
- Version bumped to `2.0.0` to align with the RedBase 2.0 release train
  (root packages and `redb.Route` also moved to 2.0.0). No source-level API
  changes vs 1.0.4.

### Why this is a major version bump
- License change is a downstream-compliance breaking change.
- Pro Strong-Name change is a binary-identity breaking change for Pro consumers.
