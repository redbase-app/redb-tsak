using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Modules;

namespace redb.Tsak.Core.Services;

/// <summary>
/// Thread-safe module registry with assembly scanning, batch events, and version tracking.
/// Based on lt.tsak RouteModuleRegistry, redesigned for redb.Route.
/// </summary>
public class TsakModuleRegistry : ITsakModuleRegistry
{
    private readonly ConcurrentDictionary<string, ITsakModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _moduleFilePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ModulePackage> _loadedPackages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITsakModuleStore _store;
    private readonly ILogger<TsakModuleRegistry> _logger;

    /// <summary>Packages loaded during startup discovery. Consumed by HotReloadService.AdoptStartupPackages().</summary>
    public IReadOnlyDictionary<string, ModulePackage> LoadedPackages => _loadedPackages;

    public TsakModuleRegistry(ITsakModuleStore store, ILogger<TsakModuleRegistry> logger)
    {
        _store = store;
        _logger = logger;
    }

    public event EventHandler<ITsakModule>? ModuleAdded;
    public event EventHandler<string>? ModuleRemoved;
    public event EventHandler<ITsakModule>? ModuleUpdated;
    public event EventHandler<IReadOnlyList<ITsakModule>>? ModulesBatchAdded;

    public async Task<int> RegisterStaticProvidersAsync(IEnumerable<IStaticModuleProvider> providers)
    {
        var batchModules = new List<ITsakModule>();

        foreach (var provider in providers.OrderByDescending(p => p.Priority))
        {
            if (!provider.CanInitialize())
            {
                _logger.LogWarning("Static provider {Provider} cannot initialize, skipping", provider.ProviderName);
                continue;
            }

            foreach (var module in provider.GetStaticModules())
            {
                if (_modules.TryAdd(module.ModuleName, module))
                {
                    batchModules.Add(module);
                    await _store.SaveAsync(new TsakModuleRecord
                    {
                        ModuleName = module.ModuleName,
                        Version = module.Version,
                        Description = module.Description,
                        Dependencies = module.Dependencies.ToList(),
                        Status = TsakModuleStatus.Loaded
                    });
                    _logger.LogInformation("Registered static module {Module} v{Version} from {Provider}",
                        module.ModuleName, module.Version, provider.ProviderName);
                }
                else
                {
                    _logger.LogDebug("Module {Module} already registered, skipping", module.ModuleName);
                }
            }
        }

        if (batchModules.Count > 0)
            ModulesBatchAdded?.Invoke(this, batchModules);

        _logger.LogInformation("Registered {Count} modules from static providers", batchModules.Count);
        return batchModules.Count;
    }

    public async Task<int> DiscoverModulesAsync(IEnumerable<string> assemblyPaths)
        => await DiscoverModulesAsync(assemblyPaths, probePaths: null, collectible: false);

    /// <summary>
    /// Discovers modules in assemblies and .tpkg packages at the specified paths.
    /// Packages are opened with the given <paramref name="probePaths"/> and <paramref name="collectible"/>
    /// settings and stored in <see cref="LoadedPackages"/> for HotReloadService adoption.
    /// </summary>
    public async Task<int> DiscoverModulesAsync(IEnumerable<string> assemblyPaths,
        string[]? probePaths, bool collectible)
    {
        var added = new List<ITsakModule>();
        var updated = new List<ITsakModule>();
        var removed = new List<string>();

        // Detect deleted assemblies
        foreach (var (moduleName, filePath) in _moduleFilePaths.ToArray())
        {
            if (!File.Exists(filePath))
            {
                if (_modules.TryRemove(moduleName, out _))
                {
                    _moduleFilePaths.TryRemove(moduleName, out _);
                    removed.Add(moduleName);
                    await _store.RemoveAsync(moduleName);
                    _logger.LogInformation("Removed module {Module} (assembly deleted: {Path})", moduleName, filePath);
                }
            }
        }

        foreach (var path in assemblyPaths)
        {
            if (!Directory.Exists(path))
            {
                _logger.LogWarning("Assembly path {Path} does not exist, skipping", path);
                continue;
            }

            foreach (var dll in Directory.GetFiles(path, "*.dll"))
            {
                try
                {
                    var modules = DiscoverModulesInAssembly(dll);
                    foreach (var module in modules)
                    {
                        _moduleFilePaths[module.ModuleName] = dll;

                        if (_modules.TryGetValue(module.ModuleName, out var existing))
                        {
                            if (string.Compare(module.Version, existing.Version, StringComparison.Ordinal) > 0)
                            {
                                _modules[module.ModuleName] = module;
                                updated.Add(module);
                                await _store.SaveAsync(new TsakModuleRecord
                                {
                                    ModuleName = module.ModuleName,
                                    Version = module.Version,
                                    Description = module.Description,
                                    Dependencies = module.Dependencies.ToList(),
                                    Status = TsakModuleStatus.Loaded,
                                    AssemblyPath = dll,
                                    LastUpdatedAt = DateTimeOffset.UtcNow
                                });
                                _logger.LogInformation("Updated module {Module} v{OldVer} → v{NewVer}",
                                    module.ModuleName, existing.Version, module.Version);
                                (existing as IDisposable)?.Dispose();
                            }
                        }
                        else
                        {
                            _modules[module.ModuleName] = module;
                            added.Add(module);
                            await _store.SaveAsync(new TsakModuleRecord
                            {
                                ModuleName = module.ModuleName,
                                Version = module.Version,
                                Description = module.Description,
                                Dependencies = module.Dependencies.ToList(),
                                Status = TsakModuleStatus.Loaded,
                                AssemblyPath = dll
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to scan assembly {Path}", dll);
                }
            }

            // Scan .tpkg packages (CAR-style deployment)
            foreach (var tpkg in Directory.GetFiles(path, "*.tpkg"))
            {
                try
                {
                    var package = ModulePackage.Open(tpkg, probePaths: probePaths,
                        logger: _logger, collectible: collectible);
                    if (package is null)
                        continue;

                    var tpkgFullPath = Path.GetFullPath(tpkg);
                    var packageModuleFound = false;

                    foreach (var assembly in package.LoadedAssemblies)
                    {
                        var sourceDir = Path.GetDirectoryName(tpkgFullPath);
                        var modules = DiscoverModulesInAssembly(assembly, sourceDir);
                        foreach (var module in modules)
                        {
                            packageModuleFound = true;

                            // Per-module config from package ({moduleName}.config.json)
                            if (module is Modules.StaticMethodModule smm)
                                smm.EmbeddedConfigJson = package.ReadModuleConfigJson(module.ModuleName);

                            _moduleFilePaths[module.ModuleName] = tpkg;

                            if (_modules.TryGetValue(module.ModuleName, out var existing))
                            {
                                _modules[module.ModuleName] = module;
                                updated.Add(module);
                                await _store.SaveAsync(new TsakModuleRecord
                                {
                                    ModuleName = module.ModuleName,
                                    Version = module.Version,
                                    Description = module.Description,
                                    Dependencies = module.Dependencies.ToList(),
                                    Status = TsakModuleStatus.Loaded,
                                    AssemblyPath = tpkg,
                                    LastUpdatedAt = DateTimeOffset.UtcNow
                                });
                                _logger.LogInformation("Updated module {Module} from package {Pkg}",
                                    module.ModuleName, package.Manifest.Name);
                                (existing as IDisposable)?.Dispose();
                            }
                            else
                            {
                                _modules[module.ModuleName] = module;
                                added.Add(module);
                                await _store.SaveAsync(new TsakModuleRecord
                                {
                                    ModuleName = module.ModuleName,
                                    Version = module.Version,
                                    Description = module.Description,
                                    Dependencies = module.Dependencies.ToList(),
                                    Status = TsakModuleStatus.Loaded,
                                    AssemblyPath = tpkg
                                });
                                _logger.LogInformation("Discovered module {Module} v{Ver} from package {Pkg}",
                                    module.ModuleName, module.Version, package.Manifest.Name);
                            }
                        }
                    }

                    // Store package for HotReloadService adoption (owns the ALC)
                    if (packageModuleFound)
                        _loadedPackages[tpkgFullPath] = package;
                    else
                        package.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to scan package {Path}", tpkg);
                }
            }
        }

        // Fire events
        foreach (var name in removed)
            ModuleRemoved?.Invoke(this, name);
        foreach (var m in updated)
            ModuleUpdated?.Invoke(this, m);
        if (added.Count > 0)
            ModulesBatchAdded?.Invoke(this, added);

        var total = added.Count + updated.Count + removed.Count;
        _logger.LogInformation("Discovery scan: {Added} added, {Updated} updated, {Removed} removed",
            added.Count, updated.Count, removed.Count);
        return total;
    }

    public void RegisterModule(ITsakModule module)
    {
        if (_modules.TryAdd(module.ModuleName, module))
        {
            _store.SaveAsync(new TsakModuleRecord
            {
                ModuleName = module.ModuleName,
                Version = module.Version,
                Description = module.Description,
                Dependencies = module.Dependencies.ToList(),
                Status = TsakModuleStatus.Loaded
            }).GetAwaiter().GetResult();

            _logger.LogInformation("Registered module {Module} v{Version}", module.ModuleName, module.Version);
            ModuleAdded?.Invoke(this, module);
        }
        else
        {
            _logger.LogWarning("Module {Module} already registered", module.ModuleName);
        }
    }

    public bool UnregisterModule(string moduleName)
    {
        if (!_modules.TryRemove(moduleName, out var removed))
            return false;

        _moduleFilePaths.TryRemove(moduleName, out _);
        _store.RemoveAsync(moduleName).GetAwaiter().GetResult();

        _logger.LogInformation("Unregistered module {Module}", moduleName);
        ModuleRemoved?.Invoke(this, moduleName);
        (removed as IDisposable)?.Dispose();
        return true;
    }

    /// <summary>
    /// Removes a module from the registry without firing events.
    /// Used during package reload to avoid triggering ProcessModuleRemovedAsync
    /// (which would delete persisted state like autoStart).
    /// </summary>
    public bool UnregisterModuleSilent(string moduleName)
    {
        if (!_modules.TryRemove(moduleName, out var removed))
            return false;

        _moduleFilePaths.TryRemove(moduleName, out _);
        _logger.LogDebug("Unregistered module {Module} silently (no events)", moduleName);
        (removed as IDisposable)?.Dispose();
        return true;
    }

    /// <summary>
    /// Replaces a module in the registry without firing any events.
    /// Used by HotReloadService to avoid double context creation.
    /// </summary>
    public void ReplaceModuleSilent(ITsakModule module)
    {
        var old = _modules.GetValueOrDefault(module.ModuleName);
        _modules[module.ModuleName] = module;
        _store.SaveAsync(new TsakModuleRecord
        {
            ModuleName = module.ModuleName,
            Version = module.Version,
            Description = module.Description,
            Dependencies = module.Dependencies.ToList(),
            Status = TsakModuleStatus.Loaded
        }).GetAwaiter().GetResult();
        (old as IDisposable)?.Dispose();
        _logger.LogDebug("Replaced module {Module} silently (no events)", module.ModuleName);
    }

    public IReadOnlyList<ITsakModule> GetAllModules() => _modules.Values.ToList();

    public ITsakModule? GetModule(string moduleName) => _modules.GetValueOrDefault(moduleName);

    public IReadOnlyList<ITsakModule> GetModulesByStatus(TsakModuleStatus status) =>
        _modules.Values.Where(m => m.Status == status).ToList();

    /// <summary>
    /// Discovers ITsakModule implementations and InitRoute.main() conventions in an assembly.
    /// Uses <see cref="Modules.LoadedAssemblyTracker"/> for dedup — if the same DLL was already
    /// loaded (by another module or a .tpkg package), the existing Assembly instance is reused.
    /// </summary>
    internal static List<ITsakModule> DiscoverModulesInAssembly(string assemblyPath)
    {
        // Load from bytes to avoid locking the file (enables hot-copy while running)
        var bytes = File.ReadAllBytes(assemblyPath);
        var asmName = Path.GetFileNameWithoutExtension(assemblyPath);
        var assembly = Modules.LoadedAssemblyTracker.LoadOrReuse(asmName, bytes);
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
        return DiscoverModulesInAssembly(assembly, sourceDir);
    }

    internal static List<ITsakModule> DiscoverModulesInAssembly(Assembly assembly, string? sourceDirectory = null, string? embeddedConfigJson = null)
    {
        var modules = new List<ITsakModule>();

        // 1. Find ITsakModule implementations
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsInterface)
                continue;

            if (typeof(ITsakModule).IsAssignableFrom(type))
            {
                if (Activator.CreateInstance(type) is ITsakModule module)
                    modules.Add(module);
            }
        }

        // 2. Find InitRoute.main(IRouteContext) convention
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.Name != "InitRoute")
                continue;

            var mainMethod = type.GetMethod("main", BindingFlags.Public | BindingFlags.Static);
            if (mainMethod == null)
                continue;

            var parameters = mainMethod.GetParameters();
            if (parameters.Length != 1)
                continue;

            // The parameter must be IRouteContext (or RouteContext)
            var paramType = parameters[0].ParameterType;
            if (paramType.Name is not ("IRouteContext" or "RouteContext"))
                continue;

            var returnType = mainMethod.ReturnType;
            if (returnType.Name is not ("IRouteContext" or "RouteContext"))
                continue;

            var moduleName = type.Namespace ?? type.Assembly.GetName().Name ?? "Unknown";
            var version = assembly.GetName().Version?.ToString() ?? "0.0.0";

            modules.Add(new Modules.StaticMethodModule(moduleName, version, mainMethod, sourceDirectory, embeddedConfigJson));
        }

        return modules;
    }
}
