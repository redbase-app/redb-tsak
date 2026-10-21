# redb.Tsak changelog: 4.1.x

Releases of the 4.1 line, moved here unchanged from CHANGELOG.md, which keeps the latest released line only.
Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [4.1.1] — 2026-09-25
### Fixed — a hot reload no longer leaves a facade taking requests its backend cannot serve

A package reload recreated its contexts one at a time — stop, create, initialize, start, then the next — in
dependency order. With "the facade depends on the core" declared, the core went first, and for the whole of its
recreation the old facade kept accepting requests and forwarding them into a context that no longer existed;
without the dependency declared, the order was arbitrary, and a new facade could come up before its core. The
contexts of a reload are now replaced as a set: all of them stop in reverse dependency order, facades first,
and then start in dependency order, facades last. For the length of a reload the set is plainly unavailable
instead of half-alive, as Camel stops routes in the reverse of their startup order. The locks of the whole set
are held throughout, so nothing recreates one of its contexts in between.

### Fixed — a module whose name is already taken is refused, instead of silently replacing the one that holds it

Module names are compared without regard to case, and during the startup scan a name met a second time was
treated as an update: the newcomer took the place of the module holding the name, that module was disposed,
and the log said "Updated module". The scan runs once — a hot swap goes through its own path — so a name met
twice is always two modules claiming it. A package's XML module, named after its manifest, destroyed the code
module that shared its name; the same package dropped into two scanned folders replaced itself. The second
claimant is now refused with an error naming both sources, and the module registered first keeps running.
The node itself does not stop, the same way a module that fails to initialize cannot stop it. Bare DLLs keep
their deliberate rule that a newer version of the same module wins, which the log already reports as such.

## [4.1.0] — 2026-09-21
### Fixed — a table no longer jumps back to its first page whenever the page around it redraws

Every grid in the dashboard returned to page one each time its page set parameters, which happens on every
refresh tick, every toast and every field change. Paging through a long list was therefore impossible on a
page that refreshes itself: a second later the reader was back at the first rows. Only a change of the filter
starts over now; otherwise the current page is kept, clamped to what the data still has.

A page size the reader picks is reported to the page holding the grid, so it survives the grid being
re-created — which is what happens whenever a page swaps the data it shows.

### Added — a Storage page: what the indexes of every database look like, and a button to refresh the statistics

The node now has a Storage tab of its own, over the maintenance surface of redb.Core. It lists the databases
this node uses — Tsak's own, plus every one a context declares in its `Redb` section — and for the selected
one shows its tables with their sizes, row estimates and when their statistics were last refreshed, and its
indexes with their columns, size and read counters. An admin can refresh the planner statistics of one table
or of the whole database; the action is confirmed, audited, and runs on the control client because on a large
database it takes minutes.

The list is of databases, not of declarations: five contexts of one module declaring the same three databases
would otherwise be fifteen near-identical rows. Declarations are grouped by the database they address — a
fingerprint the node computes over the connection string, which itself never leaves the node — and the
contexts behind each one are named on the page. Never by name: two contexts may use one name for different
databases. Until Read is pressed the page says what pressing it will do, and a refusal from the engine stays
on the page with the reference the worker log is searchable by. A node that does not answer at all is shown as
not answering, with the message and a retry, rather than as a node that uses no database.

Both tables sort by any column — a second click reverses it — and offer 20, 50, 100 or 200 rows per page. The
sorting was already written into the shared grid and had never been wired to a page; the choice of page size
is new and any grid can offer it, so pages that do not ask for it are unchanged.

What a database answered is kept while the page is open, with the moment it was read: coming back to a
database already read shows that reading instead of asking for Read again. Only what was asked for is kept —
a database never read is still never connected to.

Three things the page refuses to get wrong. An index that holds a primary key, a uniqueness constraint or a
foreign key — and every index of redb's own tables — is shown with what it holds and never appears among the
remarks: it reads zero because it enforces something, not because nobody needs it. The window the usage
counters cover is stated above the numbers, and when the engine does not say since when it has been counting,
nothing is called unread at all. Columns the engine cannot answer are not drawn rather than drawn empty, which
is decided by the provider — SQLite has no schemas, no usage counters and no record of an analyze — so an
empty PostgreSQL database does not lose its columns for looking unused.

Beyond the engine's own numbers the page names duplicate indexes and indexes whose columns are the leading
part of a wider one, both computed from the column lists the engine reports.

Statistics are read on request, never on the page's refresh timer: an instance a context declared but never
activated opens its database connection on exactly that call.

### Fixed — the sidebar's node entry opens the overview instead of leaving the open tab on screen

The sidebar links a node without a tab — `node/{id}` — and the page only changed tabs when the URL named one.
Clicking Overview from any other tab therefore navigated but changed nothing visible, and the tab had to be
picked again in the strip. A URL with no tab now means the first tab, and a URL naming no tab of the page
means the same rather than nothing.

### Added — a node opens on an overview of what needs attention, not on its list of contexts

The node page started on the Contexts tab, so the sidebar's "Overview" and "Contexts" led to the same view and
nothing on the node answered "what is wrong right now". The first tab is now Overview.

"Needs attention" gathers the watchdog's active alerts, the routes that reported errors, the routes shedding at
their admission limit, the stopped contexts and every module that is not running. A shedding route is listed as
a warning and never as an error: it is configured correctly and under-provisioned, and the answer is a limit or
another node, not a bug hunt. A row about a route carries a button that opens that route. An empty list says so
in one line instead of showing an empty table.

Under it: in-flight now, contexts running, the five busiest routes and the last fifteen lifecycle events. The
tab costs three API calls per refresh, and the node's health cards above the tabs were already loaded.

`RouteMetricsSummary` now carries `ContextName`. A route id is unique only inside its context, so without it
neither this page nor the cluster dashboard could turn a route row into a link to that route.

### Fixed — the dashboard opens the detail page of a route whose id came from its from-URI

A route that is never given an id through `.RouteId(...)` gets one derived from its from-URI, with the
slashes, `?` and `&` of that URI in it. The routes table put that id straight into the link path, no page
template matched such a path, and every one of those rows opened on "Page not found". The id now travels in
the query string; the detail page reads it from there and keeps the path form for ids that are plain words,
so links made earlier still work. Red-before: an anonymous request to the new link answered 404 instead of
the login challenge that proves the router found the page.

The management API's own route was one of those rows — it read as a line of URI noise,
`http://127.0.0.1:9090/{**path}?host=…&inOut=true&port=…`, and its buttons could do nothing. It now carries
the explicit id `system-api`, and a test requires every route of the system context to have an id the routes
API can address. Managing a route whose id does contain a slash is a separate matter and still fails: such an
id is not a legal path segment, so start, stop and detail answer 404. A context now names every route of that
kind in a warning as it starts, with what to do about it — give the route an explicit id. redb.Route has since
stopped deriving ids from the URI at all (they are now the scheme, the path and a UUID), so the case is left to
ids someone asks for by hand; the link and the warning cover it either way.

### Fixed — the route buttons say what they do before they are clicked

Start, Stop and Force-Stop carry tooltips, in the routes table and on the route detail page. The force-stop
button in the table has an icon and no label at all, so the difference between stopping a route and cancelling
its in-flight exchanges was visible only in the confirmation dialog — after the click.

### Fixed — an assembly the host already provides is not loaded a second time from a module package

A module library takes `Microsoft.Extensions.*` (and anything else outside its target framework) from NuGet,
while the worker resolves those very names from the `Microsoft.AspNetCore.App` shared framework. Every package
carrying such a file made `LoadedAssemblyTracker.LoadOrReuse` byte-load a second instance into the Default
context: nothing ever bound to it — `ModuleAssemblyLoadContext.Load` asks the host first, so the host's copy
wins every resolution — and the Default context never unloads, so the copy stayed for the life of the process.
`LoadOrReuse` now asks the host first, exactly as the module load context does, and loads the bytes only for a
name the host cannot resolve; the reuse is logged per assembly. Behaviour is unchanged — the host's copy was
already the one in use — the duplicate is simply no longer created. Pinned by a red-before test that hands the
tracker a different assembly under a name the host provides and requires the host's instance back.

Packaging note for module authors: a package should not carry assemblies of the shared frameworks at all. The
exclusion belongs in the packer and must be decided by the framework, not by what happens to be in the
worker's `Libs/shared` — that layer stopped carrying framework assemblies in 4.0.1.
