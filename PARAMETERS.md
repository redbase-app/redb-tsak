# redb.Tsak parameter reference

Every setting the worker reads, what it defaults to, and the ones that will bite you.

This is the **what**. For the **how** — the five layers, how `context.json` merges over
`appsettings.json`, and which key wins when two disagree — see [CONFIG_GUIDE.md](CONFIG_GUIDE.md).
Read that one first if a value you set is not taking effect; nine times out of ten a higher layer is
overwriting it.

Every key below is written in colon form (`Tsak:Api:Port`). In a container the same key is an
environment variable with `__` for the separator:

```
Tsak__Api__Port=9090
```

---

## Start here: three defaults that are not what you would guess

| Key | Default | Why it matters |
|---|---|---|
| `Tsak:Api:Host` | `127.0.0.1` | The management API is **loopback-only** out of the box. A container that publishes port 9090 still answers nothing from outside until you set this to `0.0.0.0`. That is deliberate — see [SECURITY.md](SECURITY.md). |
| `Tsak:Auth:Enabled` | `false` | Authentication is **off** by default. Combined with the line above that is safe on a laptop and wrong on a network. Turn it on before you change `Host`. |
| `Tsak:Storage:Type` | `InMemory` | State does not survive a restart until you point this at `Redb`. |

---

## `Tsak:Redb` — the database

This section configures the redb service the worker hosts.

| Key | Default | Notes |
|---|---|---|
| `Tsak:Redb:Provider` | `auto` | `postgres`, `mssql`, `sqlite`, or `auto` to infer from the connection string. |
| `Tsak:Redb:ConnectionString` | — | Required unless the provider resolves one itself. |
| `Tsak:Redb:UsePro` | `true` | Pro is free and needs no key through major 4 (`LicensePolicy.FreeThroughMajor`), so the default is Pro. Set `false` for the Free tier. |
| `Tsak:Redb:License` | — | Optional; a fallback license for named instances. |
| `Tsak:Redb:PropsSaveStrategy` | `ChangeTracking` | How property rows are written. **Requires Pro.** The Free provider implements only `DeleteInsert`: its batch path throws `NotSupportedException`, and since the cluster bootstrap saves in a batch on startup, the host stops. Setting `UsePro=false` means setting this to `DeleteInsert` too. |

### Every `RedbServiceConfiguration` property is settable here

`Tsak:Redb:<PropertyName>` binds directly onto redb's own configuration object — all 37 public
properties, by their exact property names. Some worth knowing:

| Key | Default | What it does |
|---|---|---|
| `Tsak:Redb:StringCollation` | `null` | Collation for string comparison, e.g. `und-x-icu`. Without it case-insensitive search folds ASCII only, so Cyrillic, Greek and most other scripts do not match across case. **Read ../COLLATION.md before setting it** — on PostgreSQL a collated operand cannot use an index built with the database's own collation, and a trigram search silently degrades to a full scan. |
| `Tsak:Redb:EnablePvtPrefilter` | `false` | Pro only, on all three providers (PostgreSQL, SQL Server, SQLite). Adds a cutting step before the pivot aggregate. It deliberately declines some query shapes — a nested `OR` under an `AND` is skipped because taking it would drop rows, not merely run slower. |
| `Tsak:Redb:EnsureCreated` | `true` for the unnamed instance, `false` for named ones | Create the schema at startup. Named instances assume the schema exists. |
| `Tsak:Redb:DefaultCheckPermissionsOnQuery` | `false` | Also `…OnLoad`, `…OnSave`, `…OnDelete`. Access control, not tuning. |
| `Tsak:Redb:SystemUserId` | `0` | The user id attributed to system writes. |
| `Tsak:Redb:AutoSyncSchemesOnSave` | `true` | Sync a scheme when an object of it is saved. |
| `Tsak:Redb:EnableLazyReferences` | `false` | V4: a `virtual` reference is a stub at any depth; its `Props` load on first access. |

> **Before 3.7.2 most of these did nothing.** Both configuration paths copied properties across one
> hand-written line at a time and the lists had gone stale — the named path carried 15 of 37, the
> unnamed one 12, and the two sets were not even subsets of each other, so the same key behaved
> differently depending on whether the instance had a name. Worse, an unrecognised key produced no
> diagnostic at all, so a typo and a missing feature looked identical. Both paths now bind by
> reflection and report unknown keys. If you are on an older build, check the source before trusting
> a setting from this table.

### `Tsak:Redb:Cache` — a subsection with its own vocabulary

This one predates the binder and uses **short names**, not property names. Both spellings work; these
are the ones to use inside `Cache`:

| Key | Maps to | Default |
|---|---|---|
| `Tsak:Redb:Cache:EnableProps` | `EnablePropsCache` | `false` |
| `Tsak:Redb:Cache:PropsMaxSize` | `PropsCacheMaxSize` | redb default |
| `Tsak:Redb:Cache:PropsTtlMinutes` | `PropsCacheTtl` | redb default |
| `Tsak:Redb:Cache:EnableList` | `EnableListCache` | `true` |
| `Tsak:Redb:Cache:ListTtlMinutes` | `ListCacheTtl` | redb default |
| `Tsak:Redb:Cache:EnableMetadata` | `EnableMetadataCache` | `true` |
| `Tsak:Redb:Cache:MetadataTtlMinutes` | `MetadataCacheLifetimeMinutes` | redb default |
| `Tsak:Redb:Cache:AutoRecomputeHash` | `AutoRecomputeHash` | `true` |
| `Tsak:Redb:Cache:CacheDomain` | `CacheDomain` | — |
| `Tsak:Redb:Cache:SkipHashValidationOnCacheCheck` | same | `false` |

> **`SkipHashValidationOnCacheCheck` + clustering is a stale-read hazard.** Skipping the hash check
> trusts the in-process cache without asking the database, which is only correct for a single writer.
> With `Tsak:Cluster:Enabled=true` the worker warns about this at startup; heed it.

---

## `Tsak:Api` — the management API

| Key | Default | Notes |
|---|---|---|
| `Tsak:Api:Enabled` | `true` | |
| `Tsak:Api:Host` | `127.0.0.1` | Loopback by default. See the table at the top. |
| `Tsak:Api:Port` | `9090` | |
| `Tsak:Api:Echo:Path` | `/api/echo` | |
| `Tsak:Api:AuthExempt` | — | Paths that skip authentication. Every entry is a hole you are opening on purpose. |
| `Tsak:Api:AuthThrottle:Limit` | `10` | Failed attempts before lockout. |
| `Tsak:Api:AuthThrottle:WindowSeconds` | `60` | |
| `Tsak:Api:AuthThrottle:LockoutSeconds` | `120` | |
| `Tsak:Http:TrustedProxies` | — | Addresses or CIDR networks of the reverse proxies in front of the worker, e.g. `["10.0.0.5", "10.1.0.0/16"]`. Set on the shared Kestrel host, so every HTTP listener in the process (the management API, module routes over Http / Soap / As2 / gRPC) sees the client's address and scheme instead of the proxy's. `X-Forwarded-For` is walked right to left past every listed proxy to the first address that is not one, so a chain of proxies resolves correctly; a header from a peer that is not listed is ignored. Keep the list to the proxies you operate: a range an attacker can send from lets them pick their own throttle bucket. A value that does not parse fails the start. |
| `Tsak:Api:AuthThrottle:TrustProxyHeaders` | `false` | **Deprecated, use `Tsak:Http:TrustedProxies`.** Takes the right-most `X-Forwarded-For` hop as the client, which is right behind exactly one proxy and wrong behind a chain (the hop is then the next proxy, and every caller shares one bucket). Ignored, with a log line, whenever `Tsak:Http:TrustedProxies` is set. |

## `Tsak:Auth` — API keys

| Key | Default | Notes |
|---|---|---|
| `Tsak:Auth:Enabled` | `false` | |
| `Tsak:Auth:Secret` | — | |
| `Tsak:Auth:Keys` | — | The key list. |
| `Tsak:Auth:EnforceRoles` | `true` | |
| `Tsak:Auth:RolelessKeysAreAdmin` | `false` | A key with no roles is denied rather than treated as admin. Fail-closed on purpose. |
| `Tsak:Auth:CacheTtlSeconds` | `300` | |
| `Tsak:Auth:RevocationCheckSeconds` | `30` | How quickly a revoked key stops working across the cluster. |

## `Tsak:Web` — the dashboard

| Key | Default | Notes |
|---|---|---|
| `Tsak:Web:Mode` | `standalone` | |
| `Tsak:Web:StandaloneUrl` | `http://localhost:9090` | |
| `Tsak:Web:Nodes` | — | Node list in cluster mode. |
| `Tsak:Web:AdminLogin` | — | |
| `Tsak:Web:AdminPasswordHash` | — | **Prefer this over `AdminPassword`.** A BCrypt hash, verified in constant time. |
| `Tsak:Web:AdminPassword` | — | Plaintext. Accepted, but it ends up in whatever holds your config. |
| `Tsak:Web:ServiceApiKey` | — | |
| `Tsak:Web:Lockout:MaxAttempts` | `5` | |
| `Tsak:Web:Lockout:WindowSeconds` | `300` | |
| `Tsak:Web:Lockout:DurationSeconds` | `60` | |

## `Tsak:Modules` — package loading

| Key | Default | Notes |
|---|---|---|
| `Tsak:Modules:AssemblyPaths` | — | Where `.tpkg` packages are looked for. **Must be `["modules"]` in anything you ship** — a development tree tends to leave an absolute path here, and on another machine that resolves to nothing and the worker loads zero modules without complaining. |
| `Tsak:Modules:Signature:Required` | `false` | |
| `Tsak:Modules:Signature:PublicKeyPath` | — | |
| `Tsak:Modules:Signature:PublicKeyPem` | — | Inline alternative to the path. |
| `Tsak:Modules:Upload:Enabled` | `false` | Upload over the API is off by default. |
| `Tsak:Modules:Upload:MaxSizeMB` | `100` | |
| `Tsak:Modules:Upload:TargetPath` | — | |
| `Tsak:Modules:Upload:KeepVersions` | `3` | |
| `Tsak:Modules:Upload:RequireSignatureForUpload` | `true` | |

## `Tsak:HotReload`

| Key | Default | Notes |
|---|---|---|
| `Tsak:HotReload:Enabled` | `true` | |
| `Tsak:HotReload:ScanIntervalSeconds` | `30` | |
| `Tsak:HotReload:Collectible` | `false` | Old module versions are **not** unloaded from memory. Collectible load contexts free memory but break modules that use `XmlSerializer` or `Emit`, so the safe choice is the default. Turn it on only if your modules are Emit-free and memory reclamation actually matters. |

## `Tsak:Cluster`

| Key | Default | Notes |
|---|---|---|
| `Tsak:Cluster:Enabled` | `false` | |
| `Tsak:Cluster:ClusterName` | `default` | |
| `Tsak:Cluster:GroupName` | `default` | |
| `Tsak:Cluster:Strategy` | `round-robin` | |

## `Tsak:Storage`

| Key | Default | Notes |
|---|---|---|
| `Tsak:Storage:Type` | `InMemory` | `Redb` to persist. |

## `Tsak:Audit` and `Tsak:Dlq`

| Key | Default |
|---|---|
| `Tsak:Audit:Enabled` | `true` |
| `Tsak:Audit:RetentionDays` | `90` |
| `Tsak:Dlq:Enabled` | `true` |
| `Tsak:Dlq:RetentionDays` | `30` |

Both retention sweeps run as cluster singletons, so enabling them on every node does not multiply the
work.

## `Tsak:Metrics` and `Tsak:Tracing`

| Key | Default | Notes |
|---|---|---|
| `Tsak:Metrics:Prometheus:Enabled` | `false` | |
| `Tsak:Metrics:Prometheus:Port` | `9464` | |
| `Tsak:Metrics:Prometheus:RequireAuth` | `false` | The scrape endpoint is open unless you say otherwise. |
| `Tsak:Metrics:Prometheus:AdditionalMeters` | — | Extra meters to export. |
| `Tsak:Metrics:Prometheus:AdditionalSources` | — | Extra activity sources. |
| `Tsak:Tracing:ServiceName` | — | |
| `Tsak:Tracing:Otlp:Enabled` | `false` | |
| `Tsak:Tracing:Otlp:Endpoint` | `http://localhost:4317` | |
| `Tsak:Tracing:Otlp:Protocol` | — | `grpc` or `http/protobuf`. |

## `Tsak:Health`, `Tsak:Logs`, `Tsak:Alerts`

| Key | Default | Notes |
|---|---|---|
| `Tsak:Health:DegradedNotReady` | `false` | Whether a degraded node reports itself not-ready. |
| `Tsak:Logs:BufferSize` | `2000` | In-memory log ring for the dashboard. |
| `Tsak:Alerts:Webhook:Url` | — | |
| `Tsak:Watchdog:Alerts` | — | |

## `Tsak:Contexts` — per-context settings

Layers 1, 2 and 5 of the model in [CONFIG_GUIDE.md](CONFIG_GUIDE.md):

| Key | Purpose |
|---|---|
| `Tsak:Contexts:default:*` | Base settings inherited by every context. |
| `Tsak:Contexts:{name}:*` | Overrides for one context. |
| `Tsak:Contexts:{name}:AutoStart` | Start the context with the worker. |
| `Tsak:Contexts:{name}:Dependencies` | Contexts that must start first. |
| `Tsak:Contexts:{name}:Override:*` | Wins over everything, including `context.json`. Intended for deployment-time secrets. |

---

## If a setting is not taking effect

1. **Check a higher layer is not overwriting it.** `Tsak:Contexts:{name}:Override` beats everything.
   [CONFIG_GUIDE.md](CONFIG_GUIDE.md) has the full order.
2. **Read the startup log.** The worker prints which redb settings it applied and warns about keys it
   did not recognise. A warning naming your key means the spelling is wrong; silence about a key you
   set means it never arrived.
3. **Check the environment-variable spelling.** `__` is the separator, not `:` and not `.`.
4. **For a named instance, check you are editing the right block.** Named instances take their
   settings from their own entry, not from `Tsak:Redb`.
