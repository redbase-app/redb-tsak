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
    // Idempotent disposal + coordination with an in-progress scan (review item 4.12): Dispose unloads
    // ALCs, so a scan running concurrently must finish first (the gate) and a scan starting after
    // disposal must bail (_disposed).
    private int _disposed;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

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

    // .tpkg path → (size, mtime, consecutive-stable-scan count) — copy-stability debounce for additions
    // so a half-written package is not opened mid-copy (review item 4.11).
    private readonly ConcurrentDictionary<string, (long Size, DateTime Mtime, int Streak)> _additionStability = new(StringComparer.OrdinalIgnoreCase);

    // config file path → last known write time (tracks context.json and {Module}.config.json changes)
    private readonly ConcurrentDictionary<string, DateTime> _configFileTimestamps = new(StringComparer.OrdinalIgnoreCase);

    // .tpkg path → list of module names loaded from that package (for atomic package reload/removal)
    private readonly ConcurrentDictionary<string, List<string>> _packageModules = new(StringComparer.OrdinalIgnoreCase);

    // .tpkg path → package's isolated ALC (entry points live here; companions in Default via Tracker)
    private readonly ConcurrentDictionary<string, ModulePackage> _packageInstances = new(StringComparer.OrdinalIgnoreCase);

    // bare-DLL full path → REAL module name (InitRoute namespace) — a file whose basename differs
    // from its module name must still be tracked/removed under the real name (review С24).
    private readonly ConcurrentDictionary<string, string> _bareDllNames = new(StringComparer.OrdinalIgnoreCase);

    public HotReloadService(
        ITsakModuleRegistry registry,
        ITsakCoordinator coordinator,
        ITsakContextManager contextManager,
        SharedAssemblyLoader sharedLoader,
        IOptions<HotReloadOptions> options,
        IConfiguration configuration,
        ILogger<HotReloadService> logger,
        Modules.ModuleLoadGate? loadGate = null)
    {
        _registry = registry;
        _coordinator = coordinator;
        _contextManager = contextManager;
        _sharedLoader = sharedLoader;
        _options = options.Value;
        _logger = logger;
        _assemblyProbePaths = configuration.GetSection("Tsak:Modules:AssemblyPaths").Get<string[]>() ?? [];
        _collectible = _options.Collectible;

        // Load-boundary signature enforcement — one shared gate for every path that loads module
        // code (startup discovery included), so the public key, not filesystem access, is the
        // trust anchor (review 2026-09-02, К3). The optional parameter keeps plain unit-test
        // construction working; DI always provides the gate.
        _loadGate = loadGate ?? new Modules.ModuleLoadGate(configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger<Modules.ModuleLoadGate>());

        if (!_collectible)
        {
            _logger.LogInformation(
                "HotReload: Collectible=false (default). Old module versions will NOT be unloaded from memory. " +
                "This is safe for modules using XmlSerializer/Emit. " +
                "Set Tsak:HotReload:Collectible=true if your modules are Emit-free and you need memory reclamation");
        }
    }

    private readonly Modules.ModuleLoadGate _loadGate;

    /// <summary>
    /// Staged validation: opens the package (from the caller's VERIFIED bytes) in a throwaway
    /// collectible ALC and checks it loads and discovers at least one module — companion DLLs stay
    /// inside that throwaway ALC (<c>isolatedCompanions</c>), so a rejected package leaks nothing
    /// into the process-wide tracker (review 2026-09-02, С23). The throwaway ALC is unloaded
    /// afterwards. Returns false (with a reason) when the package is unopenable or module-less.
    /// </summary>
    internal bool ValidatePackageLoads(byte[] packageBytes, string tpkgPath, out string error)
    {
        error = "";
        Modules.ModulePackage? probe = null;
        try
        {
            probe = Modules.ModulePackage.Open(packageBytes, tpkgPath, File.GetLastWriteTimeUtc(tpkgPath),
                probePaths: _assemblyProbePaths,
                logger: _logger, forceReload: false, collectible: true, isolatedCompanions: true);
            if (probe is null)
            {
                error = "package could not be opened (invalid ZIP, missing/invalid manifest, or unnamed)";
                return false;
            }

            // Third convention (Route-XML Ф5.2): XML artifacts count as a module, so an XML-only
            // package (no entry points) passes. Well-formedness is the cheap staged check that
            // needs no RouteContext — a truncated or hand-broken artifact is refused here, before
            // the live version is torn down; schema/compile errors stay load-time module-isolated
            // faults per the owner's fail-fast decision.
            var xmlModules = 0;
            if (probe.Manifest.Artifacts.Count > 0)
            {
                foreach (var (name, content) in probe.XmlArtifacts)
                {
                    try
                    {
                        System.Xml.Linq.XDocument.Parse(content);
                    }
                    catch (System.Xml.XmlException ex)
                    {
                        error = $"XML artifact {name} is not well-formed: {ex.Message}";
                        return false;
                    }
                }
                xmlModules = 1;
            }

            if (probe.LoadedAssemblies.Count == 0 && xmlModules == 0)
            {
                error = "no entry-point assemblies loaded";
                return false;
            }

            var found = xmlModules;
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
        if (Volatile.Read(ref _disposed) == 1)
            return 0;

        // Serialize scans and coordinate with Dispose: a scan holds the gate for its whole run, so
        // Dispose (which also takes the gate) can never unload an ALC out from under a live scan (4.12).
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) == 1)
                return 0;
            return await ScanAndReloadCoreAsync(paths, ct).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<int> ScanAndReloadCoreAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var reloaded = 0;

        // Shared-layer change: shared assemblies live in the non-collectible Default ALC and
        // CANNOT be swapped in-process — the old "reload + restart all contexts" re-served the
        // already-tracked instances, draining and rebuilding every context on the SAME old bytes
        // while the log claimed a completed reload (review 2026-09-02, С22). Report once per
        // change and require a process restart; no pointless context churn.
        if (_options.RestartContextsOnSharedChange && _sharedLoader.DetectChanges())
        {
            _logger.LogCritical(
                "Shared assembly change detected on disk. Shared DLLs load into the non-unloadable Default ALC "
                + "and cannot be swapped in-process — restart the worker process to pick up the new bytes. "
                + "Contexts are NOT being restarted: that would drain them onto the same old code.");
            _sharedLoader.AcknowledgeChanges();
        }

        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
                continue;

            _logger.LogDebug("Hot-reload scan: {Path}", path);

            foreach (var dll in Directory.GetFiles(path, "*.dll"))
            {
                // Trust gate (review 2026-09-02, К3): with signature enforcement on, a bare DLL
                // cannot carry a signature — hot-reload must not load what discovery refuses.
                if (!_loadGate.AllowBareDll(dll))
                    continue;

                try
                {
                    var newVersion = GetAssemblyVersion(dll);
                    // The REAL module name (InitRoute namespace) can differ from the file basename;
                    // keying trackers by basename made removal unregister the wrong key while
                    // unloading the live ALC — a ghost module (review 2026-09-02, С24). Once a DLL
                    // has been discovered, address it by its real name.
                    var moduleName = _bareDllNames.TryGetValue(Path.GetFullPath(dll), out var knownName)
                        ? knownName
                        : Path.GetFileNameWithoutExtension(dll);

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
                            // Potential new module — load into its OWN isolated ALC only. Do NOT also
                            // byte-load it into the Default ALC (review item 3.7b): that produced a
                            // second, distinct copy of the module's types in Default while the module
                            // actually runs as the ALC copy — a type-identity split-brain and a leak.
                            // A module's own assembly is not a shared dependency; cross-module shared
                            // types belong in the shared layer (which IS tracked).
                            var fullDll = Path.GetFullPath(dll);

                            var newAlc = new ModuleAssemblyLoadContext(moduleName, _assemblyProbePaths, _collectible);
                            ITsakModule? newModule;
                            try
                            {
                                var assembly = newAlc.LoadFromBytes(fullDll);
                                var sourceDir = Path.GetDirectoryName(fullDll);
                                newModule = TsakModuleRegistry.DiscoverModulesInAssembly(assembly, sourceDir).FirstOrDefault();
                            }
                            catch (Exception ex)
                            {
                                // A file Tsak cannot use: an InitRoute.main of an unsupported shape, a type that
                                // fails to load, a half-copied image. Said once, then ignored until the file
                                // changes. The throw used to skip both the ignore mark and the unload, so every
                                // scan loaded the file into one more permanent ALC and logged the same error.
                                _logger.LogError(ex, "Module file {Path} cannot be used and is ignored until it changes", dll);
                                IgnoreUntilChanged(dll);
                                newAlc.TryUnload();
                                continue;
                            }

                            if (newModule is not null)
                            {
                                _bareDllNames[fullDll] = newModule.ModuleName;
                                var alreadyRegistered = _registry.GetModule(newModule.ModuleName);
                                if (alreadyRegistered is not null)
                                {
                                    // Registered at startup under its REAL name (≠ basename) —
                                    // adopt tracking under that name instead of registering a
                                    // duplicate second copy (review 2026-09-02, С24).
                                    newAlc.TryUnload();
                                    _loadedModules.TryAdd(newModule.ModuleName, new LoadedModuleInfo(null, dll, newVersion));
                                }
                                else
                                {
                                    _logger.LogInformation("New module discovered: {Module} v{Version}", newModule.ModuleName, newVersion);
                                    await _registry.RegisterModuleAsync(newModule);
                                    _loadedModules[newModule.ModuleName] = new LoadedModuleInfo(newAlc, dll, newVersion);
                                    reloaded++;
                                }
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
                    var tpkgInfo = new FileInfo(tpkg);
                    var lastWrite = tpkgInfo.LastWriteTimeUtc;

                    // Skip if unchanged since the last SUCCESSFUL process. The tracked time is now written
                    // only after a successful open/verify (4.11), so a transient failure is retried rather
                    // than permanently skipped.
                    if (_ignoredDlls.TryGetValue(tpkg, out var trackedWrite) && lastWrite <= trackedWrite)
                    {
                        _additionStability.TryRemove(tpkg, out _);
                        continue;
                    }

                    // Copy-stability debounce (4.11): a freshly-dropped package may still be copying.
                    // Require its size+mtime to hold steady for AdditionStabilityScans scans before opening
                    // it, so we never read a half-written ZIP. Mirrors RemovalDebounceScans for deletions.
                    if (!IsPackageStableForLoad(tpkg, tpkgInfo.Length, lastWrite))
                        continue;

                    // Load-boundary trust gate: verify the signature before ANY code from this package is
                    // loaded. On failure we do NOT record the timestamp, so the package is retried once its
                    // detached .sig is copied in (or the file is fixed). The verified bytes are also the
                    // loaded bytes — no verify-then-reopen window (review 2026-09-02, К3).
                    var verifiedBytes = _loadGate.ReadVerifiedTpkg(tpkg);
                    if (verifiedBytes is null)
                        continue;

                    if (_packageModules.TryGetValue(tpkg, out var oldModuleNames) && oldModuleNames.Count > 0)
                    {
                        // Package update — reload preserving state (autoStart etc.). ReloadPackageAsync
                        // records the tracked timestamp itself only after a successful reload.
                        reloaded += await ReloadPackageAsync(tpkg, verifiedBytes, oldModuleNames, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // New package — first-time load with per-package ALC
                        var package = ModulePackage.Open(verifiedBytes, tpkg, lastWrite,
                            probePaths: _assemblyProbePaths, logger: _logger, collectible: _collectible);
                        if (package is null)
                            continue; // corrupt/partial — do NOT record; retry next scan

                        var newModuleNames = new List<string>();

                        // Discover every entry point before registering any: a package whose entry point Tsak
                        // cannot use (an InitRoute.main of an unsupported shape) is refused whole, said once, and
                        // ignored until the file changes. The throw used to leave the package's ALC and its
                        // extracted files behind and re-open it on every scan.
                        var discovered = new List<ITsakModule>();
                        try
                        {
                            var packageSourceDir = Path.GetDirectoryName(Path.GetFullPath(tpkg));
                            foreach (var assembly in package.LoadedAssemblies)
                                discovered.AddRange(TsakModuleRegistry.DiscoverModulesInAssembly(assembly, packageSourceDir));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Package {Path} cannot be used and is ignored until it changes", tpkg);
                            package.Dispose();
                            _ignoredDlls[tpkg] = lastWrite;
                            _additionStability.TryRemove(tpkg, out _);
                            continue;
                        }

                        {
                            foreach (var module in discovered)
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
                                await _registry.RegisterModuleAsync(module);
                                _loadedModules[module.ModuleName] = new LoadedModuleInfo(package.Alc, tpkg, module.Version);
                                newModuleNames.Add(module.ModuleName);
                                reloaded++;
                            }
                        }

                        // Third convention (Route-XML Ф5.2): XML artifacts from the manifest — the
                        // same block the startup discovery runs. Without it an XML-only package
                        // dropped into a RUNNING worker was opened, found module-less, disposed and
                        // ignored (такт 5 E2E find); one adopted at startup lost its hot-reload
                        // tracking the same way, so a redrop never reached ReloadPackageAsync.
                        if (package.Manifest.Artifacts.Count > 0)
                        {
                            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(tpkg));
                            var xmlModule = new Modules.XmlRouteModule(package, sourceDir,
                                package.ReadModuleConfigJson(package.Manifest.Name), _logger);

                            if (_registry.GetModule(xmlModule.ModuleName) is not null)
                            {
                                // Already in registry (loaded at startup discovery) — just track it
                                _loadedModules[xmlModule.ModuleName] = new LoadedModuleInfo(package.Alc, tpkg, xmlModule.Version);
                                newModuleNames.Add(xmlModule.ModuleName);
                            }
                            else
                            {
                                _logger.LogInformation("New XML route module {Module} from package {Pkg}",
                                    xmlModule.ModuleName, package.Manifest.Name);
                                await _registry.RegisterModuleAsync(xmlModule);
                                _loadedModules[xmlModule.ModuleName] = new LoadedModuleInfo(package.Alc, tpkg, xmlModule.Version);
                                newModuleNames.Add(xmlModule.ModuleName);
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

                        // Opened & verified successfully — NOW record the tracked time (4.11), so an
                        // earlier mid-copy failure did not permanently skip this package. (A package with
                        // no modules still opened cleanly and should not be re-opened every scan.)
                        _ignoredDlls[tpkg] = lastWrite;
                        _additionStability.TryRemove(tpkg, out _);
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
    /// Copy-stability debounce for a newly-seen / changed <c>.tpkg</c> (review item 4.11): returns true
    /// only once the file's size and last-write time have held steady for
    /// <see cref="HotReloadOptions.AdditionStabilityScans"/> consecutive scans, so a half-written package
    /// is never opened mid-copy. Any change to size or mtime resets the streak.
    /// </summary>
    internal bool IsPackageStableForLoad(string path, long size, DateTime mtime)
    {
        var threshold = Math.Max(1, _options.AdditionStabilityScans);
        var cur = _additionStability.AddOrUpdate(
            path,
            _ => (size, mtime, 1),
            (_, prev) => prev.Size == size && prev.Mtime == mtime
                ? (prev.Size, prev.Mtime, prev.Streak + 1)
                : (size, mtime, 1));

        if (cur.Streak < threshold)
        {
            _logger.LogDebug(
                "Package {Pkg} still settling (stable scan {Count}/{Threshold}) — waiting for the copy to finish",
                Path.GetFileName(path), cur.Streak, threshold);
            return false;
        }
        return true;
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

    // RestartAllContextsForSharedChangeAsync was removed 2026-09-02 (review С22): shared DLLs
    // load into the non-unloadable Default ALC, so its "reload" re-served the already-tracked
    // old instances and the context churn rebuilt everything on the same bytes. A shared change
    // is now reported once as critical and requires a process restart (see the scan loop).

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

        // Clean stale copy-stability entries for packages that vanished mid-settle (4.11)
        foreach (var pending in _additionStability.Keys.ToArray())
        {
            if (!existingFiles.Contains(Path.GetFullPath(pending)))
                _additionStability.TryRemove(pending, out _);
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
            await _registry.UnregisterModuleAsync(moduleName);

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
    /// Reloads a .tpkg package in place: the old modules leave the registry silently (no events), the
    /// new package is opened, its modules are registered silently, and the coordinator recreates their
    /// contexts as one batch — so persisted context state (e.g. autoStart=false of a named context)
    /// survives the reload. Unlike UnloadModuleAsync (permanent removal), the module stays.
    ///
    /// If a module of the new version does not come up, the previous package is restored (old module
    /// instances and their contexts back, modules only the new version added stopped) and the file is
    /// skipped until it changes. A module the new version no longer ships has its context stopped.
    /// </summary>
    public async Task<int> ReloadPackageAsync(string tpkgPath, List<string> oldModuleNames,
        CancellationToken ct = default)
    {
        // Path-based entry (external callers): route through the trust gate so the verified
        // bytes are also the loaded bytes (review 2026-09-02, К3).
        var verified = _loadGate.ReadVerifiedTpkg(tpkgPath);
        if (verified is null)
        {
            _logger.LogWarning("Refusing to reload package {Pkg}: trust gate refused it", Path.GetFileName(tpkgPath));
            return 0;
        }
        return await ReloadPackageAsync(tpkgPath, verified, oldModuleNames, ct).ConfigureAwait(false);
    }

    internal async Task<int> ReloadPackageAsync(string tpkgPath, byte[] packageBytes, List<string> oldModuleNames,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Reloading package {Pkg}: {Count} old modules",
            Path.GetFileName(tpkgPath), oldModuleNames.Count);

        // 0. Staged validation BEFORE touching the running version. Load the new package into a
        //    throwaway collectible ALC (companions isolated inside it — a rejected package leaks
        //    nothing into the shared tracker) and verify it opens and discovers at least one module.
        //    Only if that succeeds do we tear the old version down. A broken .tpkg therefore leaves
        //    the live context running instead of destroying it and failing to replace it.
        if (!ValidatePackageLoads(packageBytes, tpkgPath, out var validationError))
        {
            _logger.LogError(
                "Refusing to reload package {Pkg}: staged validation failed ({Error}). Keeping the current version.",
                Path.GetFileName(tpkgPath), validationError);
            return 0;
        }

        // 1. Silently unregister old modules (no events → coordinator keeps context alive,
        //    autoStart state preserved in store). Capture what is needed to put them back first: until
        //    the new version has actually come up, the old one is the rollback target — so the
        //    destructive cleanup (unloading a replaced DLL module's ALC, dropping version history)
        //    waits for step 5.
        var oldModules = new List<(string Name, ITsakModule? Module, LoadedModuleInfo? Info)>(oldModuleNames.Count);
        foreach (var moduleName in oldModuleNames)
        {
            _loadedModules.TryGetValue(moduleName, out var oldInfo);
            oldModules.Add((moduleName, _registry.GetModule(moduleName), oldInfo));
            _registry.UnregisterModuleSilent(moduleName);
        }

        // The old package is disposed AFTER the context swap (step 4), not here: Dispose deletes
        // the package's extracted resources directory, and until ProcessBatchAsync replaces the
        // context the OLD routes are still live and may read file= resources from it. TryUnload
        // is cooperative, so delaying it changes nothing for assemblies (forceReload below already
        // handles shared-dependency replacement).
        _packageInstances.TryRemove(tpkgPath, out var oldPackage);

        // 2. Open new package — forceReload: true ensures shared dependencies get replaced
        //    in the tracker so both modules see the updated Assembly instance.
        //    Each package gets its own ALC for entry point isolation.
        var package = ModulePackage.Open(packageBytes, tpkgPath, File.GetLastWriteTimeUtc(tpkgPath),
            probePaths: _assemblyProbePaths, logger: _logger, forceReload: true, collectible: _collectible);
        if (package is null)
        {
            _logger.LogWarning("Failed to open updated package {Pkg}", Path.GetFileName(tpkgPath));
            // The old context is still the live one — keep its package tracked and alive, and put its
            // modules back into the registry (step 1 took them out silently).
            if (oldPackage is not null)
                _packageInstances[tpkgPath] = oldPackage;
            foreach (var (_, oldModule, _) in oldModules)
            {
                if (oldModule is not null)
                    await _registry.ReplaceModuleSilentAsync(oldModule);
            }
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
                await _registry.ReplaceModuleSilentAsync(module);
                _loadedModules[module.ModuleName] = new LoadedModuleInfo(package.Alc, tpkgPath, module.Version);
                newModuleNames.Add(module.ModuleName);
                allNewModules.Add(module);
                reloaded++;
            }
        }

        // Third convention (Route-XML Ф5.2): XML artifacts — same silent-replace flow, so a redrop
        // of an XML-only package swaps its routes through the batch recreation below.
        if (package.Manifest.Artifacts.Count > 0)
        {
            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(tpkgPath));
            var xmlModule = new Modules.XmlRouteModule(package, sourceDir,
                package.ReadModuleConfigJson(package.Manifest.Name), _logger);

            _logger.LogInformation("Loading XML route module {Module} from package {Pkg}",
                xmlModule.ModuleName, package.Manifest.Name);
            await _registry.ReplaceModuleSilentAsync(xmlModule);
            _loadedModules[xmlModule.ModuleName] = new LoadedModuleInfo(package.Alc, tpkgPath, xmlModule.Version);
            newModuleNames.Add(xmlModule.ModuleName);
            allNewModules.Add(xmlModule);
            reloaded++;
        }

        // 4. Process all modules as a batch — creates each context exactly once
        //    with the full set of modules (no intermediate partial recreations). A module that does not
        //    come up — reported by the coordinator, which swallows Initialize/start exceptions so a bad
        //    module cannot crash the node, or an exception out of the batch itself — restores the
        //    previous package instead of leaving the module down.
        if (allNewModules.Count > 0)
        {
            ModuleActivationFailure? failed = null;
            Exception? batchException = null;
            try
            {
                var report = await _coordinator.ProcessBatchAsync(allNewModules).ConfigureAwait(false);
                foreach (var module in allNewModules)
                {
                    failed = report.FailureFor(module.ModuleName);
                    if (failed is not null)
                        break;
                }
            }
            catch (Exception ex)
            {
                batchException = ex; // logged right below, together with the reported case
            }

            if (failed is not null || batchException is not null)
            {
                _logger.LogError(failed?.Exception ?? batchException,
                    "Package {Pkg}: the new version did not come up (module {Module}); restoring the previous version",
                    Path.GetFileName(tpkgPath), failed?.ModuleName ?? "batch");
                await RestorePreviousPackageAsync(tpkgPath, package, oldPackage, oldModules, newModuleNames)
                    .ConfigureAwait(false);
                return 0;
            }
        }

        // 5. The new version is up — the deferred cleanup of the old one is safe now.
        var newNames = new HashSet<string>(newModuleNames, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _, oldInfo) in oldModules)
        {
            // Don't TryUnload individual module ALCs for package modules — the package ALC is
            // unloaded atomically below. Only a module that moved here from a bare DLL has its own.
            if (oldInfo?.Alc is not null && oldInfo.DllPath != tpkgPath)
            {
                oldInfo.Alc.TryUnload();
                if (!_collectible)
                    Interlocked.Increment(ref _leakedAlcCount);
            }
            if (!newNames.Contains(name))
            {
                // A module the new version no longer ships: stop its context BEFORE the old package (and
                // its extracted resources) is disposed below — it would otherwise keep running on it.
                await RemoveModuleContextAsync(tpkgPath, name).ConfigureAwait(false);
                _loadedModules.TryRemove(name, out _);
            }
            _previousVersions.TryRemove(name, out _);
        }

        // Old package goes away only now — the old context (and its file= resource reads) is gone.
        if (oldPackage is not null)
        {
            oldPackage.Dispose();
            if (!_collectible)
                Interlocked.Increment(ref _leakedAlcCount);
        }

        // Update package tracking. Record the tracked write-time only now, after a successful reload
        // (4.11) — the scan loop no longer records it eagerly, so a failed reload (validation/open
        // returned 0 above) is retried on the next scan instead of being permanently skipped.
        _packageModules[tpkgPath] = newModuleNames;
        _packageInstances[tpkgPath] = package;
        _ignoredDlls[tpkgPath] = package.LastWriteUtc;
        _additionStability.TryRemove(tpkgPath, out _);

        _logger.LogInformation("Package {Pkg} reloaded: {Count} modules",
            Path.GetFileName(tpkgPath), newModuleNames.Count);

        return reloaded;
    }

    /// <summary>
    /// Stops the context of a module that leaves with a package reload or rollback. A failure is logged,
    /// not thrown: the reload or rollback must still finish its bookkeeping.
    /// </summary>
    private async Task RemoveModuleContextAsync(string tpkgPath, string moduleName)
    {
        try
        {
            await _coordinator.ProcessModuleRemovedAsync(moduleName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Package {Pkg}: stopping the context of module {Module} failed; it may still be running",
                Path.GetFileName(tpkgPath), moduleName);
        }
    }

    /// <summary>
    /// Puts the previous version of a package back after its new version failed to come up in
    /// <see cref="ReloadPackageAsync(string, byte[], List{string}, CancellationToken)"/>: the old instances
    /// return to the registry and their contexts are recreated (a module re-initializes on a fresh
    /// context, as a named-context recreation always does), a module only the new version added has its
    /// context stopped, and the new package is disposed only after that. The broken file's write time is
    /// recorded, so the scan does not re-read the same bytes every interval — the next reload happens
    /// when the file changes.
    /// </summary>
    private async Task RestorePreviousPackageAsync(string tpkgPath, ModulePackage newPackage,
        ModulePackage? oldPackage, List<(string Name, ITsakModule? Module, LoadedModuleInfo? Info)> oldModules,
        List<string> newModuleNames)
    {
        // Old instances back into the registry first: stopping a new-only module below recreates a named
        // context from the registry, which must already hold the old versions by then.
        var restorable = new List<ITsakModule>(oldModules.Count);
        foreach (var (name, module, info) in oldModules)
        {
            if (info is not null)
                _loadedModules[name] = info;
            else
                _loadedModules.TryRemove(name, out _);

            if (module is null)
                continue;
            await _registry.ReplaceModuleSilentAsync(module);
            restorable.Add(module);
        }

        // A module only the NEW version added was already activated by the batch: stop its context before
        // the new package (its code and resources) is disposed below.
        var oldNames = new HashSet<string>(oldModules.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var name in newModuleNames)
        {
            if (oldNames.Contains(name))
                continue; // replaced by the old instance above
            await RemoveModuleContextAsync(tpkgPath, name).ConfigureAwait(false);
            _registry.UnregisterModuleSilent(name);
            _loadedModules.TryRemove(name, out _);
        }

        if (oldPackage is not null)
            _packageInstances[tpkgPath] = oldPackage;
        // _packageModules[tpkgPath] still lists the old names — this path never overwrote it.
        _ignoredDlls[tpkgPath] = newPackage.LastWriteUtc;
        _additionStability.TryRemove(tpkgPath, out _);

        var restored = ModuleActivationReport.Success;
        Exception? restoreException = null;
        if (restorable.Count > 0)
        {
            try
            {
                restored = await _coordinator.ProcessBatchAsync(restorable).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                restoreException = ex; // reported as critical right below
            }
        }

        // The new package goes away only after the old contexts replaced the new ones.
        newPackage.Dispose();
        if (!_collectible)
            Interlocked.Increment(ref _leakedAlcCount);

        if (restoreException is not null || !restored.Succeeded || restorable.Count == 0)
        {
            _logger.LogCritical(restoreException ?? restored.Failures.FirstOrDefault()?.Exception,
                "Package {Pkg}: the new version failed and the previous version could not be fully restored — its modules may be down. "
                + "Deploy a working package; this file is skipped until it changes.",
                Path.GetFileName(tpkgPath));
        }
        else
        {
            _logger.LogWarning(
                "Package {Pkg}: rolled back to the previous version. The new file is skipped until it changes.",
                Path.GetFileName(tpkgPath));
        }
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
        // Once we unregister the old module the registry is on the swap-in-progress state; ANY later
        // failure must roll back to the old version, not leave the registry pointing at a broken new
        // module (review item 3.2).
        var registryMutated = false;

        try
        {
            // 1. Load new assembly in a fresh ALC (from bytes — file never locked). NOTE: we do NOT
            //    publish it to the shared LoadedAssemblyTracker yet — other modules must not resolve
            //    the new, unvalidated-and-unstarted assembly. The tracker is switched only after the
            //    new version has started successfully (step 8).
            newAlc = new ModuleAssemblyLoadContext(Path.GetFileNameWithoutExtension(newDllPath), _assemblyProbePaths, _collectible);
            var fullDllPath = Path.GetFullPath(newDllPath);
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
            //    autoStart=false survives). Same pattern as ReloadPackageAsync. The instance is kept:
            //    for a version loaded outside hot reload (startup discovery) it is the only copy of the
            //    old code Tsak has once the file is overwritten in place.
            var oldModule = _registry.GetModule(moduleName);
            _registry.UnregisterModuleSilent(moduleName);
            registryMutated = true;

            // 5. Archive old version for rollback
            if (_loadedModules.TryGetValue(moduleName, out var oldInfo))
            {
                var history = _previousVersions.GetOrAdd(moduleName, _ => new List<LoadedModuleInfo>());
                lock (history)
                {
                    history.Insert(0, oldInfo with { LiveModule = oldModule });
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
            await _registry.ReplaceModuleSilentAsync(newModule);

            // 7. Start new module context. ANY failure here rolls back to the old version — otherwise the
            //    old module is unregistered and the registry is left on a broken new one. "Failure" is an
            //    exception from the coordinator AND a module the coordinator REPORTS as not come up: it
            //    swallows Initialize/start exceptions so a bad module cannot crash the node, so the report
            //    is the only way a swap learns its new version never started.
            Exception? startFailure;
            try
            {
                var report = await _coordinator.ProcessModuleAddedAsync(newModule).ConfigureAwait(false);
                startFailure = report.FailureFor(moduleName)?.Exception;
            }
            catch (Exception ex)
            {
                startFailure = ex; // logged right below, together with the reported case
            }

            if (startFailure is not null)
            {
                _logger.LogError(startFailure, "New version of {Module} failed to start, rolling back to previous version",
                    moduleName);
                var rolledBack = await RollbackAsync(moduleName, ct, overwrittenPath: newDllPath).ConfigureAwait(false);
                newAlc?.TryUnload(); // tracker was never switched to the new bytes — nothing to revert there
                if (!rolledBack)
                    _logger.LogError("Rollback of {Module} after failed swap did not restore a previous version — module may be down",
                        moduleName);
                // The failed file stays newer than the tracked version, so every scan swapped it in again:
                // the live module was torn down and rolled back once per scan interval, with a fresh ALC
                // each time. It is retried only once the file changes.
                IgnoreUntilChanged(newDllPath);
                return false;
            }

            // 8. Started successfully — finalize tracking. The module's OWN entry assembly is NOT
            //    published to the shared LoadedAssemblyTracker: it lives only in newAlc (isolated),
            //    exactly as the .tpkg path treats entry points (ModulePackage — entry points are never
            //    tracked). Publishing it would Assembly.Load a second, non-unloadable copy into the
            //    Default ALC (a leak that isn't even counted in _leakedAlcCount) and risk a type-identity
            //    split-brain; cross-module shared types must flow through the shared/companion layer,
            //    which IS tracked. (Review item F-7.)
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
            if (registryMutated)
            {
                // We already unregistered the old module (steps 4–6 mutated the registry). Restore the
                // old version rather than leaving the registry on a half-swapped/broken module.
                _logger.LogError(ex, "Hot swap of {Module} failed after unregistering the old version, rolling back", moduleName);
                var rolledBack = await RollbackAsync(moduleName, ct, overwrittenPath: newDllPath).ConfigureAwait(false);
                newAlc?.TryUnload();
                if (!rolledBack)
                    _logger.LogError("Rollback of {Module} did not restore a previous version — module may be down", moduleName);
            }
            else
            {
                // Failure before switching (load/discover/validate). The old module is untouched.
                _logger.LogError(ex, "Hot swap failed for {Module} before switching, staying on current version", moduleName);
                newAlc?.TryUnload();
            }

            return false;
        }
    }

    /// <summary>
    /// Rolls back a module to the previous version.
    /// <paramref name="overwrittenPath"/> — the path the FAILED new version was loaded from
    /// (passed by HotSwapAsync): when it equals the previous version's path, the update was
    /// in-place and the file on disk now holds the broken bytes — reloading it would be a fake
    /// rollback (review 2026-09-02, С21).
    /// Returns true if rollback succeeded.
    /// </summary>
    public async Task<bool> RollbackAsync(string moduleName, CancellationToken ct = default,
        string? overwrittenPath = null)
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
            ITsakModule? oldModule;
            if (previous.Alc is null && previous.LiveModule is { } liveModule)
            {
                // The previous version was loaded outside hot reload (startup discovery): its code is
                // still in the process, and the swap kept its module instance. Roll back onto that
                // instance — the standard deployment overwrites the DLL in place, so the file now holds
                // the bytes being rolled back FROM and there is no old copy on disk to re-read.
                await _coordinator.ProcessModuleRemovedAsync(moduleName).ConfigureAwait(false);
                oldModule = liveModule;
            }
            else
            {
                System.Reflection.Assembly rollbackAssembly;
                if (previous.Alc is not null && previous.Alc.Assemblies.Any())
                {
                    // The previous version's code is STILL LOADED — roll back onto the live instance.
                    // Re-reading previous.DllPath is wrong for an in-place update: old and new share
                    // the path, the file now holds the bytes being rolled back FROM, and the old code
                    // "rolled back" into the broken version in a retry loop (review 2026-09-02, С21).
                    await _coordinator.ProcessModuleRemovedAsync(moduleName).ConfigureAwait(false);
                    rollbackAssembly = previous.Alc.Assemblies.First();
                }
                else if (overwrittenPath is not null
                         && string.Equals(Path.GetFullPath(previous.DllPath), Path.GetFullPath(overwrittenPath),
                             StringComparison.OrdinalIgnoreCase))
                {
                    // No live instance and the failed new version was written to the SAME path — the
                    // disk no longer holds the previous version. A fake rollback would load the broken
                    // bytes in a retry loop; refuse loudly instead and keep the history entry.
                    _logger.LogCritical(
                        "Rollback of {Module} impossible: {Path} was overwritten in place and no live instance of "
                        + "version {Version} remains — deploy a known-good file.",
                        moduleName, previous.DllPath, previous.Version);
                    lock (history) history.Insert(0, previous);
                    return false;
                }
                else
                {
                    // Stop current version
                    await _coordinator.ProcessModuleRemovedAsync(moduleName).ConfigureAwait(false);

                    // Re-discover and register old version (load via ALC from bytes — no lock)
                    var rollbackAlc = new ModuleAssemblyLoadContext(Path.GetFileNameWithoutExtension(previous.DllPath), _assemblyProbePaths, _collectible);
                    // Entry assembly stays ALC-private — NOT republished to the shared tracker (see the F-7 note
                    // in HotSwapAsync step 8). Republishing would leak a duplicate copy into the Default ALC.
                    rollbackAssembly = rollbackAlc.LoadFromBytes(previous.DllPath);
                }
                var rollbackSourceDir = Path.GetDirectoryName(Path.GetFullPath(previous.DllPath));
                var modules = TsakModuleRegistry.DiscoverModulesInAssembly(rollbackAssembly, rollbackSourceDir);
                oldModule = modules.FirstOrDefault(m =>
                    m.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase));

                if (oldModule is null)
                {
                    _logger.LogError("Failed rollback: module {Module} not found in previous assembly {Path}",
                        moduleName, previous.DllPath);
                    return false;
                }
            }

            await _registry.ReplaceModuleSilentAsync(oldModule);
            var report = await _coordinator.ProcessModuleAddedAsync(oldModule).ConfigureAwait(false);

            _loadedModules[moduleName] = previous;
            if (report.FailureFor(moduleName) is { } failure)
            {
                // The previous version is registered and tracked again, but it did not come up either —
                // the coordinator swallowed the exception (a bad module must not crash the node).
                _logger.LogCritical(failure.Exception,
                    "Rollback of {Module} to version {Version} failed: the previous version did not start either — the module is down",
                    moduleName, previous.Version);
                return false;
            }

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
    /// Skips <paramref name="path"/> on later scans until its write time moves: the same rule that keeps a
    /// non-module dependency from being re-read every scan, applied to a file that failed.
    /// </summary>
    private void IgnoreUntilChanged(string path)
    {
        _ignoredDlls[path] = File.GetLastWriteTimeUtc(path);
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

            // Third convention (Route-XML Ф5.2): an XML route module is keyed by the manifest
            // name — without this an adopted XML-only package had no _packageModules entry, so
            // its redrop took the new-package path instead of ReloadPackageAsync.
            if (package.Manifest.Artifacts.Count > 0)
            {
                moduleNames.Add(package.Manifest.Name);
                _loadedModules[package.Manifest.Name] = new LoadedModuleInfo(
                    package.Alc, tpkgPath, package.Manifest.Version);
            }

            if (moduleNames.Count > 0)
                _packageModules[tpkgPath] = moduleNames;

            _logger.LogDebug("Adopted startup package {Pkg}: {Count} modules",
                Path.GetFileName(tpkgPath), moduleNames.Count);
        }
    }

    public void Dispose()
    {
        // Idempotent (4.12): a second Dispose is a no-op.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // Wait for any in-progress scan to finish before unloading its ALCs (coordinate, not just via
        // a cancellation token). A scan blocked on the gate here will see _disposed and bail on release.
        _scanGate.Wait();
        try
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
        finally
        {
            _scanGate.Release();
        }
    }

    private static string GetAssemblyVersion(string dllPath)
    {
        var lastWrite = File.GetLastWriteTimeUtc(dllPath);
        return lastWrite.ToString("yyyy.MM.dd.HHmm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <param name="LiveModule">
    /// The instance that ran this version, kept from the moment it was swapped out. For a version loaded
    /// outside hot reload (<see cref="Alc"/> is null: startup discovery) it is the only copy of that code once
    /// the file is overwritten in place, and a rollback re-activates it.
    /// </param>
    internal sealed record LoadedModuleInfo(
        ModuleAssemblyLoadContext? Alc,
        string DllPath,
        string Version,
        ITsakModule? LiveModule = null);
}
