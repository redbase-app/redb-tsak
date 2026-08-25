using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Tsak.Core.Audit;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Security;
using redb.Tsak.Core.Modules;
using redb.Tsak.Core.Services;
using redb.Tsak.Core.Services.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace redb.Tsak.Core.Services;

/// <summary>
/// Main hosted service managing the Tsak runtime lifecycle.
/// Startup: init coordinator → start _system context (REST API) → register static providers (batch) → scan dynamic modules → maintenance loop.
/// Lifecycle hooks (<see cref="ITsakLifecycleHook"/>) run before module discovery and after shutdown.
/// Hot reload: periodically scans for updated module DLLs via <see cref="HotReloadService"/>.
/// Shutdown: stop all running contexts (+ cluster deregistration).
/// </summary>
public class TsakHostedService : BackgroundService
{
    private readonly ILogger<TsakHostedService> _logger;
    private readonly ITsakModuleRegistry _registry;
    private readonly ITsakContextManager _contextManager;
    private readonly ITsakCoordinator _coordinator;
    private readonly IEnumerable<IStaticModuleProvider> _staticProviders;
    private readonly IConfiguration _configuration;
    private readonly IEnumerable<ITsakLifecycleHook> _lifecycleHooks;
    private readonly HotReloadService _hotReloadService;
    private readonly SharedAssemblyLoader _sharedLoader;
    private readonly SystemContextBuilder? _systemContextBuilder;
    private readonly IServiceProvider _serviceProvider;

    public TsakHostedService(
        ILogger<TsakHostedService> logger,
        ITsakModuleRegistry registry,
        ITsakContextManager contextManager,
        ITsakCoordinator coordinator,
        IEnumerable<IStaticModuleProvider> staticProviders,
        IConfiguration configuration,
        HotReloadService hotReloadService,
        SharedAssemblyLoader sharedLoader,
        IServiceProvider serviceProvider,
        IEnumerable<ITsakLifecycleHook> lifecycleHooks,
        SystemContextBuilder? systemContextBuilder = null)
    {
        _logger = logger;
        _registry = registry;
        _contextManager = contextManager;
        _coordinator = coordinator;
        _staticProviders = staticProviders;
        _configuration = configuration;
        _lifecycleHooks = lifecycleHooks;
        _hotReloadService = hotReloadService;
        _sharedLoader = sharedLoader;
        _systemContextBuilder = systemContextBuilder;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Tsak starting...");

        try
        {
            // 0. Sync Tsak-specific storage schemes
            // (RedbInitHostedService already called InitializeAsync before us)
            {
                using var scope = _serviceProvider.CreateScope();
                var redb = scope.ServiceProvider.GetService<IRedbService>();
                if (redb is not null)
                {
                    await redb.SyncSchemeAsync<TsakStateProps>();
                    await redb.SyncSchemeAsync<TsakModuleProps>();
                    await redb.SyncSchemeAsync<ApiKeyProps>();

                    var provider = _configuration["Tsak:Redb:Provider"] ?? "auto";
                    _logger.LogInformation("Tsak schemes synced (provider: {Provider})", provider);
                }
            }

            // Startup diagnostic summary
            LogStartupDiagnostics();

            // 1. Initialize coordinator (subscribes to registry events)
            _coordinator.Initialize(_registry);

            // 1a. Cleanup orphaned anonymous context states
            if (_contextManager is TsakContextManager tcm)
            {
                try { await tcm.CleanupAnonymousStatesAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Anonymous state cleanup failed"); }
            }

            // 1b. Run lifecycle hooks BEFORE module discovery
            // (e.g. cluster coordinator starts, acquires leadership, etc.)
            foreach (var hook in LifecycleHookOrdering.OrderAscending(_lifecycleHooks, _logger))
            {
                await hook.OnStartingAsync(stoppingToken).ConfigureAwait(false);
            }

            // 1c. Load shared assemblies (connectors, shared models) before any contexts
            {
                var sharedCount = _sharedLoader.LoadSharedAssemblies();
                if (sharedCount > 0)
                    _logger.LogInformation("Loaded {Count} shared assemblies from {Path}", sharedCount, _sharedLoader.SharedPath);
            }

            // 2. Start _system context (REST API) before module contexts
            if (_systemContextBuilder is not null)
            {
                try
                {
                    await _systemContextBuilder.BuildAndStartAsync(_registry, stoppingToken);
                }
                catch (Exception ex)
                {
                    // The node keeps running its business contexts, but with NO management/control-plane
                    // it is not fully operational — escalate loudly AND flag readiness (review item 4.1)
                    // so it is not silently reported healthy.
                    _logger.LogCritical(ex,
                        "Failed to start REST API (_system context) — node has NO control-plane; readiness will "
                        + "report UNHEALTHY. Business contexts continue.");
                    _serviceProvider.GetService<Monitoring.ControlPlaneHealth>()?.MarkFailed(ex.Message);
                }
            }

            // 2a. Retention sweeps (audit + DLQ) are mounted as cron:// routes on the _system context
            //     by SystemContextBuilder — no bare Quartz jobs here anymore.

            // 3. Register static providers (fires batch event → coordinator creates contexts)
            var staticCount = await _registry.RegisterStaticProvidersAsync(_staticProviders);
            _logger.LogInformation("Registered {Count} static modules", staticCount);

            // 4. Scan dynamic modules from configured paths
            var assemblyPaths = _configuration.GetSection("Tsak:Modules:AssemblyPaths").Get<string[]>();
            var collectible = _configuration.GetValue("Tsak:HotReload:Collectible", false);
            if (assemblyPaths is { Length: > 0 })
            {
                // Register centralized tracker resolver so byte-loaded assemblies
                // are found by all ALCs (packages, bare DLLs, shared layer).
                LoadedAssemblyTracker.EnsureResolverRegistered();

                int dynamicCount;
                if (_registry is TsakModuleRegistry concreteRegistry)
                {
                    dynamicCount = await concreteRegistry.DiscoverModulesAsync(assemblyPaths,
                        probePaths: assemblyPaths, collectible: collectible);

                    // Hand off startup-loaded packages to HotReloadService (takes ALC ownership)
                    if (concreteRegistry.LoadedPackages.Count > 0)
                        _hotReloadService.AdoptStartupPackages(concreteRegistry.LoadedPackages);
                }
                else
                {
                    dynamicCount = await _registry.DiscoverModulesAsync(assemblyPaths);
                }

                _logger.LogInformation("Discovered {Count} dynamic module changes", dynamicCount);
            }

            _logger.LogInformation("Tsak running with {Modules} modules, {Contexts} contexts",
                _registry.GetAllModules().Count,
                _contextManager.GetAllContexts().Count);

            // 5. Periodic hot-reload loop (only if enabled in config)
            var hotReloadEnabled = _configuration.GetValue("Tsak:HotReload:Enabled", true);
            var scanInterval = _configuration.GetValue("Tsak:HotReload:ScanIntervalSeconds", 30);

            if (hotReloadEnabled && scanInterval > 0 && assemblyPaths is { Length: > 0 })
            {
                _logger.LogInformation("Hot-reload enabled, scanning every {Interval}s", scanInterval);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(scanInterval), stoppingToken);
                    try
                    {
                        var reloaded = await _hotReloadService.ScanAndReloadAsync(assemblyPaths, stoppingToken);
                        if (reloaded > 0)
                            _logger.LogInformation("Hot-reloaded {Count} modules", reloaded);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during periodic module scan");
                    }
                }
            }
            else
            {
                _logger.LogInformation("Hot-reload disabled, running with startup modules only");
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Tsak fatal error");
            throw;
        }

        _logger.LogInformation("Tsak stopped");
    }

    private void LogStartupDiagnostics()
    {
        // Database connection
        var provider = _configuration["Tsak:Redb:Provider"]?.ToLowerInvariant() ?? "postgres";
        var connKey = provider switch
        {
            "mssql" or "sqlserver" => "MSSql",
            "sqlite" => "Sqlite",
            _ => "Postgres"
        };
        var connStr = _configuration.GetConnectionString(connKey) ?? "";
        var safeConnStr = SanitizeConnectionString(connStr);
        var tier = _configuration.GetValue<bool>("Tsak:Redb:UsePro") ? "Pro" : "Free";
        _logger.LogInformation("Database: provider={Provider}, tier={Tier}, connection={Connection}",
            provider, tier, safeConnStr);

        // Quartz mode
        var jobStoreType = _configuration["Quartz:quartz.jobStore.type"] ?? "RAMJobStore";
        var isAdoJobStore = jobStoreType.Contains("AdoJobStore", StringComparison.OrdinalIgnoreCase);
        var isRamJobStore = jobStoreType.Contains("RAMJobStore", StringComparison.OrdinalIgnoreCase);
        if (!isAdoJobStore && !isRamJobStore)
        {
            _logger.LogWarning(
                "Unknown Quartz jobStore.type '{JobStoreType}'. Recognized values are 'RAMJobStore' " +
                "and any AdoJobStore variant (e.g. 'JobStoreTX'). Treating as non-persistent.",
                jobStoreType);
        }
        var clustered = _configuration.GetValue<bool>("Quartz:quartz.jobStore.clustered");
        var instanceId = _configuration["Quartz:quartz.scheduler.instanceId"] ?? "NON_CLUSTERED";

        _logger.LogInformation(
            "Quartz: store={JobStore}, persistence={Persistence}, clustered={Clustered}, instanceId={InstanceId}",
            isAdoJobStore ? "AdoJobStore" : "RAMJobStore",
            isAdoJobStore,
            clustered,
            instanceId);

        // Tsak cluster
        var tsakCluster = _configuration.GetValue<bool>("Tsak:Cluster:Enabled");
        var strategy = _configuration["Tsak:Cluster:Strategy"] ?? "round-robin";
        var clusterName = _configuration["Tsak:Cluster:ClusterName"] ?? "default";
        var groupName = _configuration["Tsak:Cluster:GroupName"] ?? "default";
        if (tsakCluster)
        {
            _logger.LogInformation(
                "Tsak cluster: enabled=True, cluster={ClusterName}, group={GroupName}, strategy={Strategy}",
                clusterName, groupName, strategy);
        }
        else
        {
            _logger.LogInformation("Tsak cluster: enabled=False (single-node mode)");
        }
    }

    private static string SanitizeConnectionString(string connStr)
    {
        // Show host+database, mask credentials
        var parts = connStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>();
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim().ToLowerInvariant();
            if (key is "password" or "pwd")
                safe.Add($"{kv[0].Trim()}=***");
            else
                safe.Add(part.Trim());
        }
        return string.Join("; ", safe);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Tsak graceful shutdown, stopping all contexts...");

        // Run lifecycle hooks in reverse order (e.g. cluster deregistration)
        foreach (var hook in LifecycleHookOrdering.OrderDescending(_lifecycleHooks, _logger))
        {
            try
            {
                await hook.OnStoppingAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in lifecycle hook {Hook}", hook.GetType().Name);
            }
        }

        // Don't propagate host's cancellation token to context stops:
        // draining inflight exchanges in one context may exhaust the host
        // shutdown timeout, causing remaining contexts to fail with TaskCanceledException
        // on the lifecycle semaphore. Each context has its own internal drain timeout.
        foreach (var (name, _) in _contextManager.GetAllContexts())
        {
            try
            {
                await _contextManager.StopContextAsync(name, setAutoStartFalse: false, ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping context {Context}", name);
            }
        }

        await base.StopAsync(cancellationToken);
    }
}
