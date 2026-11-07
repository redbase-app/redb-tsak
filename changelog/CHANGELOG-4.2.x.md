# redb.Tsak changelog: 4.2.x

Releases of the 4.2 line, moved here unchanged from CHANGELOG.md, which keeps the latest released line only.
Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [4.2.2] — 2026-10-05
### Fixed — a package reload follows the manifest's order, and a replaced carrier drags its dependents along

Hot reload scanned the module directory in filesystem order (`Directory.GetFiles` does not sort), so when a
carrier and the packages that depend on it were replaced in the same pass, the dependent could be reloaded
before the carrier and come up on the carrier's old shared types. The startup scan never had this problem:
`TsakCoordinator` topologically sorts what it starts by the declared `Dependencies`. A reload now does the
same, by the package's own `Manifest.Dependencies`: every changed `.tpkg` across the scanned paths is
gathered first, ordered so that a carrier precedes the packages that declare it, and only then reloaded. A
cycle is logged and the packages keep their scan order rather than being dropped.

The second half is the rule `redb.Identity.Contracts.Module` states in prose: reloading a package replaces
the companion DLLs it ships, so it must be followed by reloading every module that uses them. A reload
force-replaces its companions in `LoadedAssemblyTracker`, and a dependent keeps the old assembly identity in
its already-loaded code, so leaving it alone handed it the same CLR type in two copies and a typed body
crossing `direct-vm` quietly became `null`. TSAK now reloads those dependents itself: a reloaded
package that ships companions pulls in, transitively, every loaded package that names it in
`Manifest.Dependencies`, after the carrier. A dependent that cannot be re-verified is named in a warning and
stays up on the old types until its own reload. The deployment no longer has to order the drops by hand to
match the manifest.

## [4.2.1] — 2026-10-02
### Fixed — the `tsak-worker` template starts on a machine with nothing installed

`dotnet new tsak-worker` followed by `dotnet run` stopped on `Npgsql: Failed to connect to
127.0.0.1:5432`: the generated `appsettings.json` pinned `Provider: postgres`, and `-db` offered no other
database for a laptop — although the runtime has carried `redb.SQLite` since 4.2.0 and the worker image
itself defaults to SQLite. `sqlite` is now a `-db` choice and the default one (`ConnectionStrings:Sqlite`
→ `Data Source=tsak.db`), so the host starts by creating its database file next to itself. Postgres and
SQL Server remain one flag away.

### Fixed — a module no longer dies on its first write when the host runs the Free tier

The template wrote `PropsSaveStrategy: ChangeTracking` next to `UsePro: false`, and the Free provider does
not implement batch change tracking: any route that saves inside `Transacted()` — the ordinary pattern —
failed with `NotSupportedException: PropsSaveStrategy.ChangeTracking (batch) is not implemented in this
provider`. The strategy now follows the tier: `--pro true` (the new default, matching the worker image)
keeps `ChangeTracking`, `--pro false` writes `DeleteInsert`.

### Security — the management API is bound to loopback by default

`Tsak:Api:Host` was `0.0.0.0` while `Tsak:Auth:Enabled` was false and the secret was the literal
`CHANGE_ME_TO_A_LONG_RANDOM_VALUE` — the unprotected management API the runtime warns about on every
start, published in a template. The API now listens on `127.0.0.1` with an empty secret; opening it up is
a deliberate edit of `Tsak:Api:Host` and `Tsak:Auth`.

### Fixed — the template no longer carries switches the runtime ignores

`HotReload:RollingUpdate: true` made every start log `Tsak:HotReload:RollingUpdate=true is NOT
implemented`. The default is `false` until the runtime implements it.

### Added — the pack is released apart from the libraries, and the folder rules are written down

`redb.Tsak.Templates` and `redb.Templates` now have their own release path: the packs stay at their own
version while the libraries move on, and the release tooling gates the redb version the templates pin.
The generated project's README explains the two rules that decide whether a module loads — modules are
scanned relative to the working directory, `Libs/shared` is resolved next to the application, not next to
the module — and the repository's `scripts/` ships the smoke checks that generate, run and verify both
packs (`smoke-template.ps1`, `smoke-module-in-host.ps1`).

## [4.2.0] — 2026-09-30
### Fixed — the audit writer's endpoint no longer carries its whole INSERT, so it stops flooding the log

The endpoint of the audit writer route was the entire statement plus seventeen `param.*` options, so every
start logged a single line of some two kilobytes — `Endpoint sql://INSERT INTO tsak_audit_log (...) VALUES
(...)?dataSource=...&param.user_agent=... started successfully` — and the Endpoints page showed the same wall
of text. The INSERT is now a named query the writer publishes on its own context, and the values travel as a
map keyed by column, which the sql connector binds by itself; the endpoint reads
`sql:ref:tsak-audit-insert?dataSource=tsak-audit-ds&mode=Execute`. Nothing written changes: the same columns
get the same values, and a missing header is still stored as NULL. Whoever mounts the writer still publishes
only the data source.

### Security — a module whose http input carries credentials no longer starts, because nothing ever checked them

`authScheme`, `username`, `password` and `authToken` are what an http producer sends to the service it calls.
They share one options class with the consumer, so on a `from("http:...")` they were accepted — and then checked
nothing. An input written as `http:0.0.0.0:8080/orders?authScheme=Basic&username=ops&password=...` looked
protected and took every caller, with the password sitting in the route key. redb.Route now refuses such an
input when it creates the consumer, naming the parameters and never their values.

What this means in Tsak: the consumer is created while its context starts, so the whole context holding such a
route stays stopped, not just the one route, and the worker log names the endpoint and the parameters. Nothing
in this repository declares one, but a module deployed from elsewhere may. The fix is to remove those
parameters from the input and to put inbound authentication in front of the route — a reverse proxy, or a
processor that validates the `Authorization` header. The key-based authentication of Tsak's own management API
does not cover module routes and never did.
