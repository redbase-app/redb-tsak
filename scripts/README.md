# scripts/

Tooling for the **shared runtime layer** — `Libs/shared/`, the directory the Worker loads the redb.*
framework and the Route connectors from at startup.

Since 3.4.0 that layer holds **both**:

- the **framework** — `redb.Core(.Pro)`, the storage providers, `redb.Route.Core/Http/Quartz`;
- the **connectors** — RabbitMQ, Kafka, Elasticsearch, LDAP, Llm, … with their native payloads
  (`runtimes/<rid>/native`, e.g. librdkafka, e_sqlite3).

Only `redb.Tsak.*` and `redb.Licensing` remain in the application bin; every redb.* framework DLL is
pruned from it at build time. **A Worker started without a populated `Libs/shared/` fails fast** with
a message naming the missing assembly — that is by design, not a bug to work around.

The payoff: a binary-compatible patch of any library, or a new beta connector, ships by swapping a
DLL in `Libs/shared/` — no rebuild of the Worker, no re-cut archives.

## `shared-manifest.psd1`

The single source of truth for what belongs in the layer — two lists, `Framework` and `Connectors`.
Both scripts read it. Adding a connector means adding one line here, nowhere else.

## `build-shared.ps1` — populate the layer

```powershell
# dev: connectors + framework into src/redb.Tsak.Worker/Libs/shared (Debug, net10.0)
./scripts/build-shared.ps1 -IncludeFramework

# rebuild just two of them
./scripts/build-shared.ps1 -IncludeFramework -Clean -Only IbmMq,RabbitMQ

# publish mode: one directory per TFM under <OutRoot>, Release
./scripts/build-shared.ps1 -Configuration Release -Tfms net8.0,net9.0,net10.0 -OutRoot publish/staging -IncludeFramework
```

`-IncludeFramework` is what makes the Worker actually start; without it you get connectors only and
the fail-fast on the first framework type.

**Building from the standalone repository.** The Route sources are a separate repository, so point
the script at a checkout of <https://github.com/redbase-app/redb-route>:

```powershell
git clone https://github.com/redbase-app/redb-route ../redb-route
./scripts/build-shared.ps1 -IncludeFramework -RouteSrc ../redb-route
```

`-RouteSrc` accepts either the repository root or its `src/` directly. In the monorepo the parameter
is omitted and the layout is resolved automatically.

## `refresh-shared.ps1` — patch one library in place

```powershell
# into the dev tree
./scripts/refresh-shared.ps1 -Lib redb.Route.Http

# into an unpacked distribution archive
./scripts/refresh-shared.ps1 -Lib redb.SQLite.Pro -SharedDir D:\dist\redb-tsak-3.4.0-win-x64\worker\Libs\shared
```

Rebuilds a single library and drops its DLL into the target layer. Takes the same `-RouteSrc`.

⚠️ The host compares the **minor** version of the shared layer against its own on startup: a patch
difference is fine — that is the whole point — but a minor mismatch, or a mix of minors inside
`Libs/shared/`, aborts the process. So a given Worker build accepts patches of its own minor only;
a new ecosystem minor means a full release.

## Do not put modules here

`Libs/shared/` is process-global: everything in it is loaded into the default `AssemblyLoadContext`
and shared by every module. Module packages belong in the worker's `modules/` directory, where they
are loaded in isolation and can be hot-reloaded.
