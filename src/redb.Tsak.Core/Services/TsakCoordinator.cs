using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Services;

/// <summary>
/// Orchestrates module-to-context mapping. Subscribes to registry events and 
/// creates/updates/removes contexts accordingly.
/// Two context types: Named (from config, multi-module) and Anonymous (one module each).
/// Based on lt.tsak ModuleContextCoordinator.
/// </summary>
public class TsakCoordinator : ITsakCoordinator, IDisposable
{
    private readonly ITsakContextManager _contextManager;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TsakCoordinator> _logger;

    // Registry events are EventHandler<T> (synchronous, void). Instead of handling them as
    // fire-and-forget `async void` (unordered, unobservable, and impossible to wait on), each event is
    // ENQUEUED here and a single background consumer AWAITS the handler in FIFO order (review items
    // 1.7/3.8). This gives true async processing with no `async void`, deterministic ordering, and a
    // WaitForIdleAsync drain so startup/tests can wait for topology changes to finish.
    private readonly Channel<QueuedEvent> _eventQueue =
        Channel.CreateUnbounded<QueuedEvent>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _consumerTask;
    private int _initialized;
    // _pending and _idleSignal are guarded by _idleLock together: the busy↔idle transition and the
    // signal reset/complete MUST be atomic, or a lost-wakeup hangs WaitForIdleAsync (the consumer could
    // complete a signal a concurrent Enqueue just swapped out, stranding the real waiter's signal).
    private readonly object _idleLock = new();
    private int _pending;
    private TaskCompletionSource _idleSignal = CreateIdleSignal(completed: true);

    private sealed record QueuedEvent(Func<Task> Work, string EventName, string Subject);

    // module → context name mapping
    private readonly ConcurrentDictionary<string, string> _moduleContextMap = new(StringComparer.OrdinalIgnoreCase);
    // contextName → first moduleName that claimed it (for collision detection)
    private readonly ConcurrentDictionary<string, string> _claimedContextNames = new(StringComparer.OrdinalIgnoreCase);
    // Per-context lock: serializes RecreateContextWithModulesAsync and ProcessModuleRemovedAsync
    // to prevent races when multiple ModuleAdded events fire concurrently for the same context.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _contextLocks = new(StringComparer.OrdinalIgnoreCase);
    private ITsakModuleRegistry? _registry;

    public TsakCoordinator(
        ITsakContextManager contextManager,
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        ILogger<TsakCoordinator> logger)
    {
        _contextManager = contextManager;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public void Initialize(ITsakModuleRegistry registry)
    {
        // Idempotent: subscribe exactly once, even if Initialize is called again with the same registry
        // — otherwise events would be handled (and processed) multiple times.
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        _registry = registry;

        // Registry events (synchronous EventHandler<T>) are ENQUEUED, not handled inline as async void.
        // A single background consumer awaits each handler in order — see the field comment above.
        registry.ModuleAdded += (_, module) => Enqueue(() => ProcessModuleAddedAsync(module), "ModuleAdded", module.ModuleName);
        registry.ModuleRemoved += (_, name) => Enqueue(() => ProcessModuleRemovedAsync(name), "ModuleRemoved", name);
        registry.ModuleUpdated += (_, module) => Enqueue(() => ProcessModuleUpdatedAsync(module), "ModuleUpdated", module.ModuleName);
        registry.ModulesBatchAdded += (_, modules) => Enqueue(() => ProcessBatchAsync(modules), "ModulesBatchAdded", $"{modules.Count} modules");

        _consumerTask ??= Task.Run(ConsumeEventsAsync);

        _logger.LogInformation("Coordinator initialized, subscribed to registry events");
    }

    /// <summary>
    /// Waits until every enqueued registry-event has been processed. Lets startup and tests wait for
    /// topology changes to settle instead of racing the (formerly fire-and-forget) handlers.
    /// </summary>
    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task idleTask;
            lock (_idleLock)
            {
                if (_pending == 0)
                    return;
                // While _pending > 0 the signal is always the uncompleted one installed on the last
                // idle→busy transition, so this captures a genuinely-pending task (no busy-spin).
                idleTask = _idleSignal.Task;
            }
            await idleTask.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Enqueues a registry-event handler for the single background consumer to await in order.</summary>
    private void Enqueue(Func<Task> work, string eventName, string subject)
    {
        // Mark busy BEFORE writing, so a WaitForIdleAsync called right after enqueuing cannot observe
        // a stale "idle" signal. The transition and the signal swap are one atomic step under the lock.
        lock (_idleLock)
        {
            if (_pending++ == 0)
                _idleSignal = CreateIdleSignal(completed: false);
        }

        if (!_eventQueue.Writer.TryWrite(new QueuedEvent(work, eventName, subject)))
        {
            // Queue already completed (coordinator disposed) — undo the pending bump.
            lock (_idleLock)
            {
                if (--_pending == 0)
                    _idleSignal.TrySetResult();
            }
        }
    }

    /// <summary>Single background consumer: awaits each queued handler in FIFO order, catching+logging
    /// per item so one bad module can never crash the node or stall the queue.</summary>
    private async Task ConsumeEventsAsync()
    {
        await foreach (var item in _eventQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await item.Work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Coordinator '{Event}' handler failed for '{Subject}' — node stays up, this topology change was skipped",
                    item.EventName, item.Subject);
            }
            finally
            {
                lock (_idleLock)
                {
                    if (--_pending == 0)
                        _idleSignal.TrySetResult(); // safe under lock: RunContinuationsAsynchronously
                }
            }
        }
    }

    private static TaskCompletionSource CreateIdleSignal(bool completed)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
            tcs.TrySetResult();
        return tcs;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _eventQueue.Writer.TryComplete();
        // Best-effort drain so in-flight topology changes finish; never block shutdown indefinitely.
        try { _consumerTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch { /* shutdown — ignore */ }
        GC.SuppressFinalize(this);
    }

    public async Task ProcessBatchAsync(IReadOnlyList<ITsakModule> modules)
    {
        _logger.LogInformation("Processing batch of {Count} modules", modules.Count);

        // Group by target context (named or anonymous)
        var namedContextModules = new Dictionary<string, List<ITsakModule>>(StringComparer.OrdinalIgnoreCase);
        var anonymousModules = new List<ITsakModule>();

        foreach (var module in modules)
        {
            var (contextName, isNamed) = DetermineContextForModule(module);
            if (isNamed)
            {
                if (!TryClaimContextName(contextName, module.ModuleName))
                    continue; // collision logged — skip this module, keep processing the batch

                if (!namedContextModules.TryGetValue(contextName, out var list))
                {
                    list = [];
                    namedContextModules[contextName] = list;
                }
                list.Add(module);
            }
            else
            {
                anonymousModules.Add(module);
            }
        }

        // Collect all context names and their dependencies for topological ordering
        var allContextEntries = new List<(string Name, IReadOnlyList<ITsakModule> Modules, bool IsAnonymous)>();

        foreach (var (contextName, contextModules) in namedContextModules)
            allContextEntries.Add((contextName, contextModules, false));

        foreach (var module in anonymousModules)
        {
            var contextName = GenerateAnonymousContextName(module.ModuleName);
            allContextEntries.Add((contextName, [module], true));
        }

        // Topological sort by Dependencies from config
        var ordered = TopologicalSort(allContextEntries);

        // Create contexts in dependency order
        foreach (var (contextName, contextModules, _) in ordered)
            await RecreateContextWithModulesAsync(contextName, contextModules);
    }

    public async Task ProcessModuleAddedAsync(ITsakModule module)
    {
        _logger.LogInformation("Processing added module {Module}", module.ModuleName);

        var (contextName, isNamed) = DetermineContextForModule(module);

        if (isNamed)
        {
            if (!TryClaimContextName(contextName, module.ModuleName))
                return; // collision logged — skip this module (node stays up)

            // Named context: recreate with ALL configured modules (existing + new)
            var allModulesForContext = GetAllModulesForNamedContext(contextName, module);
            await RecreateContextWithModulesAsync(contextName, allModulesForContext);
        }
        else
        {
            // Anonymous: create dedicated context
            contextName = GenerateAnonymousContextName(module.ModuleName);
            await RecreateContextWithModulesAsync(contextName, [module]);
        }
    }

    public async Task ProcessModuleRemovedAsync(string moduleName)
    {
        _logger.LogInformation("Processing removed module {Module}", moduleName);

        if (!_moduleContextMap.TryRemove(moduleName, out var contextName))
            return;

        var existingContext = _contextManager.GetContext(contextName);
        if (existingContext == null)
            return;

        // If anonymous context: just remove it
        if (IsAnonymousContext(contextName))
        {
            await _contextManager.RemoveContextAsync(contextName);
            return;
        }

        // Named context: recreate without the removed module
        var remainingModules = GetModulesInContext(contextName)
            .Where(m => !string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (remainingModules.Count == 0)
        {
            await _contextManager.RemoveContextAsync(contextName);
            // Release the context name claim if no modules left
            _claimedContextNames.TryRemove(contextName, out _);
        }
        else
        {
            await RecreateContextWithModulesAsync(contextName, remainingModules);
        }
    }

    public async Task ProcessModuleUpdatedAsync(ITsakModule module)
    {
        _logger.LogInformation("Processing updated module {Module} v{Version}", module.ModuleName, module.Version);

        if (!_moduleContextMap.TryGetValue(module.ModuleName, out var contextName))
        {
            // Treat as new
            await ProcessModuleAddedAsync(module);
            return;
        }

        // Recreate context with updated module
        if (IsAnonymousContext(contextName))
        {
            await RecreateContextWithModulesAsync(contextName, [module]);
        }
        else
        {
            var allModules = GetAllModulesForNamedContext(contextName, module);
            await RecreateContextWithModulesAsync(contextName, allModules);
        }
    }

    /// <summary>
    /// Stops, removes, recreates, initializes all modules, and starts a context.
    /// Serialized per context name to prevent races from concurrent ModuleAdded events.
    /// </summary>
    private async Task RecreateContextWithModulesAsync(string contextName, IReadOnlyList<ITsakModule> modules)
    {
        var contextLock = _contextLocks.GetOrAdd(contextName, _ => new SemaphoreSlim(1, 1));
        await contextLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await RecreateContextWithModulesCoreAsync(contextName, modules).ConfigureAwait(false);
        }
        finally
        {
            contextLock.Release();
        }
    }

    private async Task RecreateContextWithModulesCoreAsync(string contextName, IReadOnlyList<ITsakModule> modules)
    {
        // Remove existing context if present
        var existing = _contextManager.GetContext(contextName);
        if (existing != null)
            await _contextManager.RemoveContextAsync(contextName, preserveState: true);

        var contextConfig = GetContextConfig(contextName, modules);
        var context = _contextManager.CreateContext(contextName, _serviceProvider, contextConfig);

        // Initialize all modules in this context
        foreach (var module in modules)
        {
            try
            {
                module.Initialize(context);
                _moduleContextMap[module.ModuleName] = contextName;
                _logger.LogInformation("Initialized module {Module} in context {Context}",
                    module.ModuleName, contextName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize module {Module} in context {Context}",
                    module.ModuleName, contextName);
            }
        }

        // Auto-start if configured
        if (await _contextManager.ShouldAutoStartAsync(contextName).ConfigureAwait(false))
        {
            try
            {
                await _contextManager.StartContextAsync(contextName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-start context {Context}", contextName);
            }
        }
    }

    /// <summary>
    /// Determines the target context for a module based on config.
    /// Priority: appsettings Contexts section → embedded config from .tpkg → {Module}.config.json from disk → anonymous.
    /// </summary>
    private (string ContextName, bool IsNamed) DetermineContextForModule(ITsakModule module)
    {
        var moduleName = module.ModuleName;

        // Step 1: Check appsettings Tsak:Contexts:{key}:Modules[]
        var contextsSection = _configuration.GetSection("Tsak:Contexts");
        foreach (var contextSection in contextsSection.GetChildren())
        {
            if (string.Equals(contextSection.Key, "default", StringComparison.OrdinalIgnoreCase))
                continue;

            var modulesConfig = contextSection.GetSection("Modules").Get<string[]>();
            if (modulesConfig?.Contains(moduleName, StringComparer.OrdinalIgnoreCase) == true)
            {
                // Use explicit ContextName from section if present, otherwise section key
                var explicitName = contextSection["ContextName"];
                return (explicitName ?? contextSection.Key, true);
            }
        }

        // Step 2: Check embedded config from .tpkg package (in-memory)
        if (module is Modules.StaticMethodModule { EmbeddedConfigJson: { Length: > 0 } configJson })
        {
            try
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(configJson));
                var jsonConfig = new ConfigurationBuilder()
                    .AddJsonStream(stream)
                    .Build();

                var contextName = jsonConfig["ContextName"];
                if (!string.IsNullOrWhiteSpace(contextName))
                {
                    _logger.LogInformation(
                        "Module {Module} has ContextName '{ContextName}' from embedded package config",
                        moduleName, contextName);
                    return (contextName, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read ContextName from embedded config for {Module}", moduleName);
            }
        }

        // Step 3: Check {Module}.config.json for ContextName
        if (module.SourceDirectory is { Length: > 0 } sourceDir && Directory.Exists(sourceDir))
        {
            var moduleConfigPath = Path.Combine(sourceDir, $"{moduleName}.config.json");
            if (File.Exists(moduleConfigPath))
            {
                try
                {
                    var jsonConfig = new ConfigurationBuilder()
                        .AddJsonFile(moduleConfigPath, optional: true, reloadOnChange: false)
                        .Build();

                    var contextName = jsonConfig["ContextName"];
                    if (!string.IsNullOrWhiteSpace(contextName))
                    {
                        _logger.LogInformation(
                            "Module {Module} has ContextName '{ContextName}' from {File}.config.json",
                            moduleName, contextName, moduleName);
                        return (contextName, true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read ContextName from {Module}.config.json", moduleName);
                }
            }
        }

        // Step 4: Anonymous context
        return (moduleName, false);
    }

    /// <summary>
    /// Validates that a named ContextName is not already claimed by an unrelated module.
    /// Modules in the same appsettings Modules[] section are allowed to share a context name.
    /// </summary>
    /// <summary>
    /// Claims a context name for a module. Returns <c>true</c> if the name is free (or already this
    /// module's / same appsettings group), <c>false</c> on a genuine collision. Must NOT throw: it runs
    /// under the registry event handlers, and a throw there (previously an
    /// <c>InvalidOperationException</c>) escaped as an unobserved <c>async void</c> exception and
    /// killed the whole node. A collision is now a logged rejection — the offending module is skipped,
    /// the node stays up and other modules keep loading.
    /// </summary>
    private bool TryClaimContextName(string contextName, string moduleName)
    {
        if (!_claimedContextNames.TryGetValue(contextName, out var claimedBy))
        {
            _claimedContextNames[contextName] = moduleName;
            return true;
        }

        if (string.Equals(claimedBy, moduleName, StringComparison.OrdinalIgnoreCase))
            return true;

        // Allow if both modules are in the same appsettings Modules[] section
        if (AreModulesInSameAppSettingsGroup(claimedBy, moduleName))
            return true;

        _logger.LogError(
            "Context name '{ContextName}' already claimed by module '{ClaimedBy}' — module '{ModuleName}' " +
            "will be SKIPPED (context-name collision). Give it a distinct context or group them in the same "
            + "appsettings Modules[] section.",
            contextName, claimedBy, moduleName);
        return false;
    }

    /// <summary>
    /// Checks if two modules are listed in the same Tsak:Contexts:{*}:Modules[] section.
    /// </summary>
    private bool AreModulesInSameAppSettingsGroup(string module1, string module2)
    {
        var contextsSection = _configuration.GetSection("Tsak:Contexts");
        foreach (var contextSection in contextsSection.GetChildren())
        {
            var modulesConfig = contextSection.GetSection("Modules").Get<string[]>();
            if (modulesConfig == null) continue;

            var hasModule1 = modulesConfig.Contains(module1, StringComparer.OrdinalIgnoreCase);
            var hasModule2 = modulesConfig.Contains(module2, StringComparer.OrdinalIgnoreCase);

            if (hasModule1 && hasModule2)
                return true;
        }
        return false;
    }

    private List<ITsakModule> GetAllModulesForNamedContext(string contextName, ITsakModule newModule)
    {
        var existing = GetModulesInContext(contextName).ToList();
        // Replace if same name, otherwise add
        var replaced = false;
        for (int i = 0; i < existing.Count; i++)
        {
            if (string.Equals(existing[i].ModuleName, newModule.ModuleName, StringComparison.OrdinalIgnoreCase))
            {
                existing[i] = newModule;
                replaced = true;
                break;
            }
        }
        if (!replaced)
            existing.Add(newModule);
        return existing;
    }

    private IEnumerable<ITsakModule> GetModulesInContext(string contextName)
    {
        if (_registry == null)
            return [];

        return _moduleContextMap
            .Where(kv => string.Equals(kv.Value, contextName, StringComparison.OrdinalIgnoreCase))
            .Select(kv => _registry.GetModule(kv.Key))
            .Where(m => m != null)
            .Cast<ITsakModule>()
            .ToList();
    }

    /// <summary>
    /// Builds context configuration by merging layers:
    ///   Layer 1: Tsak:Contexts:default (base)
    ///   Layer 2: Tsak:Contexts:{contextName} (named overrides)
    ///   Layer 3: context.json files from module source directories
    ///   Layer 4: {Module}.config.json files from module source directories
    ///   Layer 5: Tsak:Contexts:{contextName}:Override (DevOps last word)
    /// Each layer deep-merges over the previous; later layers win on conflicts.
    /// </summary>
    internal IDictionary<string, object?> GetContextConfig(string contextName, IReadOnlyList<ITsakModule>? modules = null)
    {
        var config = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // Layer 1: default section
        var defaultSection = _configuration.GetSection("Tsak:Contexts:default");
        if (defaultSection.Exists())
            LoadConfigurationSection(defaultSection, config);

        // Layer 2: named context section
        if (!string.Equals(contextName, "default", StringComparison.OrdinalIgnoreCase))
        {
            var contextSection = _configuration.GetSection($"Tsak:Contexts:{contextName}");
            if (contextSection.Exists())
            {
                var layer2 = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                LoadConfigurationSection(contextSection, layer2);
                ConfigMergeHelper.DeepMerge(config, layer2);
            }
        }

        // Layers 3+4: module config files (disk or embedded in .tpkg)
        if (modules != null)
        {
            foreach (var module in modules)
            {
                // Prefer disk-based config files when source directory exists
                if (module.SourceDirectory is { Length: > 0 } sourceDir && Directory.Exists(sourceDir))
                {
                    LoadModuleConfigFiles(sourceDir, module.ModuleName, contextName, config);
                }

                // Fallback: load embedded config from .tpkg package (Layer 4)
                if (module is Modules.StaticMethodModule { EmbeddedConfigJson: { Length: > 0 } configJson })
                {
                    try
                    {
                        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(configJson));
                        var jsonConfig = new ConfigurationBuilder()
                            .AddJsonStream(stream)
                            .Build();

                        var layer = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var section in jsonConfig.GetChildren())
                            LoadNestedOrLeaf(section, layer);

                        ConfigMergeHelper.DeepMerge(config, layer);
                        _logger.LogDebug("Loaded embedded config for module {Module} ({Count} keys)",
                            module.ModuleName, layer.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load embedded config for module {Module}",
                            module.ModuleName);
                    }
                }
            }
        }

        // Layer 5: Override section (DevOps last word)
        var overrideSection = _configuration.GetSection($"Tsak:Contexts:{contextName}:Override");
        if (overrideSection.Exists() && overrideSection.GetChildren().Any())
        {
            var overrideConfig = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            LoadConfigurationSection(overrideSection, overrideConfig);
            ConfigMergeHelper.DeepMerge(config, overrideConfig);
            _logger.LogInformation("Applied Override layer for context {Context} ({Count} keys)",
                contextName, overrideConfig.Count);
        }

        return config;
    }

    /// <summary>
    /// Loads a configuration section into a dictionary, handling nested objects recursively.
    /// Skips reserved keys (Modules, Override).
    /// </summary>
    private static void LoadConfigurationSection(IConfigurationSection section, IDictionary<string, object?> config)
    {
        foreach (var item in section.GetChildren())
        {
            if (string.Equals(item.Key, "Modules", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Key, "Override", StringComparison.OrdinalIgnoreCase))
                continue;

            if (item.GetChildren().Any())
            {
                var nested = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                LoadNestedConfiguration(item, nested);
                config[item.Key] = nested;
            }
            else
            {
                config[item.Key] = item.Value;
            }
        }
    }

    /// <summary>
    /// Recursively loads nested configuration sections into a dictionary.
    /// </summary>
    private static void LoadNestedConfiguration(IConfigurationSection section, IDictionary<string, object?> config)
    {
        foreach (var child in section.GetChildren())
        {
            if (child.GetChildren().Any())
            {
                var nested = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                LoadNestedConfiguration(child, nested);
                config[child.Key] = nested;
            }
            else
            {
                config[child.Key] = child.Value;
            }
        }
    }

    /// <summary>
    /// Loads module config files from the module's source directory (layers 3+4):
    ///   - context.json: module infrastructure defaults
    ///   - {ModuleName}.config.json: module builder settings
    ///
    /// Both files support an OPTIONAL <c>Contexts:</c> root key for per-context
    /// scoping (mirrors host appsettings <c>Tsak:Contexts:{name}</c>):
    /// <code>
    /// {
    ///   "Contexts": {
    ///     "default":       { /* shared by all contexts in this folder */ },
    ///     "identity.core": { "Identity": { ... } },
    ///     "identity.http": { "IdentityTransport": { ... } }
    ///   }
    /// }
    /// </code>
    /// When <c>Contexts:</c> is absent, the file is merged flat (legacy behaviour).
    /// When present, only <c>Contexts:default</c> and <c>Contexts:{contextName}</c>
    /// are merged for the target context; sections for OTHER contexts are ignored.
    /// </summary>
    private void LoadModuleConfigFiles(string sourceDir, string moduleName, string contextName, IDictionary<string, object?> config)
    {
        // Layer 3: context.json (module infra defaults)
        var contextJsonPath = Path.Combine(sourceDir, "context.json");
        if (File.Exists(contextJsonPath))
        {
            try
            {
                var jsonConfig = new ConfigurationBuilder()
                    .AddJsonFile(contextJsonPath, optional: true, reloadOnChange: false)
                    .Build();

                var layer = ExtractLayerForContext(jsonConfig, contextName);
                ConfigMergeHelper.DeepMerge(config, layer);
                _logger.LogDebug("Loaded context.json from {Path} ({Count} keys for context {Context})",
                    sourceDir, layer.Count, contextName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load context.json from {Path}", sourceDir);
            }
        }

        // Layer 4: {ModuleName}.config.json (builder settings)
        var moduleConfigPath = Path.Combine(sourceDir, $"{moduleName}.config.json");
        if (File.Exists(moduleConfigPath))
        {
            try
            {
                var jsonConfig = new ConfigurationBuilder()
                    .AddJsonFile(moduleConfigPath, optional: true, reloadOnChange: false)
                    .Build();

                var layer = ExtractLayerForContext(jsonConfig, contextName);
                ConfigMergeHelper.DeepMerge(config, layer);
                _logger.LogDebug("Loaded {File}.config.json from {Path} ({Count} keys for context {Context})",
                    moduleName, sourceDir, layer.Count, contextName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load {Module}.config.json from {Path}", moduleName, sourceDir);
            }
        }
    }

    /// <summary>
    /// Extracts the effective config layer for a given context from a JSON file.
    /// If the file has a top-level <c>Contexts:</c> section, merges
    /// <c>Contexts:default</c> then <c>Contexts:{contextName}</c> (later wins).
    /// Otherwise returns all root keys flat (legacy behaviour).
    /// </summary>
    private static Dictionary<string, object?> ExtractLayerForContext(IConfiguration jsonConfig, string contextName)
    {
        var layer = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var contextsSection = jsonConfig.GetSection("Contexts");
        if (contextsSection.Exists())
        {
            // New format: per-context scoping
            var defaultSection = contextsSection.GetSection("default");
            if (defaultSection.Exists())
            {
                foreach (var section in defaultSection.GetChildren())
                    LoadNestedOrLeaf(section, layer);
            }

            var ownSection = contextsSection.GetSection(contextName);
            if (ownSection.Exists())
            {
                var own = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var section in ownSection.GetChildren())
                    LoadNestedOrLeaf(section, own);
                ConfigMergeHelper.DeepMerge(layer, own);
            }
        }
        else
        {
            // Legacy format: flat keys at root, shared by all contexts
            foreach (var section in jsonConfig.GetChildren())
                LoadNestedOrLeaf(section, layer);
        }

        return layer;
    }

    /// <summary>
    /// Loads an IConfigurationSection as either a nested dictionary or a leaf value.
    /// </summary>
    private static void LoadNestedOrLeaf(IConfigurationSection section, IDictionary<string, object?> config)
    {
        if (section.GetChildren().Any())
        {
            var nested = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            LoadNestedConfiguration(section, nested);
            config[section.Key] = nested;
        }
        else
        {
            config[section.Key] = section.Value;
        }
    }

    private static string GenerateAnonymousContextName(string moduleName) =>
        $"{moduleName}_dyn_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";

    private static bool IsAnonymousContext(string contextName) =>
        contextName.Contains("_dyn_", StringComparison.Ordinal);

    /// <summary>
    /// Reads Dependencies for a context from config layers (appsettings, context.json, module.config.json).
    /// Returns context names this context depends on.
    /// </summary>
    internal string[] GetContextDependencies(string contextName, IReadOnlyList<ITsakModule>? modules = null)
    {
        // Check appsettings: Tsak:Contexts:{name}:Dependencies[]
        var deps = _configuration.GetSection($"Tsak:Contexts:{contextName}:Dependencies").Get<string[]>();
        if (deps is { Length: > 0 })
            return deps;

        // Check default: Tsak:Contexts:default:Dependencies[]
        deps = _configuration.GetSection("Tsak:Contexts:default:Dependencies").Get<string[]>();
        if (deps is { Length: > 0 })
            return deps;

        // Check module config files
        if (modules == null) return [];

        foreach (var module in modules)
        {
            if (module.SourceDirectory is not { Length: > 0 } sourceDir || !Directory.Exists(sourceDir))
                continue;

            // context.json
            var contextJsonPath = Path.Combine(sourceDir, "context.json");
            if (File.Exists(contextJsonPath))
            {
                try
                {
                    var json = new ConfigurationBuilder()
                        .AddJsonFile(contextJsonPath, optional: true, reloadOnChange: false)
                        .Build();
                    deps = json.GetSection("Dependencies").Get<string[]>();
                    if (deps is { Length: > 0 }) return deps;
                }
                catch { /* logged elsewhere */ }
            }

            // {Module}.config.json
            var moduleConfigPath = Path.Combine(sourceDir, $"{module.ModuleName}.config.json");
            if (File.Exists(moduleConfigPath))
            {
                try
                {
                    var json = new ConfigurationBuilder()
                        .AddJsonFile(moduleConfigPath, optional: true, reloadOnChange: false)
                        .Build();
                    deps = json.GetSection("Dependencies").Get<string[]>();
                    if (deps is { Length: > 0 }) return deps;
                }
                catch { /* logged elsewhere */ }
            }
        }

        return [];
    }

    /// <summary>
    /// Topologically sorts context entries by their declared Dependencies.
    /// Contexts with no dependencies come first. Cyclic dependencies are detected and logged.
    /// </summary>
    private List<(string Name, IReadOnlyList<ITsakModule> Modules, bool IsAnonymous)> TopologicalSort(
        List<(string Name, IReadOnlyList<ITsakModule> Modules, bool IsAnonymous)> entries)
    {
        if (entries.Count <= 1)
            return entries;

        // Build adjacency: context → depends-on context names
        var depMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var entryMap = new Dictionary<string, (string Name, IReadOnlyList<ITsakModule> Modules, bool IsAnonymous)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            entryMap[entry.Name] = entry;
            depMap[entry.Name] = GetContextDependencies(entry.Name, entry.Modules);
        }

        // Kahn's algorithm
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in entryMap.Keys)
            inDegree[name] = 0;

        foreach (var (name, deps) in depMap)
        {
            foreach (var dep in deps)
            {
                if (entryMap.ContainsKey(dep))
                    inDegree[name]++;
                // Dependencies on contexts not in this batch are ignored (already running or external)
            }
        }

        var queue = new Queue<string>();
        foreach (var (name, degree) in inDegree)
        {
            if (degree == 0)
                queue.Enqueue(name);
        }

        var result = new List<(string Name, IReadOnlyList<ITsakModule> Modules, bool IsAnonymous)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            visited.Add(current);
            result.Add(entryMap[current]);

            // Find contexts that depend on current — reduce their in-degree
            foreach (var (name, deps) in depMap)
            {
                if (visited.Contains(name)) continue;
                if (deps.Any(d => string.Equals(d, current, StringComparison.OrdinalIgnoreCase)))
                {
                    inDegree[name]--;
                    if (inDegree[name] == 0)
                        queue.Enqueue(name);
                }
            }
        }

        // Detect cycle: any unvisited entries have circular dependencies
        foreach (var entry in entries)
        {
            if (!visited.Contains(entry.Name))
            {
                var deps = string.Join(", ", depMap[entry.Name]);
                _logger.LogError(
                    "Cyclic dependency detected: context {Context} depends on [{Dependencies}]. Skipping — will not start",
                    entry.Name, deps);
            }
        }

        return result;
    }
}
