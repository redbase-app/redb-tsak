using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using redb.Tsak.Core.Services;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Loads assemblies from the shared layer (<c>Libs/shared/</c>) into the Default ALC.
/// These assemblies (connectors, shared models) become available to all module contexts
/// via <c>RouteContextExtensions.AddComponents()</c>.
/// Tracks file timestamps for change detection during hot-reload scans.
/// </summary>
public sealed class SharedAssemblyLoader
{
    private readonly ILogger<SharedAssemblyLoader> _logger;
    private readonly string _sharedPath;
    // Published snapshot of loaded shared assemblies. Immutable + swapped atomically under _loadLock,
    // so a concurrent reader (TsakContextManager.CreateContext enumerates LoadedAssemblies during a
    // hot-reload) never sees a torn/cleared set — review item 3.4.
    private ImmutableArray<Assembly> _loadedAssemblies = ImmutableArray<Assembly>.Empty;
    private readonly object _loadLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _fileTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private bool _resolvingHandlerRegistered;

    public SharedAssemblyLoader(
        ILogger<SharedAssemblyLoader> logger,
        IOptions<HotReloadOptions> options)
    {
        _logger = logger;
        var configuredPath = options.Value.SharedPath ?? "Libs/shared";
        // Resolve relative paths against AppContext.BaseDirectory (bin output),
        // not CWD — so Libs/shared works for both dotnet run and deployed scenarios
        _sharedPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
    }

    /// <summary>Assemblies currently loaded from the shared layer.</summary>
    public IReadOnlyList<Assembly> LoadedAssemblies => _loadedAssemblies;

    /// <summary>Resolved absolute path to the shared directory.</summary>
    public string SharedPath => _sharedPath;

    /// <summary>
    /// Loads all DLLs from the shared directory into the Default ALC.
    /// Registers a <c>Default.Resolving</c> handler to probe the shared path
    /// for transitive dependencies.
    /// Safe to call multiple times — skips already-loaded files.
    /// </summary>
    /// <returns>Number of newly loaded assemblies.</returns>
    public int LoadSharedAssemblies()
    {
        var scanned = ScanAndLoad(out var loaded);
        if (scanned.Count > 0)
            lock (_loadLock)
                _loadedAssemblies = _loadedAssemblies.AddRange(scanned); // append (incremental-safe)
        return loaded;
    }

    /// <summary>
    /// Scans the shared directory and loads any not-yet-timestamped DLLs into the Default ALC.
    /// Returns the assemblies to publish (reused-preloaded + newly loaded) and, via <paramref name="loaded"/>,
    /// the count genuinely loaded here. Updates <see cref="_fileTimestamps"/> but does NOT touch the
    /// published <see cref="_loadedAssemblies"/> — the caller publishes atomically under the lock.
    /// </summary>
    private List<Assembly> ScanAndLoad(out int loaded)
    {
        loaded = 0;
        var result = new List<Assembly>();

        var fullPath = Path.GetFullPath(_sharedPath);
        if (!Directory.Exists(fullPath))
        {
            _logger.LogDebug("Shared path {Path} does not exist, skipping", fullPath);
            return result;
        }

        RegisterResolvingHandler(fullPath);

        // Prepare native lib dirs for per-assembly resolvers
        var nativeDirs = GetNativeLibraryDirs(fullPath);

        var dllFiles = Directory.GetFiles(fullPath, "*.dll");

        foreach (var dll in dllFiles)
        {
            if (_fileTimestamps.ContainsKey(dll))
                continue;

            // Skip anything the early bootstrap (SharedRuntime.InstallEarly) already byte-loaded
            // — e.g. framework assemblies preloaded before the host was built. Re-loading via
            // LoadFromStream would create a duplicate Assembly instance. We still record the
            // timestamp so hot-reload change detection covers these files. Matched by assembly
            // simple name (file name == assembly name for redb.* framework DLLs).
            var simpleName = Path.GetFileNameWithoutExtension(dll);
            if (LoadedAssemblyTracker.TryGet(simpleName, out var preloaded) && preloaded is not null)
            {
                result.Add(preloaded);
                _fileTimestamps[dll] = File.GetLastWriteTimeUtc(dll);
                // Surface framework/providers that the early bootstrap (SharedRuntime) byte-loaded
                // before Serilog existed — otherwise they are invisible in the log file, unlike the
                // connectors loaded below. Logged here (host is up) so redb.Core/redb.SQLite/... show.
                _logger.LogInformation("Shared assembly {Name} {Version} preloaded from shared (framework)",
                    preloaded.GetName().Name, preloaded.GetName().Version);
                continue; // not counted as newly loaded by this loader
            }

            try
            {
                var bytes = File.ReadAllBytes(dll);
                var assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(bytes));
                result.Add(assembly);
                _fileTimestamps[dll] = File.GetLastWriteTimeUtc(dll);
                loaded++;

                // Track in centralized registry so package/bare-DLL loaders
                // reuse this instance instead of loading a duplicate
                LoadedAssemblyTracker.Track(assembly);

                // Register native library resolver for this assembly
                if (nativeDirs.Count > 0)
                    RegisterNativeResolverForAssembly(assembly, nativeDirs);

                _logger.LogInformation("Loaded shared assembly {Name} from {Path}",
                    assembly.GetName().Name, dll);
            }
            catch (FileLoadException ex)
            {
                // Version/identity mismatch — check if the runtime already provides this assembly.
                _fileTimestamps[dll] = File.GetLastWriteTimeUtc(dll);
                var asmName = Path.GetFileNameWithoutExtension(dll);
                var alreadyLoaded = AssemblyLoadContext.Default.Assemblies
                    .Any(a => string.Equals(a.GetName().Name, asmName, StringComparison.OrdinalIgnoreCase));

                if (alreadyLoaded)
                {
                    // Runtime already has this assembly — the shared/ copy is redundant.
                    // Remove it from shared/ to avoid noise on next startup.
                    _logger.LogInformation(
                        "Shared assembly {Name} conflicts with runtime version — runtime copy will be used. " +
                        "Consider removing {Path} from shared/ (run build-shared.ps1 -Clean)",
                        asmName, dll);
                }
                else
                {
                    // Assembly is NOT available from runtime and failed to load — real error.
                    _logger.LogError(ex,
                        "Failed to load shared assembly {Path} — version conflict and no runtime fallback available",
                        dll);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load shared assembly {Path}", dll);
            }
        }

        if (loaded > 0)
            _logger.LogInformation("Loaded {Count} shared assemblies from {Path}", loaded, fullPath);

        return result;
    }

    /// <summary>
    /// Checks if any shared DLLs have changed (added, updated, or removed) since last load.
    /// </summary>
    /// <returns>True if changes detected; caller should trigger full context restart.</returns>
    public bool DetectChanges()
    {
        var fullPath = Path.GetFullPath(_sharedPath);
        if (!Directory.Exists(fullPath))
            return _fileTimestamps.Count > 0; // directory removed = change

        var currentFiles = new HashSet<string>(
            Directory.GetFiles(fullPath, "*.dll"),
            StringComparer.OrdinalIgnoreCase);

        // Check for new or modified files
        foreach (var dll in currentFiles)
        {
            var lastWrite = File.GetLastWriteTimeUtc(dll);
            if (!_fileTimestamps.TryGetValue(dll, out var tracked) || tracked != lastWrite)
                return true;
        }

        // Check for removed files
        foreach (var tracked in _fileTimestamps.Keys)
        {
            if (!currentFiles.Contains(tracked))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reloads all shared assemblies. Since Default ALC can't unload,
    /// this replaces the assembly list and timestamps. Old assemblies remain in memory
    /// until process restart.
    /// </summary>
    /// <returns>Number of assemblies in the new load.</returns>
    public int ReloadSharedAssemblies()
    {
        _logger.LogInformation("Reloading shared assemblies from {Path}", _sharedPath);
        _fileTimestamps.Clear();

        // Build the fresh set, THEN swap atomically — readers never observe an empty/torn set mid-reload
        // (review item 3.4). Assemblies stay in the Default ALC (it can't unload); already-tracked ones
        // are reused via the preloaded path in ScanAndLoad, so no duplicate loads.
        var scanned = ScanAndLoad(out var loaded);
        lock (_loadLock)
            _loadedAssemblies = ImmutableArray.CreateRange(scanned);
        return loaded;
    }

    private void RegisterResolvingHandler(string fullSharedPath)
    {
        if (_resolvingHandlerRegistered)
            return;

        // Single implementation shared with the early (DI-free) bootstrap so the handler is
        // installed only once per path — see SharedRuntime.EnsureSharedPathResolver. It performs
        // exactly what this method used to inline: tracker resolver first, then shared-dir probe
        // for transitive deps, then version-tolerant forwarding for higher-version references.
        SharedRuntime.EnsureSharedPathResolver(fullSharedPath, msg => _logger.LogDebug("{Msg}", msg));

        _resolvingHandlerRegistered = true;
    }

    private static List<string> GetNativeLibraryDirs(string fullSharedPath)
    {
        var runtimesPath = Path.Combine(fullSharedPath, "runtimes");
        if (!Directory.Exists(runtimesPath))
            return [];

        var rid = RuntimeInformation.RuntimeIdentifier;
        var nativeDirs = new List<string>();

        // Exact RID first (e.g. win-x64, linux-x64)
        var exactDir = Path.Combine(runtimesPath, rid, "native");
        if (Directory.Exists(exactDir))
            nativeDirs.Add(exactDir);

        // Fallback: OS-only RID (e.g. win, linux, osx)
        var osOnly = rid.Contains('-') ? rid[..rid.IndexOf('-')] : rid;
        var osDir = Path.Combine(runtimesPath, osOnly, "native");
        if (Directory.Exists(osDir) && !nativeDirs.Contains(osDir))
            nativeDirs.Add(osDir);

        return nativeDirs;
    }

    /// <summary>
    /// Registers a native library resolver for a loaded shared assembly so its P/Invoke calls
    /// can find native libraries in Libs/shared/runtimes/{rid}/native/.
    /// </summary>
    internal void RegisterNativeResolverForAssembly(Assembly assembly, List<string> nativeDirs)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, (name, asm, searchPath) =>
                TryLoadNativeFromShared(name, nativeDirs));
        }
        catch (InvalidOperationException)
        {
            // Already has a resolver — ignore
        }
    }

    private static IntPtr TryLoadNativeFromShared(string libraryName, List<string> nativeDirs)
    {
        foreach (var dir in nativeDirs)
        {
            // Try exact name, then with platform extension
            var candidates = new[]
            {
                Path.Combine(dir, libraryName),
                Path.Combine(dir, libraryName + ".dll"),
                Path.Combine(dir, libraryName + ".so"),
                Path.Combine(dir, "lib" + libraryName + ".so"),
                Path.Combine(dir, libraryName + ".dylib"),
                Path.Combine(dir, "lib" + libraryName + ".dylib"),
            };

            foreach (var candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
            }
        }

        return IntPtr.Zero;
    }
}
