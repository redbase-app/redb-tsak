# Upgrading redb.Tsak

What to check and change when moving to a new version. Each section lists the changes that need action;
[CHANGELOG.md](CHANGELOG.md) has the full list of what changed.

## 4.5.0

### Package config beside a .tpkg

**A `{Module}.config.json` on disk beside a `.tpkg` now overrides the one inside the package** (it was the other way
round). Layer 4 is read as: the package's own `{Module}.config.json` first, then the file beside the package, so a
deployment can change a value of a package without repacking it. The module's context name is looked up in the same
order. Whom it concerns: only a folder that holds both a `.tpkg` and a `{Module}.config.json` of the same module with
different values for one key (or a different `ContextName`): the file on disk now wins. Projects made from the
redb.Templates templates keep only `ContextName` and `AutoStart` inside the package and `context.json` beside it, and
work as before. To keep the package's value, remove the key from the file beside it, or the file itself.

## 4.4

### Node id and Quartz instance id

**One node id, one live worker.** A worker that finds its node id (`Tsak:Cluster:NodeId`, or the host name) held by
a record that is still alive watches it until its heartbeat would expire (`DeadNodeTimeoutSeconds`, 60 s by
default): a previous process of the node goes quiet and the start goes on; a second live process keeps beating and
this start is refused, naming the other's host and last heartbeat. Workers that today run with one shared `NodeId`
(a value in a configuration all replicas read) stop starting: leave `NodeId` empty so each takes its host name, or
give each its own. A worker restarted right after a crash may now wait up to `DeadNodeTimeoutSeconds` before it
registers; after a clean stop it does not wait.

**`Quartz:quartz.scheduler.instanceId` is `NODE` in the shipped configuration** (was `AUTO`): every context
scheduler of the worker takes the node id, which stays across restarts, so a crashed node takes its own fired
triggers back at its first check-in. A configuration of your own that sets `AUTO` or an explicit id keeps it. The
records the earlier `AUTO` ids left in `qrtz_scheduler_state` are harmless, no live scheduler reads them; delete them
by hand if you like, with the rows of every `qrtz_` table under scheduler names no worker uses any more.

### Quartz tables

The scheduler of every route context runs on Quartz 3.22 (was 3.16). Its corrections change when or whether a job
fires: a job with `[DisallowConcurrentExecution]` (a stateful cron route) is held to one run at a time across the
whole cluster, where it used to be held per node only; day-of-month `L`, `LW` and `W` expressions, fire times in the
repeated hour of a DST fall-back and day-based calendars near DST changes are corrected; pausing or resuming groups
by prefix covers every matching group. The trigger of a running job that does not run concurrently reads `Blocked`,
not `Complete`, in `GET /scheduled`, `tsak scheduler jobs` and the dashboard.

With a clustered job store the worker brings the Quartz tables up to 3.22 at start:

- `QRTZ_TRIGGERS.MISFIRE_ORIG_FIRE_TIME` (3.17): the time a misfired trigger was due, so a route's
  `CamelQuartzScheduledFireTime` is that time after a misfire under the fire-now policies;
- `EXECUTION_GROUP` on `QRTZ_TRIGGERS` and `QRTZ_FIRED_TRIGGERS` (3.18);
- `QRTZ_TRIGGERS.PREFERRED_NODE` and `PREFERRED_NODE_AUTO` (3.19): node affinity, which needs both.

New installations get them from the create scripts. On an existing one the worker adds the missing columns under a
lock (`pg_advisory_lock` on PostgreSQL, `sp_getapplock` on SQL Server, an immediate transaction on SQLite), so nodes
starting together add each column once; existing triggers keep their state.

**A database role that may not alter the Quartz tables** gets a warning naming the missing columns, and the worker
goes on without them, as before. Run the administrator's script once: it ships beside the worker in
`sql/quartz/quartz_3.22_{postgres,sqlServer,sqlite}.sql` (in the image and the archive). The script also carries the
Quartz 3.20 index alignment, which drops and creates indexes and which the worker never runs on its own. The
PostgreSQL and SQL Server scripts may run more than once; the SQLite one once, after checking `PRAGMA table_info`.
