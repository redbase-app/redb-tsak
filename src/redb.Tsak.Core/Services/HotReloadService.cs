using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Modules;

namespace redb.Tsak.Core.Services;

/// <summary>
/// Manages hot reload of module assemblies.
/// Monitors configured directories for new DLLs, loads them in collectible ALCs,
/// performs graceful swap (stop old → start new), and supports auto-rollback.
/// </summary>
public sealed class HotReloadService : IDisposable
{
    private readonly ITsakModuleRegistry _registry;
    private readonly ITsakCoordinator _coordinator;
    private readonly ITsakContextManager _contextManager;
    private readonly SharedAssemblyLoader _sharedLoader;
    private readonly HotReloadOptions _options;
    private readonly ILogger<HotReloadService> _logger;
    private readonly string[] _assemblyProbePaths;
    private readonly bool _collectible;

    /// <summary>Number of non-collectible ALCs accumulated (never freed until process restart).</summary>
    private int _leakedAlcCount;

    /// <summary>Number of non-collectible ALCs that were orphaned and can't be unloaded. For diagnostics/monitoring.</summary>
    public int LeakedAlcCount => Volatile.Read(ref _leakedAlcCount);

    // DLLs that were scanned and found to contain no ITsakModule — remember last-write time so
    // we re-scan if the file is replaced with a newer version that might contain a module.
    private readonly ConcurrentDictionary<string, DateTime> _ignoredDlls = new(StringComparer.OrdinalIgnoreCase);

    // module name → (ALC, assembly path, version)
    private readonly ConcurrentDictionary<string, LoadedModuleInfo> _loadedModules = new(StringComparer.OrdinalIgnoreCase);

    // module name → list of previous versions (for rollback), most recent first
    private readonly ConcurrentDictionary<string, List<LoadedModuleInfo>> _previousVersions = new(StringComparer.OrdinalIgnoreCase);

    // module name → number of consecutive scans where DLL was missing (debounce for file replacement)
    private readonly ConcurrentDictionary<string, int> _pendingRemovals = new(StringComparer.OrdinalIgnoreCase);

    // config file path → last known write time (tracks context.json and {Module}.config.json changes)
    private readonly ConcurrentDictionary<string, DateTime> _configFileTimestamps = new(StringComparer.OrdinalIgnoreCase);

    // .tpkg path → list of module names loaded from that package (for atomic package reload/removal)
    private readonly ConcurrentDictionary<string, List<string>> _packageModules = new(StringComparer.OrdinalIgnoreCase);

    // .tpkg path → package's isolated ALC (entry points live here; companions in Default via Tracker)
    private readonly ConcurrentDictionary<string, ModulePackage> _packageInstances = new(StringComparer.OrdinalIgnoreCase);

    public HotReloadService(
        ITsakModuleRegistry registry,
        ITsakCoordinator coordinator,
        ITsakContextManager contextManager,
        SharedAssemblyLoader sharedLoader,
        IOptions<HotReloadOptions> options,
        IConfiguration configuration,
        ILogger<HotReloadService> logger)
    {
        _registry = registry;
        _coordinator = coordinator;
        _contextManager = contextManager;
        _sharedLoader = sharedLoader;
        _options = options.Value;
        _logger = logger;
        _assemblyProbePaths = configuration.GetSection("Tsak:Modules:AssemblyPaths").Get<string[]>() ?? [];
        _collectible = _options.Collectible;

        // Load-boundary signature enforcement. Applies to EVERY .tpkg — uploaded or dropped into the
        // directory — so the public key, not filesystem access, is the trust anchor.
        var sigOptions = new Modules.ModuleSignatureOptions();
        configuration.GetSection("Tsak:Modules:Signature").Bind(sigOptions);
        _signatureRequired = sigOptions.Required;
        var pem = sigOptions.ResolvePem();
        _signatureVerifier = pem is not null ? new Modules.ModuleSignatureVerifier(pem) : null;
        if (_signatureRequired && _signatureVerifier is null)
        {
            _logger.LogError(
                "Tsak:Modules:Signature:Required=true but no public key is configured — ALL .tpkg packages " +
                "will be refused at load. Set Tsak:Modules:Signature:PublicKeyPath.");
        }
        else if (_signatureRequired)
        {
            _logger.LogInformation("Module signature enforcement ON — unsigned/tampered .tpkg packages will be refused");
        }

        if (!_collectible)
        {
            _logger.LogInformation(
                "HotReload: Collectible=false (default). Old module versions will NOT be unloaded from memory. " +
                "This is safe for modules using XmlSerializer/Emit. " +
                "Set Tsak:HotReload:Collectible=true if your modules are Emit-free and you need memory reclamation");
        }
    }

    private readonly bool _signatureRequired;
    private readonly Modules.ModuleSignatureVerifier? _signatureVerifier;

    /// <summary>
    /// The load-boundary gate: returns true if the package may be loaded. When signature enforcement
    /// is on, a <c>.tpkg</c> must have a matching <c>.tpkg.sig</c> verified by the configured key —
    /// otherwise it is refused and logged, regardless of how the file arrived.
    /// </summary>
    private bool PassesSignatureGate(string tpkgPath)
    {
        if (!_signatureRequired) return true;

        if (_signatureVerifier is null)
        {
            _logger.LogError("Refusing {Pkg}: signature required but no public key configured", Path.GetFileName(tpkgPath));
            return false;
        }

        var sigPath = tpkgPath + ".sig";
        if (!File.Exists(sigPath))
        {
            _logger.LogError("Refusing {Pkg}: no signature file ({Sig}) alongside the package",
                Path.GetFileName(tpkgPath), Path.GetFileName(sigPath));
            return false;
        }

        try
        {
            var pkg = File.ReadAllBytes(tpkgPath);
            var sig = File.ReadAllBytes(sigPath);
            if (_signatureVerifier.VerifyWithEncodedSignature(pkg, sig))
                return true;

            _logger.LogError("Refusing {Pkg}: signature verification FAILED — unsigned by the trusted key or modified",
                Path.GetFileName(tpkgPath));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refusing {Pkg}: signature check errored", Path.GetFileName(tpkgPath));
            return false;
        }
    }

    /// <summary>
    /// Staged validation: opens the package in a throwaway collectible ALC and checks it loads and
    /// discovers at least one module — without mutating the shared assembly tracker
    /// (<c>forceReload:false</c>) and without registering anything. The throwaway ALC is unloaded
    /// afterwards. Returns false (with a reason) when the package is unopenable or module-less.
    /// </summary>
    internal bool ValidatePackageLoads(string tpkgPath, out string error)
    {
        error = "";
        Modules.ModulePackage? probe = null;
        try
        {
            probe = Modules.ModulePackage.Open(tpkgPath, probePaths: _assemblyProbePaths,
                logger: _logger, forceReload: false, collectible: true);
            if (probe is null)
            {
                error = "package could not be opened (invalid ZIP, missing/invalid manifest, or unnamed)";
                return false;
            }

            if (probe.LoadedAssemblies.Count == 0)
            {
                error = "no entry-point assemblies loaded";
                return false;
            }

            var found = 0;
            foreach (var assembly in probe.LoadedAssemblies)
            {
                try
                {
                    found += TsakModuleRegistry.DiscoverModulesInAssembly(assembly).Count;
                }
                catch (Exception ex)
                {
                    error = $"module discovery threw: {ex.Message}";
                    return false;
                }
            }

            if (found == 0)
            {
                error = "no modules discovered in the package";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            // Unload the throwaway ALC — it was created collectible for exactly this.
            probe?.Dispose();
        }
    }

    /// <summary>
    /// Scans the given directories for updated module DLLs and hot-swaps any that have newer versions.
    /// </summary>
    public async Task<int> ScanAndReloadAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var reloaded = 0;

        // Check shared layer changes first — if any shared DLL changed, restart all contexts
        if (_options.RestartContextsOnSharedChange && _sharedLoader.DetectChanges())
        {
            reloaded += await RestartAllContextsForSharedChangeAsync(ct).ConfigureAwait(false);
        }

        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
                continue;

            _logger.LogDebug("Hot-reload scan: {Path}", path);

            foreach (var dll in Directory.GetFiles(path, "*.dll"))
            {
                try
                {
                    var newVersion = GetAssemblyVersion(dll);
                    var moduleName = Path.GetFileNameWithoutExtension(dll);

                    // Skip DLLs previously identified as non-module dependencies (unless file changed)
                    if (_ignoredDlls.TryGetValue(dll, out var ignoredAt)
                        && File.GetLastWriteTimeUtc(dll) <= ignoredAt)
                        continue;

                    // Check if we have this module loaded with an older version
                    if (_loadedModules.TryGetValue(moduleName, out var current))
                    {
                        if (!ModuleVersion.IsNewer(newVersion, current.Version))
                            continue;

                        _logger.LogInformation("New version detected for {Module}: {Old} → {New}",
                            moduleName, current.Version, newVersion);

                        var success = await HotSwapAsync(moduleName, dll, newVersion, ct).ConfigureAwait(false);
                        if (success) reloaded++;
                    }
                    else
                    {
                        // Module not tracked by hot reload yet
                        var existingModule = _registry.GetModule(moduleName);
                        if (existingModule is not null)
                        {
                            // Already in registry (loaded at startup) — track with current file timestamp
                            // so the next scan compares apples-to-apples
                            _loadedModules[moduleName] = new LoadedModuleInfo(null, dll, newVersion);
                        }
                        else
                        {
                            // Potential new module — load and check for ITsakModule.
                            // Register in tracker so other modules' ALCs can resolve this as a dependency.
                            var fullDll = Path.GetFullPath(dll);
                            var dllBytes = File.ReadAllBytes(fullDll);
                            LoadedAssemblyTracker.LoadOrReuse(moduleName, dllBytes);

                            var newAlc = new ModuleAssemblyLoadContext(moduleName, _assemblyProbePaths, _collectible);
                            var assembly = newAlc.LoadFromBytes(fullDll);
                            var sourceDir = Path.GetDirectoryName(fullDll);
                            var modules = TsakModuleRegistry.DiscoverModulesInAssembly(assembly, sourceDir);
                            var newModule = modules.FirstOrDefault();

                            if (newModule is not null)
                            {
                                _logger.LogInformation("New module discovered: {Module} v{Version}", newModule.ModuleName, newVersion);
                                _registry.RegisterModule(newModule);
                                _loadedModules[moduleName] = new LoadedModuleInfo(newAlc, dll, newVersion);
                                reloaded++;
                            }
                            else
                            {
                                _logger.LogDebug("No ITsakModule found in {Path}, skipping", dll);
                                _ignoredDlls[dll] = File.GetLastWriteTimeUtc(dll);
                                newAlc.TryUnload();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning {Path}", dll);
                }
            }

            // Scan .tpkg packages (CAR-style hot deploy) — atomic package-level reload
            foreach (var tpkg in Directory.GetFiles(path, "*.tpkg"))
            {
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(tpkg);

                    // Check if file changed since last scan
                    if (_ignoredDlls.TryGetValue(tpkg, out var trackedWrite) && lastWrite <= trackedWrite)
                        continue;

                    _ignoredDlls[tpkg] = lastWrite;

                    // Load-boundary trust gate: verify the signature before ANY code from this
                    // package is loaded. Covers both API uploads and operator file drops.
                    if (!PassesSignatureGate(tpkg))
                        continue;

                    if (_packageModules.TryGetValue(tpkg, out var oldModuleNames) && oldModuleNames.Count > 0)
                    {
                        // Package update — reload preserving state (autoStart etc.)
                        reloaded += await ReloadPackageAsync(tpkg, oldModuleNames, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // New package — first-time load with per-package ALC
                        var package = ModulePackage.Open(tpkg, probePaths: _assemblyProbePaths,
                            logger: _logger, collectible: _collectible);
                        if (package is null)
                            continue;

                        var newModuleNames = new List<string>();

                        foreach (var assembly in package.LoadedAssemblies)
                        {
                            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(tpkg));
                            var modules = TsakModuleRegistry.DiscoverModulesInAssembly(assembly, sourceDir);

                            foreach (var module in modules)
                            {
                                if (module is Modules.StaticMethodModule smm)
                                    smm.EmbeddedConfigJson = package.ReadModuleConfigJson(module.ModuleName);

                                var existingModule = _registry.GetModule(module.ModuleName);
                                if (existingModule is not null)
                                {
                                    // Already in registry (loaded at startup discovery) — just track it
                                    _loadedModules[module.ModuleName] = new LoadedModuleInfo(package.Alc, tpkg, module.Version);
                                    newModuleNames.Add(module.ModuleName);
                                    continue;
                                }

                                _logger.LogInformation("New module {Module} from package {Pkg}",
                                    module.ModuleName, package.Manifest.Name);
                                _registry.RegisterModule(module);
                                _loadedModules[module.ModuleName] = new LoadedModuleInfo(package.Alc, tpkg, module.Version);
                                newModuleNames.Add(module.ModuleName);
                                reloaded++;
                            }
                        }

                        if (newModuleNames.Count > 0)
                        {
                            _packageModules[tpkg] = newModuleNames;
                            _packageInstances[tpkg] = package;
                        }
                        else
                        {
                            // No modules found — dispose the ALC
                            package.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning package {Path}", tpkg);
                }
            }
        }

        // Detect modules whose DLLs have been removed from disk
        var removed = await DetectRemovedModulesAsync(paths, ct).ConfigureAwait(false);
        reloaded += removed;

        // Detect config file changes (context.json, {Module}.config.json) — trigger context recreation
        var configReloads = await DetectConfigFileChangesAsync(ct).ConfigureAwait(false);
        reloaded += configReloads;

        return reloaded;
    }

    /// <summary>
    /// Detects changes to module config files (context.json, {Module}.config.json).
    /// When a config file changes, triggers context recreation via ProcessModuleUpdatedAsync.
    /// </summary>
    private async Task<int> DetectConfigFileChangesAsync(CancellationToken ct)
    {
        var reloaded = 0;

        foreach (var (moduleName, info) in _loadedModules.ToArray())
        {
            var module = _registry.GetModule(moduleName);
            if (module?.SourceDirectory is not { Length: > 0 } sourceDir || !Directory.Exists(sourceDir))
                continue;

            var configFiles = new[]
            {
                Path.Combine(sourceDir, "context.json"),
                Path.Combine(sourceDir, $"{moduleName}.config.json")
            };

            var changed = false;
            foreach (var configFile in configFiles)
            {
                if (!File.Exists(configFile))
                    continue;

                var lastWrite = File.GetLastWriteTimeUtc(configFile);
                var fullPath = Path.GetFullPath(configFile);

                if (_configFileTimestamps.TryGetValue(fullPath, out var tracked))
                {
                    if (lastWrite > tracked)
                    {
                        _logger.LogInformation("Config file changed: {File}", configFile);
                        _configFileTimestamps[fullPath] = lastWrite;
                        changed = true;
                    }
                }
                else
                {
                    // First time seeing this file — just track it
                    _configFileTimestamps[fullPath] = lastWrite;
                }
            }

            if (changed && module != null)
            {
                _logger.LogInformation("Recreating context for module {Module} due to config file change", moduleName);
                try
                {
                    await _coordinator.ProcessModuleUpdatedAsync(module).ConfigureAwait(false);
                    reloaded++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to recreate context for {Module} after config change", moduleName);
                }
            }
        }

        return reloaded;
    }

    /// <summary>
    /// Restarts all contexts when a shared assembly changes.
    /// Reloads shared assemblies, then stops and recreates all contexts.
    /// Old shared assemblies remain in Default ALC memory (can't unload Default ALC).
    /// </summary>
    private async Task<int> RestartAllContextsForSharedChangeAsync(CancellationToken ct)
    {
        _logger.LogInformation("Shared assembly change detected — reloading shared layer and restarting all contexts");

        var newCount = _sharedLoader.ReloadSharedAssemblies();
        _logger.LogInformation("Reloaded {Count} shared assemblies", newCount);

        var contexts = _contextManager.GetAllContexts();
        var restarted = 0;

        // Stop all contexts (reverse order for safety)
        for (var i = contexts.Count - 1; i >= 0; i--)
        {
            var (name, ctx) = contexts[i];
            if (name == "_system") continue; // _system context manages itself
            if (!ctx.IsStarted) continue;

            try
            {
                await _contextManager.StopContextAsync(name, setAutoStartFalse: false, ct: ct);
                _logger.LogDebug("Stopped context {Context} for shared reload", name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop context {Context} during shared reload", name);
            }
        }

        // Recreate all module contexts — coordinator will rebuild with new shared assemblies
        var allModules = _registry.GetAllModules();
        if (allModules.Count > 0)
        {
            try
            {
                await _coordinator.ProcessBatchAsync(allModules).ConfigureAwait(false);
                restarted = allModules.Count;
                _logger.LogInformation("Recreated {Count} module contexts after shared reload", allModules.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recreate contexts after shared reload");
            }
        }

        return restarted;
    }

    /// <summary>
    /// Detects modules whose DLL/.tpkg files no longer exist on disk.
    /// For packages: removes ALL modules from the package atomically.
    /// Uses debounce (RemovalDebounceScans) to avoid false positives during file replacement.
    /// </summary>
    private async Task<int> DetectRemovedModulesAsync(IEnumerable<string> scanPaths, CancellationToken ct)
    {
        // Build set of all module file paths currently on disk (.dll + .tpkg)
        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in scanPaths)
        {
            if (!Directory.Exists(path))
                continue;
            foreach (var dll in Directory.GetFiles(path, "*.dll"))
                existingFiles.Add(Path.GetFullPath(dll));
            foreach (var tpkg in Directory.GetFiles(path, "*.tpkg"))
                existingFiles.Add(Path.GetFullPath(tpkg));
        }

        // Clean stale _ignoredDlls entries (files that no longer exist)
        foreach (var ignored in _ignoredDlls.Keys.ToArray())
        {
            if (!existingFiles.Contains(Path.GetFullPath(ignored)))
                _ignoredDlls.TryRemove(ignored, out _);
        }

        var removed = 0;
        var debounceThreshold = Math.Max(1, _options.RemovalDebounceScans);

        // Detect removed .tpkg packages — unload all modules atomically
        foreach (var (tpkgPath, moduleNames) in _packageModules.ToArray())
        {
            if (existingFiles.Contains(Path.GetFullPath(tpkgPath)))
            {
                _pendingRemovals.TryRemove(tpkgPath, out _);
                continue;
            }

            var count = _pendingRemovals.AddOrUpdate(tpkgPath, 1, (_, c) => c + 1);
            if (count < debounceThreshold)
            {
                _logger.LogDebug("Package {Pkg} missing (scan {Count}/{Threshold}), waiting for debounce",
                    Path.GetFileName(tpkgPath), count, debounceThreshold);
                continue;
            }

            _logger.LogInformation("Package {Pkg} removed from disk — unloading {Count} modules",
                Path.GetFileName(tpkgPath), moduleNames.Count);

            foreach (var moduleName in moduleNames)
            {
                var success = await UnloadModuleAsync(moduleName, ct).ConfigureAwait(false);
                if (success) removed++;
            }

            _packageModules.TryRemove(tpkgPath, out _);
            _pendingRemovals.TryRemove(tpkgPath, out _);

            // Dispose the package ALC (unloads entry points if collectible)
            if (_packageInstances.TryRemove(tpkgPath, out var oldPkg))
            {
                oldPkg.Dispose();
                if (!_collectible)
                    Interlocked.Increment(ref _leakedAlcCount);
            }
        }

        // Detect removed bare .dll modules
        foreach (var (moduleName, info) in _loadedModules.ToArray())
        {
            // Skip modules tracked via _packageModules (handled above)
            if (info.DllPath.EndsWith(".tpkg", StringComparison.OrdinalIgnoreCase))
                continue;

            if (existingFiles.Contains(Path.GetFullPath(info.DllPath)))
            {
                _pendingRemovals.TryRemove(moduleName, out _);
                continue;
            }

            var count = _pendingRemovals.AddOrUpdate(moduleName, 1, (_, c) => c + 1);

            if (count < debounceThreshold)
            {
                _logger.LogDebug(
                    "Module {Module} DLL missing (scan {Count}/{Threshold}), waiting for debounce",
                    moduleName, count, debounceThreshold);
                continue;
            }

            _logger.LogInformation(
                "Module {Module} DLL removed from disk, performing graceful shutdown", moduleName);

            var success = await UnloadModuleAsync(moduleName, ct).ConfigureAwait(false);
            if (success) removed++;

            _pendingRemovals.TryRemove(moduleName, out _);
        }

        return removed;
    }

    /// <summary>
    /// Gracefully stops a module context, unregisters the module, and cleans up ALC state.
    /// </summary>
    public async Task<bool> UnloadModuleAsync(string moduleName, CancellationToken ct = default)
    {
        try
        {
            // 1. Graceful shutdown — stops context, closes connections
            try
            {
                await _coordinator.ProcessModuleRemovedAsync(moduleName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during graceful shutdown of module {Module}", moduleName);
            }

            // 2. Unregister from module registry
            _registry.UnregisterModule(moduleName);

            // 3. Clean up current ALC (skip for package-owned modules — their ALC
            //    is disposed atomically when the package itself is removed)
            if (_loadedModules.TryRemove(moduleName, out var info))
            {
                var isPackageOwned = info.DllPath.EndsWith(".tpkg", StringComparison.OrdinalIgnoreCase);
                if (!isPackageOwned)
                {
                    info.Alc?.TryUnload();
                    if (info.Alc is not null && !_collectible)
                        Interlocked.Increment(ref _leakedAlcCount);
                }
            }

            // 4. Clean up archived versions
            if (_previousVersions.TryRemove(moduleName, out var history))
            {
                lock (history)
                {
                    foreach (var prev in history)
                    {
                        prev.Alc?.TryUnload();
                        if (prev.Alc is not null && !_collectible)
                            Interlocked.Increment(ref _leakedAlcCount);
                    }
                    history.Clear();
                }
            }

            _logger.LogInformation(
                "Module {Module} unloaded: context stopped, ALC released (Collectible={Collectible})",
                moduleName, _collectible);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unload module {Module}", moduleName);
            return false;
        }
    }

    /// <summary>
    /// Atomically reloads a .tpkg package: silently unregisters old modules,
    /// loads new package, registers new modules via normal event pipeline.
    /// Unlike UnloadModuleAsync (permanent removal), this preserves persisted context state
    /// (e.g. autoStart) — a manually stopped context stays stopped after package reload.
    ///
    /// Flow: UnregisterSilent (no events, state untouched)
    ///     → RegisterModule (fires ModuleAdded)
    ///     → Coordinator.ProcessModuleAddedAsync
    ///     → RecreateContextWithModulesAsync → RemoveContextAsync(preserveState: true)
    ///     → ShouldAutoStart checks store → respects persisted autoStart=false
    /// </summary>
    public async Task<int> ReloadPackageAsync(string tpkgPath, List<string> oldModuleNames,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Reloading package {Pkg}: {Count} old modules",
            Path.GetFileName(tpkgPath), oldModuleNames.Count);

        // 0. Staged validation BEFORE touching the running version. Load the new package into a
        //    throwaway collectible ALC (forceReload:false → does not mutate the shared tracker;
        //    entry points are isolated) and verify it opens and discovers at least one module.
        //    Only if that succeeds do we tear the old version down. A broken .tpkg therefore leaves
        //    the live context running instead of destroying it and failing to replace it.
        if (!ValidatePackageLoads(tpkgPath, out var validationError))
        {
            _logger.LogError(
                "Refusing to reload package {Pkg}: staged validation failed ({Error}). Keeping the current version.",
                Path.GetFileName(tpkgPath), validationError);
            return 0;
        }

        // 1. Silently unregister old modules (no events → coordinator keeps context alive,
        //    autoStart state preserved in store)
        foreach (var moduleName in oldModuleNames)
        {
            _registry.UnregisterModuleSilent(moduleName);

            if (_loadedModules.TryRemove(moduleName, out var info))
            {
                // Don't TryUnload individual module ALCs for package modules —
                // the package ALC is unloaded atomically below.
                if (info.Alc is not null && info.DllPath != tpkgPath)
                {
                    info.Alc.TryUnload();
                    if (!_collectible)
                        Interlocked.Increment(ref _leakedAlcCount);
                }
            }

            _previousVersions.TryRemove(moduleName, out _);
        }

        // Dispose old package ALC (unloads entry points if collectible)
        if (_packageInstances.TryRemove(tpkgPath, out var oldPackage))
        {
            oldPackage.Dispose();
            if (!_collectible)
                Interlocked.Increment(ref _leakedAlcCount);
        }

        // 2. Open new package — forceReload: true ensures shared dependencies get replaced
        //    in the tracker so both modules see the updated Assembly instance.
        //    Each package gets its own ALC for entry point isolation.
        var package = ModulePackage.Open(tpkgPath, probePaths: _assemblyProbePaths,
            logger: _logger, forceReload: true, collectible: _collectible);
        if (package is null)
        {
            _logger.LogWarning("Failed to open updated package {Pkg}", Path.GetFileName(tpkgPath));
            return 0;
        }

        var newModuleNames = new List<string>();
        var allNewModules = new List<ITsakModule>();
        var reloaded = 0;

        // 3. Discover and register modules silently (no events — avoids concurrent
        //    ModuleAdded handlers racing to recreate the same context)
        foreach (var assembly in package.LoadedAssemblies)
        {
            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(tpkgPath));
            var modules = TsakModuleRegistry.DiscoverModulesInAssembly(assembly, sourceDir);

            foreach (var module in modules)
            {
                if (module is Modules.StaticMethodModule smm)
                    smm.EmbeddedConfigJson = package.ReadModuleConfigJson(module.ModuleName);

                _logger.LogInformation("Loading module {Module} from package {Pkg}",
                    module.ModuleName, package.Manifest.Name);
                _registry.ReplaceModuleSilent(module);
                _loadedModules[module.ModuleName] = new LoadedModuleInfo(package.Alc, tpkgPath, module.Version);
                newModuleNames.Add(module.ModuleName);
                allNewModules.Add(module);
                reloaded++;
            }
        }

        // 4. Process all modules as a batch — creates each context exactly once
        //    with the full set of modules (no intermediate partial recreations)
        if (allNewModules.Count > 0)
        {
            await _coordinator.ProcessBatchAsync(allNewModules).ConfigureAwait(false);
        }

        // 5. Update package tracking
        _packageModules[tpkgPath] = newModuleNames;
        _packageInstances[tpkgPath] = package;

        _logger.LogInformation("Package {Pkg} reloaded: {Count} modules",
            Path.GetFileName(tpkgPath), newModuleNames.Count);

        return reloaded;
    }

    /// <summary>
    /// Performs hot swap of a single module: load new → stop old → unload old → start new.
    /// On failure, stays on old version (auto-rollback).
    /// </summary>
    public async Task<bool> HotSwapAsync(string moduleName, string newDllPath, string newVersion,
        CancellationToken ct = default)
    {
        ITsakModule? newModule = null;
        ModuleAssemblyLoadContext? newAlc = null;

        try
        {
            // 1. Load new assembly in fresh ALC (from bytes — file never locked).
            //    Also replace in centralized tracker so dependencies resolved by
            //    other modules see the new version.
            newAlc = new ModuleAssemblyLoadContext(Path.GetFileNameWithoutExtension(newDllPath), _assemblyProbePaths, _collectible);
            var fullDllPath = Path.GetFullPath(newDllPath);
            var bytes = File.ReadAllBytes(fullDllPath);
            var asmName = Path.GetFileNameWithoutExtension(newDllPath);
            LoadedAssemblyTracker.Replace(asmName, bytes);
            var assembly = newAlc.LoadFromBytes(fullDllPath);

            // 2. Discover module in the loaded assembly
            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(newDllPath));
            var modules = TsakModuleRegistry.DiscoverModulesInAssembly(assembly, sourceDir);
            newModule = modules.FirstOrDefault(m =>
                m.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase));

            if (newModule is null)
            {
                _logger.LogWarning("Module {Module} not found in new assembly {Path}, skipping swap",
                    moduleName, newDllPath);
                newAlc.TryUnload();
                return false;
            }

            // 3. Validate new module can initialize
            if (!newModule.CanInitialize)
            {
                _logger.LogWarning("New version of {Module} cannot initialize, staying on current version",
                    moduleName);
                newAlc.TryUnload();
                return false;
            }

            // 4. Silently unregister old module (no events → state preserved in store,
            //    autoStart=false survives). Same pattern as ReloadPackageAsync.
            _registry.UnregisterModuleSilent(moduleName);

            // 5. Archive old version for rollback
            if (_loadedModules.TryGetValue(moduleName, out var oldInfo))
            {
                var history = _previousVersions.GetOrAdd(moduleName, _ => new List<LoadedModuleInfo>());
                lock (history)
                {
                    history.Insert(0, oldInfo);
                    // Trim to keep only N versions
                    while (history.Count > _options.KeepVersions)
                    {
                        var evicted = history[^1];
                        history.RemoveAt(history.Count - 1);
                        if (evicted.Alc is not null)
                        {
                            evicted.Alc.TryUnload();
                            if (!_collectible)
                                Interlocked.Increment(ref _leakedAlcCount);
                        }
                    }
                }
            }

            // 6. Replace module in registry (silent — no events, coordinator is called directly)
            _registry.ReplaceModuleSilent(newModule);

            // 7. Start new module context
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startCts.CancelAfter(TimeSpan.FromSeconds(_options.StartupTimeoutSeconds));

            try
            {
                await _coordinator.ProcessModuleAddedAsync(newModule).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                _logger.LogError(ex, "New version of {Module} failed to start within timeout, rolling back",
                    moduleName);
                return await RollbackAsync(moduleName, ct).ConfigureAwait(false) ? false : false;
            }

            // 8. Update tracking
            _loadedModules[moduleName] = new LoadedModuleInfo(newAlc, newDllPath, newVersion);

            if (!_collectible)
            {
                var leaked = Interlocked.Increment(ref _leakedAlcCount);
                _logger.LogWarning(
                    "Hot-swapped {Module} to v{Version} (Collectible=false: {LeakedCount} orphaned ALC(s) in memory, " +
                    "not reclaimable until process restart)",
                    moduleName, newVersion, leaked);
            }
            else
            {
                _logger.LogInformation("Hot-swapped {Module} to version {Version}", moduleName, newVersion);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hot swap failed for {Module}, staying on current version", moduleName);

            // Cleanup failed ALC
            if (newModule is null)
                newAlc?.TryUnload();

            return false;
        }
    }

    /// <summary>
    /// Rolls back a module to the previous version.
    /// Returns true if rollback succeeded.
    /// </summary>
    public async Task<bool> RollbackAsync(string moduleName, CancellationToken ct = default)
    {
        if (!_previousVersions.TryGetValue(moduleName, out var history))
        {
            _logger.LogWarning("No previous versions available for rollback of {Module}", moduleName);
            return false;
        }

        LoadedModuleInfo? previous;
        lock (history)
        {
            if (history.Count == 0)
            {
                _logger.LogWarning("No previous versions available for rollback of {Module}", moduleName);
                return false;
            }
            previous = history[0];
            history.RemoveAt(0);
        }

        try
        {
            // Stop current version
            await _coordinator.ProcessModuleRemovedAsync(moduleName).ConfigureAwait(false);

            // Re-discover and register old version (load via ALC from bytes — no lock)
            var rollbackAlc = new ModuleAssemblyLoadContext(Path.GetFileNameWithoutExtension(previous.DllPath), _assemblyProbePaths, _collectible);
            var rollbackBytes = File.ReadAllBytes(previous.DllPath);
            var rollbackAsmName = Path.GetFileNameWithoutExtension(previous.DllPath);
            LoadedAssemblyTracker.Replace(rollbackAsmName, rollbackBytes);
            var rollbackAssembly = rollbackAlc.LoadFromBytes(previous.DllPath);
            var rollbackSourceDir = Path.GetDirectoryName(Path.GetFullPath(previous.DllPath));
            var modules = TsakModuleRegistry.DiscoverModulesInAssembly(rollbackAssembly, rollbackSourceDir);
            var oldModule = modules.FirstOrDefault(m =>
                m.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase));

            if (oldModule is null)
            {
                _logger.LogError("Failed rollback: module {Module} not found in previous assembly {Path}",
                    moduleName, previous.DllPath);
                return false;
            }

            _registry.ReplaceModuleSilent(oldModule);
            await _coordinator.ProcessModuleAddedAsync(oldModule).ConfigureAwait(false);

            _loadedModules[moduleName] = previous;
            _logger.LogInformation("Rolled back {Module} to version {Version}", moduleName, previous.Version);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback failed for {Module}", moduleName);
            return false;
        }
    }

    /// <summary>Tracks a module loaded via the initial discovery (not via hot reload).</summary>
    public void TrackModule(string moduleName, string dllPath, string version)
    {
        _loadedModules[moduleName] = new LoadedModuleInfo(null, dllPath, version);
    }

    /// <summary>
    /// Adopts packages loaded during startup discovery (by TsakModuleRegistry).
    /// Takes ownership of the package ALCs so hot-reload can manage their lifecycle.
    /// Must be called once, after DiscoverModulesAsync and before the first ScanAndReloadAsync.
    /// </summary>
    public void AdoptStartupPackages(IReadOnlyDictionary<string, ModulePackage> packages)
    {
        foreach (var (tpkgPath, package) in packages)
        {
            _packageInstances[tpkgPath] = package;
            _ignoredDlls[tpkgPath] = package.LastWriteUtc;

            // Build module name list for this package
            var moduleNames = new List<string>();
            foreach (var assembly in package.LoadedAssemblies)
            {
                var sourceDir = Path.GetDirectoryName(Path.GetFullPath(tpkgPath));
                var modules = TsakModuleRegistry.DiscoverModulesInAssembly(assembly, sourceDir);
                foreach (var module in modules)
                {
                    moduleNames.Add(module.ModuleName);
                    _loadedModules[module.ModuleName] = new LoadedModuleInfo(
                        package.Alc, tpkgPath, module.Version);
                }
            }

            if (moduleNames.Count > 0)
                _packageModules[tpkgPath] = moduleNames;

            _logger.LogDebug("Adopted startup package {Pkg}: {Count} modules",
                Path.GetFileName(tpkgPath), moduleNames.Count);
        }
    }

    public void Dispose()
    {
        // Dispose package ALCs first (covers all package-owned modules atomically)
        foreach (var pkg in _packageInstances.Values)
            pkg.Dispose();
        _packageInstances.Clear();

        // Dispose remaining bare-DLL ALCs (skip package-owned — already handled above)
        foreach (var info in _loadedModules.Values)
        {
            if (info.Alc is not null && !info.DllPath.EndsWith(".tpkg", StringComparison.OrdinalIgnoreCase))
                info.Alc.TryUnload();
        }

        foreach (var history in _previousVersions.Values)
        {
            foreach (var info in history)
                info.Alc?.TryUnload();
        }
    }

    private static string GetAssemblyVersion(string dllPath)
    {
        var lastWrite = File.GetLastWriteTimeUtc(dllPath);
        return lastWrite.ToString("yyyy.MM.dd.HHmm", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal sealed record LoadedModuleInfo(
        ModuleAssemblyLoadContext? Alc,
        string DllPath,
        string Version);
}
