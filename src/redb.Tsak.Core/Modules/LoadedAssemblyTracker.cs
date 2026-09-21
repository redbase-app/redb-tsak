using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Centralized registry of byte-loaded assemblies across all Tsak loading paths.
/// Prevents type identity split-brain: the same assembly name always resolves to
/// the same <see cref="Assembly"/> instance regardless of which module loaded it first.
/// <para>
/// Provides a single <c>Default.Resolving</c> handler that serves all callers:
/// <see cref="ModulePackage"/>, <see cref="SharedAssemblyLoader"/>,
/// <see cref="ModuleAssemblyLoadContext"/>, and bare-DLL discovery.
/// </para>
/// </summary>
public static class LoadedAssemblyTracker
{
    private static readonly ConcurrentDictionary<string, Assembly> _assemblies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<Assembly>> _loadLocks = new(StringComparer.OrdinalIgnoreCase);
    private static int _resolverRegistered;

    /// <summary>
    /// Registers the single <c>Default.Resolving</c> handler.
    /// Safe to call multiple times — only the first call registers.
    /// Must be called before any byte-loaded assembly usage.
    /// </summary>
    public static void EnsureResolverRegistered()
    {
        if (Interlocked.CompareExchange(ref _resolverRegistered, 1, 0) == 0)
        {
            AssemblyLoadContext.Default.Resolving += (_, name) =>
                name.Name is not null && _assemblies.TryGetValue(name.Name, out var asm) ? asm : null;
        }
    }

    /// <summary>
    /// Returns an already-tracked assembly with the given name, the host's own instance when the host
    /// provides that name, or loads <paramref name="bytes"/> into the Default ALC and tracks it.
    /// Guarantees that the same assembly name always maps to a single <see cref="Assembly"/> instance.
    /// </summary>
    /// <param name="assemblyName">Caller's key — the assembly's simple name, or the file basename.</param>
    /// <param name="bytes">The bytes to load when the name is new to this process.</param>
    /// <param name="logger">Takes the note when the host's instance is reused instead of the bytes.</param>
    public static Assembly LoadOrReuse(string assemblyName, byte[] bytes, ILogger? logger = null)
    {
        EnsureResolverRegistered();

        // Fast path: reuse existing
        if (_assemblies.TryGetValue(assemblyName, out var existing))
            return existing;

        // The host may already provide this name — a shared-framework assembly above all: a module
        // library takes Microsoft.Extensions.* from NuGet, while the worker resolves those very names from
        // Microsoft.AspNetCore.App. Byte-loading the package's copy added a SECOND instance to the Default
        // context that nothing ever bound to (ModuleAssemblyLoadContext asks Default first, so the host's
        // copy wins every resolution) and that never unloads. Reuse what the host resolves; only a name the
        // host cannot resolve is loaded from the bytes. Same first step as ModuleAssemblyLoadContext.Load.
        if (TryLoadFromHost(assemblyName) is { } hostProvided)
        {
            var canonicalName = hostProvided.GetName().Name ?? assemblyName;
            var hostTracked = _assemblies.GetOrAdd(canonicalName, hostProvided);
            if (!string.Equals(canonicalName, assemblyName, StringComparison.OrdinalIgnoreCase))
                _assemblies.TryAdd(assemblyName, hostTracked);
            logger?.LogDebug(
                "Assembly {Name} is provided by the host — reusing that instance instead of loading the copy that came with the module",
                assemblyName);
            return hostTracked;
        }

        // Use Lazy<T> to guarantee only one Assembly.Load call per name,
        // even under concurrent access. Losers get the winner's Assembly.
        var lazy = _loadLocks.GetOrAdd(assemblyName, _ => new Lazy<Assembly>(() =>
        {
            var loaded = Assembly.Load(bytes);
            var actualName = loaded.GetName().Name ?? assemblyName;
            // The REAL simple name is the canonical key (it's what the runtime's Default.Resolving asks
            // for). If that identity is ALREADY tracked (e.g. loaded from shared/ or a prior call under
            // the matching name), the assembly we just loaded is a DUPLICATE — keep the canonical
            // instance and alias the caller's key to IT, never to the duplicate (review: ALC finding #2).
            // Then also alias under the caller's key (typically the file basename) so a later call with
            // the same basename reuses this instance rather than byte-loading a second copy (3.5).
            var canonical = _assemblies.GetOrAdd(actualName, loaded);
            if (!string.Equals(actualName, assemblyName, StringComparison.OrdinalIgnoreCase))
                _assemblies.TryAdd(assemblyName, canonical);
            return canonical;
        }));

        var result = lazy.Value;
        _loadLocks.TryRemove(assemblyName, out _);
        // Prefer the canonical (real-name) instance, then the caller-key alias, then the lazy result.
        return _assemblies.TryGetValue(result.GetName().Name ?? assemblyName, out var byReal) ? byReal
             : _assemblies.TryGetValue(assemblyName, out var tracked) ? tracked
             : result;
    }

    /// <summary>
    /// The host's own instance of <paramref name="assemblyName"/>, or null when the host cannot resolve
    /// that name. Resolution runs against the Default context: the shared frameworks and the worker's own
    /// directory, plus this tracker's own <c>Resolving</c> handler for names already byte-loaded here.
    /// </summary>
    private static Assembly? TryLoadFromHost(string assemblyName)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(assemblyName));
        }
        catch (FileNotFoundException) { return null; }   // the host does not have it — load the bytes
        catch (FileLoadException) { return null; }
        catch (BadImageFormatException) { return null; }
        catch (ArgumentException) { return null; }       // a file basename is not always a valid assembly name
    }

    /// <summary>
    /// Force-replaces the tracked assembly with a newly loaded one.
    /// Used during hot-reload when a shared dependency DLL has been updated.
    /// Old assembly stays in memory (Default ALC can't unload) but all future
    /// JIT resolutions will see the new instance.
    /// </summary>
    public static Assembly Replace(string assemblyName, byte[] bytes)
    {
        EnsureResolverRegistered();

        var loaded = Assembly.Load(bytes);
        var actualName = loaded.GetName().Name ?? assemblyName;
        _assemblies[actualName] = loaded;
        // Alias under the caller's key too (3.5) so a later LoadOrReuse(fileBasename) reuses this.
        if (!string.Equals(actualName, assemblyName, StringComparison.OrdinalIgnoreCase))
            _assemblies[assemblyName] = loaded;
        return loaded;
    }

    /// <summary>
    /// Tracks an assembly loaded externally (e.g. by <see cref="SharedAssemblyLoader"/>
    /// which loads into Default ALC via <c>LoadFromStream</c>).
    /// </summary>
    public static void Track(Assembly assembly)
    {
        EnsureResolverRegistered();

        var name = assembly.GetName().Name;
        if (name is not null)
            _assemblies.TryAdd(name, assembly);
    }

    /// <summary>
    /// Tracks an assembly, replacing if already present.
    /// Used by <see cref="SharedAssemblyLoader"/> on reload.
    /// </summary>
    public static void TrackOrReplace(Assembly assembly)
    {
        EnsureResolverRegistered();

        var name = assembly.GetName().Name;
        if (name is not null)
            _assemblies[name] = assembly;
    }

    /// <summary>
    /// Tries to find a tracked assembly by name.
    /// Used by <see cref="ModuleAssemblyLoadContext"/> to resolve shared deps
    /// before falling back to probe paths.
    /// </summary>
    public static bool TryGet(string assemblyName, out Assembly? assembly)
        => _assemblies.TryGetValue(assemblyName, out assembly);

    /// <summary>Number of tracked assemblies — for diagnostics.</summary>
    public static int Count => _assemblies.Count;

    /// <summary>
    /// Resets all state. For testing only.
    /// </summary>
    internal static void Reset()
    {
        _assemblies.Clear();
        _loadLocks.Clear();
        // Note: Default.Resolving handler stays (can't unsubscribe lambda) but will
        // return null for all lookups after Clear().
    }
}
