using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Monitoring;
using redb.Tsak.Core.Modules;
using redb.Route.Extensions;

using redb.Tsak.Core.Controllers;
using redb.Tsak.Core.Contracts;
using redb.Route.RedbCore.Extensions;

namespace redb.Tsak.Core.Services;

/// <summary>
/// Manages lifecycle of route contexts. Supports named (config-driven) and anonymous (dynamic) contexts.
/// Based on lt.tsak RouteContextManager, adapted for redb.Route.
/// </summary>
public class TsakContextManager : ITsakContextManager
{
    private readonly ConcurrentDictionary<string, IRouteContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITsakStateStore _stateStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TsakContextManager> _logger;
    private readonly LifecycleAuditService? _auditService;
    private readonly SharedAssemblyLoader? _sharedLoader;
    private readonly IEnumerable<IContextCustomizer> _contextCustomizers;

    // Serializes lifecycle operations (start/stop/restart/remove) to prevent
    // races between API calls and hot-reload context recreation.
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    // Mini-DI containers for named redb instances, keyed by context name.
    // Must be disposed when context is removed.
    private readonly ConcurrentDictionary<string, List<ServiceProvider>> _namedRedbContainers = new(StringComparer.OrdinalIgnoreCase);

    private const string AutoStartKeyPrefix = "context:autostart:";

    public TsakContextManager(
        ITsakStateStore stateStore,
        IConfiguration configuration,
        ILogger<TsakContextManager> logger,
        IEnumerable<IContextCustomizer> contextCustomizers,
        LifecycleAuditService? auditService = null,
        SharedAssemblyLoader? sharedLoader = null)
    {
        _stateStore = stateStore;
        _configuration = configuration;
        _logger = logger;
        _contextCustomizers = contextCustomizers;
        _auditService = auditService;
        _sharedLoader = sharedLoader;
    }

    public IRouteContext CreateContext(string contextName, IServiceProvider serviceProvider, IDictionary<string, object?> configuration)
    {
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var context = new RouteContext(serviceProvider, contextName, loggerFactory);

        // Register shared components (connectors, shared models) from Libs/shared/
        if (_sharedLoader is { LoadedAssemblies.Count: > 0 })
            context.AddComponents(_sharedLoader.LoadedAssemblies.ToArray());

        // Inject IScheduler from DI if available (like lt.tsak pattern)
        try
        {
            var scheduler = serviceProvider.GetService<IScheduler>();
            if (scheduler != null)
            {
                context.AddService(typeof(IScheduler), scheduler);
                _logger.LogInformation("IScheduler injected into context {Context}", contextName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject IScheduler into context {Context}", contextName);
        }

        // Run context customizers (e.g. cluster policy factory registration)
        foreach (var customizer in _contextCustomizers)
        {
            try
            {
                customizer.CustomizeAsync(context, serviceProvider, contextName)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Context customizer {Customizer} failed for {Context}",
                    customizer.GetType().Name, contextName);
            }
        }

        foreach (var (key, value) in configuration)
        {
            if (value != null)
                context.SetProperty(key, value);
        }

        // Register named IRedbService instances from "Redb" config section
        RegisterNamedRedbServices(context, contextName, configuration);

        // Wire route-level state persistence callback
        context.OnBeforeRoutesStart = async routes =>
        {
            // Load suspended keys: routes manually stopped should remain stopped
            var suspendedPrefix = RoutesController.RouteSuspendedKeyPrefix + contextName + ":";
            var suspended = await _stateStore.GetByPrefixAsync(suspendedPrefix);

            // Load started keys: routes manually started (with definition AutoStart=false) should start
            var startedPrefix = RoutesController.RouteStartedKeyPrefix + contextName + ":";
            var started = await _stateStore.GetByPrefixAsync(startedPrefix);

            if (suspended.Count == 0 && started.Count == 0) return;

            foreach (var route in routes)
            {
                var suspendedKey = RoutesController.RouteSuspendedKey(contextName, route.RouteId);
                var startedKey = RoutesController.RouteStartedKey(contextName, route.RouteId);

                if (suspended.ContainsKey(suspendedKey))
                {
                    route.AutoStart = false;
                    _logger.LogInformation("Route {RouteId} in context {Context} suspended by persisted state",
                        route.RouteId, contextName);
                }
                else if (started.ContainsKey(startedKey))
                {
                    route.AutoStart = true;
                    _logger.LogInformation("Route {RouteId} in context {Context} started by persisted state",
                        route.RouteId, contextName);
                }
            }

            // Clean orphan keys for routes that no longer exist
            var routeIds = new HashSet<string>(routes.Select(r => r.RouteId));
            foreach (var key in suspended.Keys)
            {
                var routeId = key[(suspendedPrefix.Length)..];
                if (!routeIds.Contains(routeId))
                {
                    await _stateStore.RemoveAsync(key);
                    _logger.LogDebug("Cleaned orphan route suspended key: {Key}", key);
                }
            }
            foreach (var key in started.Keys)
            {
                var routeId = key[(startedPrefix.Length)..];
                if (!routeIds.Contains(routeId))
                {
                    await _stateStore.RemoveAsync(key);
                    _logger.LogDebug("Cleaned orphan route started key: {Key}", key);
                }
            }
        };

        if (!_contexts.TryAdd(contextName, context))
        {
            context.Dispose();
            throw new InvalidOperationException($"Context '{contextName}' already exists.");
        }

        _auditService?.RegisterContext(contextName, context);
        _logger.LogInformation("Created context {Context}", contextName);
        return context;
    }

    public IRouteContext? GetContext(string contextName) => _contexts.GetValueOrDefault(contextName);

    public IReadOnlyList<(string Name, IRouteContext Context)> GetAllContexts() =>
        _contexts.Select(kv => (kv.Key, kv.Value)).ToList();

    public async Task StartContextAsync(string contextName, bool ignoreAutoStart = false)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try { await StartContextCoreAsync(contextName, ignoreAutoStart); }
        finally { _lifecycleLock.Release(); }
    }

    public async Task<ContextActionResponse> StopContextAsync(string contextName, bool setAutoStartFalse = true, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try { return await StopContextCoreAsync(contextName, setAutoStartFalse, ct); }
        finally { _lifecycleLock.Release(); }
    }

    public async Task<ContextActionResponse> RestartContextAsync(string contextName, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stopResult = await StopContextCoreAsync(contextName, setAutoStartFalse: false, ct: ct);
            await _stateStore.SetAsync(AutoStartKeyPrefix + contextName, "true");
            await StartContextCoreAsync(contextName);

            return new ContextActionResponse
            {
                Name = contextName,
                Status = "Running",
                InflightBefore = stopResult.InflightBefore,
                InflightAfter = stopResult.InflightAfter,
                DrainTimeMs = stopResult.DrainTimeMs
            };
        }
        finally { _lifecycleLock.Release(); }
    }

    public async Task RemoveContextAsync(string contextName, bool preserveState = false)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try { await RemoveContextCoreAsync(contextName, preserveState); }
        finally { _lifecycleLock.Release(); }
    }

    // ── Core implementations (caller must hold _lifecycleLock) ──

    private async Task StartContextCoreAsync(string contextName, bool ignoreAutoStart = false)
    {
        if (!_contexts.TryGetValue(contextName, out var context))
            throw new InvalidOperationException($"Context '{contextName}' not found.");

        if (context.IsStarted)
        {
            _logger.LogDebug("Context {Context} already started", contextName);
            return;
        }

        if (!ignoreAutoStart && !await ShouldAutoStartAsync(contextName).ConfigureAwait(false))
        {
            _logger.LogInformation("Context {Context} skipped (autoStart=false)", contextName);
            return;
        }

        if (ignoreAutoStart && !IsAnonymousContext(contextName))
            await _stateStore.SetAsync(AutoStartKeyPrefix + contextName, "true");

        await context.Start(CancellationToken.None);
        _auditService?.RecordContextEvent(contextName, LifecycleEventType.ContextStarted);
        _logger.LogInformation("Started context {Context}", contextName);
    }

    private async Task<ContextActionResponse> StopContextCoreAsync(string contextName, bool setAutoStartFalse = true, CancellationToken ct = default)
    {
        if (!_contexts.TryGetValue(contextName, out var context))
            throw new InvalidOperationException($"Context '{contextName}' not found.");

        if (!context.IsStarted)
            return new ContextActionResponse { Name = contextName, Status = "Stopped" };

        var routeCtx = context as RouteContext;
        var inflightBefore = routeCtx?.InflightRepository.Count ?? 0;
        var sw = Stopwatch.StartNew();

        await context.Stop(ct);
        sw.Stop();

        var inflightAfter = routeCtx?.InflightRepository.Count ?? 0;

        if (inflightBefore > 0)
        {
            _logger.LogInformation(
                "Context {Context} stopped: drained {Drained}/{Total} exchanges in {Elapsed}ms",
                contextName, inflightBefore - inflightAfter, inflightBefore, sw.ElapsedMilliseconds);
        }

        if (setAutoStartFalse && !IsAnonymousContext(contextName))
            await _stateStore.SetAsync(AutoStartKeyPrefix + contextName, "false");

        _logger.LogInformation("Stopped context {Context} (autoStart persisted: {Persist})", contextName, setAutoStartFalse);
        _auditService?.RecordContextEvent(contextName, LifecycleEventType.ContextStopped);

        return new ContextActionResponse
        {
            Name = contextName,
            Status = "Stopped",
            InflightBefore = inflightBefore,
            InflightAfter = inflightAfter,
            DrainTimeMs = sw.ElapsedMilliseconds
        };
    }

    private async Task RemoveContextCoreAsync(string contextName, bool preserveState = false)
    {
        if (!_contexts.TryRemove(contextName, out var context))
            return;

        if (context.IsStarted)
            await context.Stop(CancellationToken.None);

        // Dispose named redb mini-containers before context disposal
        if (_namedRedbContainers.TryRemove(contextName, out var containers))
        {
            foreach (var container in containers)
            {
                try { container.Dispose(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose named redb container for context {Context}", contextName); }
            }
            _logger.LogDebug("Disposed {Count} named redb container(s) for context {Context}", containers.Count, contextName);
        }

        context.Dispose();
        if (!preserveState && !IsAnonymousContext(contextName))
        {
            await _stateStore.RemoveAsync(AutoStartKeyPrefix + contextName);
            await CleanupRouteSuspendedKeysAsync(contextName);
        }

        _auditService?.RecordContextEvent(contextName, LifecycleEventType.ContextRemoved);
        _logger.LogInformation("Removed context {Context}", contextName);
    }

    public bool ShouldAutoStart(string contextName)
        => ShouldAutoStartAsync(contextName).GetAwaiter().GetResult();

    public async Task<bool> ShouldAutoStartAsync(string contextName)
    {
        // First check state store (persisted override)
        var persisted = await _stateStore.GetAsync(AutoStartKeyPrefix + contextName).ConfigureAwait(false);
        if (persisted != null)
            return string.Equals(persisted, "true", StringComparison.OrdinalIgnoreCase);

        // Then check config
        var configValue = _configuration[$"Tsak:Contexts:{contextName}:AutoStart"];
        if (configValue != null)
            return string.Equals(configValue, "true", StringComparison.OrdinalIgnoreCase);

        // Default context config
        var defaultAutoStart = _configuration["Tsak:Contexts:default:AutoStart"];
        if (defaultAutoStart != null)
            return string.Equals(defaultAutoStart, "true", StringComparison.OrdinalIgnoreCase);

        return true; // default: autoStart
    }

    /// <summary>Remove orphaned autoStart keys for anonymous contexts from StateStore.</summary>
    public async Task CleanupAnonymousStatesAsync()
    {
        var entries = await _stateStore.GetByPrefixAsync(AutoStartKeyPrefix);
        foreach (var key in entries.Keys)
        {
            if (key.Contains("_dyn_", StringComparison.Ordinal))
            {
                await _stateStore.RemoveAsync(key);
                _logger.LogDebug("Cleaned up orphaned anonymous state key: {Key}", key);
            }
        }
    }

    /// <summary>Remove all route:suspended keys for a given context.</summary>
    private async Task CleanupRouteSuspendedKeysAsync(string contextName)
    {
        var prefix = RoutesController.RouteSuspendedKeyPrefix + contextName + ":";
        var entries = await _stateStore.GetByPrefixAsync(prefix);
        foreach (var key in entries.Keys)
        {
            await _stateStore.RemoveAsync(key);
            _logger.LogDebug("Cleaned route suspended key on context removal: {Key}", key);
        }
    }

    private static bool IsAnonymousContext(string contextName) =>
        contextName.Contains("_dyn_", StringComparison.Ordinal);

    /// <summary>
    /// Reads the "Redb" section from context configuration and registers named IRedbService instances.
    /// Each named instance gets its own mini-DI container tracked for disposal.
    /// <para>
    /// <b>EnsureCreated gating:</b> only the instance whose name matches
    /// <c>RedbInstanceName</c> (top-level, or <c>Identity:RedbInstanceName</c> as a
    /// module fallback) is allowed to honor <c>EnsureCreated=true</c> and run
    /// <see cref="IRedbService.InitializeAsync"/> at registration. All other
    /// registered instances are still created (so callers can resolve them via
    /// <c>"redb-factory:{name}"</c>) but their <c>EnsureCreated</c> is forced to
    /// <c>false</c> — they do NOT open a connection at startup. This prevents
    /// dev-time toggles like <c>identity-pg</c>/<c>identity-sqlite</c> from
    /// failing the host with a connect-refused warning when the non-active
    /// backend is offline.
    /// </para>
    /// </summary>
    private void RegisterNamedRedbServices(IRouteContext context, string contextName, IDictionary<string, object?> configuration)
    {
        if (!configuration.TryGetValue("Redb", out var redbSection) || redbSection is not IDictionary<string, object?> redbDict)
            return;

        var activeRedbName = ResolveActiveRedbName(configuration);
        var fallbackLicense = redb.Tsak.Core.Extensions.LicenseConfigReader.Read(_configuration, "Tsak:Redb:License");
        var containers = new List<ServiceProvider>();

        foreach (var (name, entryRaw) in redbDict)
        {
            if (entryRaw is not IDictionary<string, object?> entryConfig)
            {
                _logger.LogWarning("Named redb '{Name}' in context {Context}: config is not a dictionary, skipping", name, contextName);
                continue;
            }

            var isActive = activeRedbName is not null
                && string.Equals(name, activeRedbName, StringComparison.OrdinalIgnoreCase);

            IDictionary<string, object?> effectiveConfig = entryConfig;
            if (!isActive)
            {
                // Non-active instance — force EnsureCreated=false so InitializeAsync
                // (and the connect attempt it implies) is skipped. The container is
                // still built and 'redb-factory:{name}' is still resolvable; the
                // underlying provider opens its connection lazily at first actual use.
                var overrideConfig = new Dictionary<string, object?>(entryConfig, StringComparer.OrdinalIgnoreCase);
                overrideConfig["EnsureCreated"] = false;
                effectiveConfig = overrideConfig;
                _logger.LogInformation(
                    "Named redb '{Name}' in context {Context}: NOT active (active='{Active}') — registered without schema bootstrap",
                    name, contextName, activeRedbName ?? "<unset>");
            }

            try
            {
                var (service, container) = RedbInstanceFactory.Create(name, effectiveConfig, fallbackLicense, _logger);

                // Register the scope factory so that named services can be resolved
                // per-exchange (thread-safe). The singleton is kept as a fallback for
                // non-exchange callers (seed, diagnostics).
                var scopeFactory = container.GetRequiredService<IServiceScopeFactory>();
                var cleanName = name.TrimStart('#');
                context.AddToRegistry("redb-factory:" + cleanName, scopeFactory);
                context.RegisterRedbService(name, service);

                containers.Add(container);
                _logger.LogInformation("Registered named IRedbService '{Name}' for context {Context}", name, contextName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create named IRedbService '{Name}' for context {Context}, skipping", name, contextName);
            }
        }

        if (containers.Count > 0)
            _namedRedbContainers[contextName] = containers;
    }

    /// <summary>
    /// Picks the active redb instance name for a context. Checks top-level
    /// <c>RedbInstanceName</c> first (the canonical location used by the HTTP facade
    /// and merged from <c>Contexts.default.RedbInstanceName</c>), then falls back to
    /// <c>Identity:RedbInstanceName</c> for the redb.Identity module convention.
    /// Returns <c>null</c> when neither is set — in that case all entries init normally
    /// (legacy behaviour, no gating).
    /// </summary>
    private static string? ResolveActiveRedbName(IDictionary<string, object?> configuration)
    {
        if (configuration.TryGetValue("RedbInstanceName", out var top)
            && top?.ToString() is { } topStr && !string.IsNullOrWhiteSpace(topStr))
            return topStr;

        if (configuration.TryGetValue("Identity", out var identitySection)
            && identitySection is IDictionary<string, object?> identityDict
            && identityDict.TryGetValue("RedbInstanceName", out var nested)
            && nested?.ToString() is { } nestedStr && !string.IsNullOrWhiteSpace(nestedStr))
            return nestedStr;

        return null;
    }
}
