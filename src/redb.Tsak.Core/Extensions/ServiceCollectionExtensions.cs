using Quartz;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using redb.Core.Extensions;
using redb.Core.Pro.Extensions;
using redb.Postgres.Pro.Extensions;
using redb.MSSql.Pro.Extensions;
using redb.SQLite.Pro.Extensions;   // UseSqlite is tier-agnostic (AddRedb → Free, AddRedbPro → Pro)
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Monitoring;
using redb.Tsak.Core.Modules;
using redb.Tsak.Core.Services;
using redb.Tsak.Core.Security;
using redb.Tsak.Core.Services.Storage;
using redb.Core.Models.Configuration;
using redb.Tsak.Core.Quartz;
using redb.Route.Http;
using redb.Route.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace redb.Tsak.Core.Extensions;

/// <summary>
/// DI registration for Tsak services.
/// Reads Tsak:Storage and Tsak:Redb config sections to configure storage provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register all Tsak services into the DI container.
    /// Storage mode is determined by Tsak:Storage:Type (InMemory | Redb).
    /// </summary>
    public static IServiceCollection AddTsak(this IServiceCollection services, IConfiguration configuration)
    {
        ConfigureRedb(services, configuration);
        ConfigureStorage(services, configuration);

        services.AddSingleton<ITsakModuleRegistry, TsakModuleRegistry>();
        services.AddSingleton<ITsakContextManager, TsakContextManager>();
        services.AddSingleton<ITsakCoordinator, TsakCoordinator>();

        // Shared registry for cross-context direct-vm / vm components
        services.TryAddSingleton<redb.Route.Components.SharedVmRegistry>();

        // REST API (_system context) — HTTP component + builder
        services.AddRedbRouteHttp();
        services.AddSingleton<SystemContextBuilder>();

        // Admin audit (default sink: structured WRN log; Pro can replace with redb-backed sink)
        services.AddTsakAdminAudit();

        // Watchdog alert delivery (all channels off by default; configured via Tsak:Watchdog:Alerts)
        services.AddTsakAlerts(configuration);

        // Dead-letter queue (capture failed exchanges at route checkpoints + replay).
        services.AddSingleton<Dlq.DlqService>();
        services.AddSingleton<IHostedService, Dlq.DlqSchemaInitializer>();
        // Retention runs as a cron:// route on the _system context (SystemContextBuilder), not a job.

        // Module deployment (upload/rollback). Disabled by default — an RCE-capable surface.
        var uploadOptions = new Modules.ModuleUploadOptions();
        configuration.GetSection("Tsak:Modules:Upload").Bind(uploadOptions);
        var moduleSigOptions = new Modules.ModuleSignatureOptions();
        configuration.GetSection("Tsak:Modules:Signature").Bind(moduleSigOptions);
        services.AddSingleton(uploadOptions);
        services.AddSingleton(moduleSigOptions);
        services.AddSingleton<Modules.ModuleUploadService>();

        ConfigureHotReload(services, configuration);
        ConfigureMonitoring(services, configuration);
        ConfigureQuartz(services, configuration);

        // Cluster (Pro) is registered separately via redb.Tsak.Core.Pro.Extensions.AddTsakCluster()
        // because it lives in the Pro assembly which depends on this Core library.

        services.AddHostedService<TsakHostedService>();
        return services;
    }

    private static void ConfigureStorage(IServiceCollection services, IConfiguration configuration)
    {
        var storageType = configuration["Tsak:Storage:Type"] ?? "InMemory";

        if (storageType.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ITsakModuleStore, InMemoryTsakModuleStore>();
            services.AddSingleton<ITsakStateStore, InMemoryTsakStateStore>();
            services.AddSingleton<IApiKeyStore, ConfigApiKeyStore>();

            // Warn: InMemory state store loses route:suspended:* on restart — clustered routes
            // won't respect previously suspended state after a process restart.
            if (configuration.GetValue<bool>("Tsak:Cluster:Enabled"))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine(
                    "[WARN] Cluster is enabled but Storage:Type is InMemory. "
                    + "Route suspension state will be LOST on process restart. "
                    + "Consider Storage:Type=Redb for persistent state.");
                Console.ResetColor();
            }
        }
        else if (storageType.Equals("Redb", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ITsakModuleStore, RedbTsakModuleStore>();
            services.AddSingleton<ITsakStateStore, RedbTsakStateStore>();
            services.AddSingleton<IApiKeyStore, RedbApiKeyStore>();
        }
    }

    private static void ConfigureRedb(IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Tsak:Redb:Provider"]?.ToLowerInvariant();
        if (string.IsNullOrEmpty(provider))
            return;
        // Default to Pro (the Pro redb tier is free): absent Tsak:Redb:UsePro ⇒ Pro. Set it to false
        // explicitly to run the Free tier.
        var usePro = configuration.GetValue("Tsak:Redb:UsePro", true);
        var connectionString = provider switch
        {
            "mssql" or "sqlserver" => configuration.GetConnectionString("MSSql"),
            "sqlite" => configuration.GetConnectionString("Sqlite"),
            _ => configuration.GetConnectionString("Postgres")
        };

        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException(
                $"Connection string for provider '{provider}' is not configured. " +
                $"Set ConnectionStrings:{(provider switch { "mssql" or "sqlserver" => "MSSql", "sqlite" => "Sqlite", _ => "Postgres" })} in appsettings.json");

        var strategy = Enum.TryParse<PropsSaveStrategy>(configuration["Tsak:Redb:PropsSaveStrategy"], true, out var parsed)
            ? parsed
            : PropsSaveStrategy.DeleteInsert;

        // The Tsak license (tsak.cluster, tsak.web.pro, ...) is INDEPENDENT of the
        // redb database tier. UsePro only selects Free (AddRedb) vs Pro (AddRedbPro)
        // redb providers; the license must land in the shared LicenseStore either
        // way, so Tsak Pro features (multi-node cluster, web dashboard) work even on
        // a Free redb database.
        var license = LicenseConfigReader.Read(configuration, "Tsak:Redb:License");

        if (usePro)
        {
            services.AddRedbPro(options =>
            {
                if (provider is "mssql" or "sqlserver")
                    options.UseMsSql(connectionString);
                else if (provider is "sqlite")
                    options.UseSqlite(connectionString);
                else
                    options.UsePostgres(connectionString);

                if (!string.IsNullOrEmpty(license))
                    options.WithLicense(license);   // registers the license (redb Pro + LicenseStore)

                options.Configure(c =>
                {
                    c.PropsSaveStrategy = strategy;
                    c.EnsureCreated = true;
                    ApplyCacheConfig(c, configuration);
                });
            });
        }
        else
        {
            // Free redb DB, but still honour the Tsak license so tsak.cluster /
            // tsak.web.pro unlock — UsePro governs only the redb DB tier.
            if (!string.IsNullOrEmpty(license))
                redb.Licensing.LicenseStore.AddLicense(license);

            services.AddRedb(options =>
            {
                if (provider is "mssql" or "sqlserver")
                    options.UseMsSql(connectionString);
                else if (provider is "sqlite")
                    options.UseSqlite(connectionString);
                else
                    options.UsePostgres(connectionString);

                options.Configure(c =>
                {
                    c.PropsSaveStrategy = strategy;
                    c.EnsureCreated = true;
                    ApplyCacheConfig(c, configuration);
                });
            });
        }
    }

    /// <summary>
    /// Maps the optional <c>Tsak:Redb:Cache</c> section onto the redb configuration. Every key is
    /// optional and defaults to redb's own default, so an absent section changes nothing.
    /// <para>
    /// <b>Safety note:</b> <c>SkipHashValidationOnCacheCheck=true</c> trusts the in-process cache
    /// without re-checking the object hash in the database. That is correct only for a single writer.
    /// In a Tsak cluster (multiple nodes writing to the same database) it can serve stale data, so we
    /// log a warning if both are on.
    /// </para>
    /// </summary>
    private static void ApplyCacheConfig(redb.Core.Models.Configuration.RedbServiceConfiguration c, IConfiguration configuration)
    {
        var cache = configuration.GetSection("Tsak:Redb:Cache");
        if (!cache.Exists()) return;

        // Props cache
        if (cache["EnableProps"] is { } ep && bool.TryParse(ep, out var enableProps)) c.EnablePropsCache = enableProps;
        if (cache.GetValue<int?>("PropsMaxSize") is { } pms) c.PropsCacheMaxSize = pms;
        if (cache.GetValue<int?>("PropsTtlMinutes") is { } ptm) c.PropsCacheTtl = TimeSpan.FromMinutes(ptm);
        if (cache["SkipHashValidationOnCacheCheck"] is { } sh && bool.TryParse(sh, out var skipHash))
            c.SkipHashValidationOnCacheCheck = skipHash;

        // List cache
        if (cache["EnableList"] is { } el && bool.TryParse(el, out var enableList)) c.EnableListCache = enableList;
        if (cache.GetValue<int?>("ListTtlMinutes") is { } ltm) c.ListCacheTtl = TimeSpan.FromMinutes(ltm);

        // Metadata cache
        if (cache["EnableMetadata"] is { } em && bool.TryParse(em, out var enableMeta)) c.EnableMetadataCache = enableMeta;
        if (cache.GetValue<int?>("MetadataTtlMinutes") is { } mtm) c.MetadataCacheLifetimeMinutes = mtm;

        // Hash recompute on save + cache domain isolation
        if (cache["AutoRecomputeHash"] is { } ar && bool.TryParse(ar, out var autoHash)) c.AutoRecomputeHash = autoHash;
        if (!string.IsNullOrWhiteSpace(cache["CacheDomain"])) c.CacheDomain = cache["CacheDomain"];

        // Cluster + skip-hash is a stale-read hazard.
        if (c.SkipHashValidationOnCacheCheck && configuration.GetValue<bool>("Tsak:Cluster:Enabled"))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine(
                "[WARN] Tsak:Redb:Cache:SkipHashValidationOnCacheCheck=true with Tsak:Cluster:Enabled=true. " +
                "Skipping cache hash validation can serve STALE data across cluster nodes writing to the same " +
                "database. Set it to false in clustered deployments.");
            Console.ResetColor();
        }
    }

    private static void ConfigureMonitoring(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Tsak:Metrics");
        services.Configure<MetricsOptions>(section);

        services.AddSingleton<MetricsService>();
        services.AddSingleton<ContextMetricsCollector>();
        services.AddSingleton<ContextInfoCollector>();
        services.AddSingleton<LifecycleAuditService>();
        services.AddSingleton<HealthCheckService>();
        // Control-plane health (review item 4.1): if the _system context fails to start, readiness must
        // report the node as unhealthy instead of silently serving as if fully operational.
        services.AddSingleton<ControlPlaneHealth>();
        services.AddSingleton<IHealthContributor, ControlPlaneHealthContributor>();
        services.AddHostedService<MetricsCollectionService>();

        // Log ring buffer — in-memory circular buffer for live log viewing
        var logBufferSize = configuration.GetValue<int?>("Tsak:Logs:BufferSize") ?? 2000;
        var logBuffer = new LogRingBuffer(logBufferSize);
        services.AddSingleton(logBuffer);
        services.AddSingleton(new RingBufferSink(logBuffer));

        // Route watchdog — detects hung exchanges
        var watchdogSection = configuration.GetSection("Tsak:Watchdog");
        services.Configure<WatchdogOptions>(watchdogSection);
        services.AddSingleton<RouteWatchdogService>();
        services.AddHostedService(sp => sp.GetRequiredService<RouteWatchdogService>());

        // OpenTelemetry pipeline.
        //   • Metrics → Prometheus exporter (Grafana)  — Tsak:Metrics:Prometheus:Enabled
        //   • Traces  → OTLP exporter (Jaeger / collector) — Tsak:Tracing:Otlp:Enabled
        // Either toggle (or both) activates the shared pipeline; both default to off.
        var prometheusEnabled = configuration.GetValue<bool>("Tsak:Metrics:Prometheus:Enabled");
        var otlpEnabled = configuration.GetValue<bool>("Tsak:Tracing:Otlp:Enabled");
        if (prometheusEnabled || otlpEnabled)
        {
            // Modules hosted on top of Tsak (e.g. redb.Identity) can expose their own
            // Meters and ActivitySources without taking a runtime dependency on Tsak —
            // they advertise the names via configuration and Tsak subscribes them into
            // the OTel pipeline on startup. Keeps Tsak.Core module-agnostic.
            // Example:
            //   "Tsak": { "Metrics": { "Prometheus": {
            //       "AdditionalMeters": [ "RedbIdentity" ],
            //       "AdditionalSources": [ "RedbIdentity" ]
            //   } } }
            var additionalMeters = configuration
                .GetSection("Tsak:Metrics:Prometheus:AdditionalMeters")
                .Get<string[]>() ?? Array.Empty<string>();
            var additionalSources = configuration
                .GetSection("Tsak:Metrics:Prometheus:AdditionalSources")
                .Get<string[]>() ?? Array.Empty<string>();

            var otel = services.AddOpenTelemetry();

            // Resource service.name — what shows up in Jaeger / the collector. Configurable
            // so operators can distinguish nodes; defaults to "redb-tsak-worker" (without it
            // OTel reports the ugly "unknown_service:redb.Tsak.Worker").
            var serviceName = configuration.GetValue<string>("Tsak:Tracing:ServiceName");
            if (string.IsNullOrWhiteSpace(serviceName)) serviceName = "redb-tsak-worker";
            otel.ConfigureResource(r => r.AddService(serviceName));

            if (prometheusEnabled)
            {
                var port = configuration.GetValue<int?>("Tsak:Metrics:Prometheus:Port") ?? 9464;
                // The OTel scrape listener binds LOOPBACK only (localhost) — that needs no admin
                // and no URL ACL on any OS (the Windows wildcard-bind footgun is gone). It is an
                // internal endpoint: the public scrape target is the Tsak facade's `/metrics`
                // route (Kestrel, the Api port), which proxies to this loopback listener. See
                // SystemContextBuilder. Keeping it on loopback also means it is never exposed
                // unauthenticated on an external interface by accident.
                var prefix = $"http://localhost:{port}/";

                // Pre-flight the bind: an optional metrics exporter must NEVER crash the host.
                // (Loopback almost always binds; this just guarantees graceful degradation.)
                if (CanBindHttpListener(prefix))
                {
                    otel.WithMetrics(m =>
                    {
                        m.AddMeter(RouteMetrics.MeterName);
                        foreach (var meter in additionalMeters)
                        {
                            if (!string.IsNullOrWhiteSpace(meter))
                                m.AddMeter(meter);
                        }
                        m.AddRuntimeInstrumentation()
                         .AddProcessInstrumentation()
                         .AddPrometheusHttpListener(o => o.UriPrefixes = [prefix]);
                    });
                }
            }

            // Tracing sources are always subscribed when the pipeline is up; the OTLP
            // exporter is attached only when Tsak:Tracing:Otlp:Enabled. Jaeger ingests
            // OTLP natively (gRPC :4317 / HTTP :4318) — point Endpoint at the collector.
            otel.WithTracing(t =>
            {
                t.AddSource(RouteActivitySource.SourceName);
                foreach (var source in additionalSources)
                {
                    if (!string.IsNullOrWhiteSpace(source))
                        t.AddSource(source);
                }

                if (otlpEnabled)
                {
                    var endpoint = configuration.GetValue<string>("Tsak:Tracing:Otlp:Endpoint")
                                   ?? "http://localhost:4317";
                    var protocol = configuration.GetValue<string>("Tsak:Tracing:Otlp:Protocol");
                    t.AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(endpoint);
                        if (string.Equals(protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase))
                            o.Protocol = OtlpExportProtocol.HttpProtobuf;
                    });
                }
            });
        }
    }

    /// <summary>
    /// Probes whether an <see cref="System.Net.HttpListener"/> can claim <paramref name="prefix"/>.
    /// Returns false (and logs an actionable warning to stderr) instead of letting a bind failure
    /// propagate and take down hosting — the Prometheus exporter is optional infrastructure.
    /// </summary>
    private static bool CanBindHttpListener(string prefix)
    {
        // Retry briefly: on a restart the previous Worker may still hold the port for a
        // moment (HTTP.sys releases the binding slightly after the old process exits), which
        // would otherwise make the new instance silently skip metrics for that run.
        const int attempts = 5;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var listener = new System.Net.HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();
                listener.Stop();
                listener.Close();
                return true;
            }
            catch (Exception ex)
            {
                if (attempt < attempts)
                {
                    System.Threading.Thread.Sleep(700);
                    continue;
                }
                Console.Error.WriteLine(
                    $"[Tsak] Prometheus exporter DISABLED — cannot bind HttpListener '{prefix}' after " +
                    $"{attempts} attempts. The Worker continues without metrics (an optional exporter " +
                    $"must never crash the host). Reason: {ex.Message}");
                return false;
            }
        }
        return false;
    }

    private static void ConfigureHotReload(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Tsak:HotReload");
        services.Configure<HotReloadOptions>(section);

        // Shared assembly loader — loads connectors and shared models from Libs/shared/
        services.AddSingleton<SharedAssemblyLoader>();

        // Always register — needed for initial module loading via ALC.
        // Periodic scanning is controlled by HotReload:Enabled at runtime.
        services.AddSingleton<HotReloadService>();
    }

    private static void ConfigureQuartz(IServiceCollection services, IConfiguration configuration)
    {
        var quartzSection = configuration.GetSection("Quartz");
        if (quartzSection.Exists())
        {
            services.Configure<QuartzOptions>(quartzSection);

            // Auto-inject connection string and driver delegate from redb config
            // when AdoJobStore is used without explicit Quartz data source settings
            var jobStoreType = configuration["Quartz:quartz.jobStore.type"] ?? "";
            if (jobStoreType.Contains("AdoJobStore", StringComparison.OrdinalIgnoreCase))
            {
                var explicitCs = configuration["Quartz:quartz.dataSource.default.connectionString"];
                if (string.IsNullOrEmpty(explicitCs))
                {
                    var provider = configuration["Tsak:Redb:Provider"]?.ToLowerInvariant();
                    // Per-provider Quartz AdoJobStore mapping — same set of providers as redb storage.
                    var (csName, qProvider, qDelegate) = provider switch
                    {
                        "mssql" or "sqlserver" => ("MSSql",  "SqlServer",        "Quartz.Impl.AdoJobStore.SqlServerDelegate, Quartz"),
                        "sqlite"               => ("Sqlite", "SQLite-Microsoft", "Quartz.Impl.AdoJobStore.SQLiteDelegate, Quartz"),
                        _                      => ("Postgres", "Npgsql",         "Quartz.Impl.AdoJobStore.PostgreSQLDelegate, Quartz"),
                    };
                    var connStr = configuration.GetConnectionString(csName);

                    if (!string.IsNullOrEmpty(connStr))
                    {
                        services.PostConfigure<QuartzOptions>(opts =>
                        {
                            opts["quartz.dataSource.default.connectionString"] = connStr;
                            opts["quartz.dataSource.default.provider"] = qProvider;
                            opts.TryAdd("quartz.jobStore.dataSource", "default");
                            opts.TryAdd("quartz.jobStore.tablePrefix", "QRTZ_");
                            opts.TryAdd("quartz.serializer.type", "newtonsoft");
                            opts.TryAdd("quartz.jobStore.driverDelegateType", qDelegate);
                        });
                    }
                }
            }
        }
        else
        {
            // No `Quartz` config section — still stand up a shared IN-MEMORY scheduler. Tsak must ALWAYS
            // hand out one IScheduler so every RouteContext resolves the SAME instance: the `_system`
            // management API context (which backs the dashboard scheduler page) AND the business contexts
            // running cron routes. Without this, a cron consumer finds no injected scheduler and
            // self-creates a per-context RAMJobStore (see redb.Route.Quartz QuartzConsumerBase) that the
            // `_system` context cannot see — so the dashboard shows no jobs even though the route runs.
            // RAMJobStore is Quartz's default; set it explicitly for clarity (non-persistent, single node).
            services.Configure<QuartzOptions>(opts =>
                opts.TryAdd("quartz.jobStore.type", "Quartz.Simpl.RAMJobStore, Quartz"));
        }

        // Schema initializer must run before QuartzHostedService validates tables (it no-ops unless
        // AdoJobStore is configured). Hosted services start sequentially in registration order.
        services.AddSingleton<IHostedService, QuartzSchemaInitializer>();

        services.AddQuartz();
        services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

        // Register the IScheduler singleton ALWAYS (RAM by default, AdoJobStore/DB when configured) so
        // TsakContextManager injects one shared scheduler into every context — including `_system`.
        services.AddSingleton(provider =>
            provider.GetRequiredService<ISchedulerFactory>()
                .GetScheduler().GetAwaiter().GetResult());
    }
}
