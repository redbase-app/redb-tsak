using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

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
    /// Returns an already-tracked assembly with the given name, or loads <paramref name="bytes"/>
    /// into the Default ALC and tracks it. Guarantees that the same assembly name always
    /// maps to a single <see cref="Assembly"/> instance.
    /// </summary>
    public static Assembly LoadOrReuse(string assemblyName, byte[] bytes)
    {
        EnsureResolverRegistered();

        // Fast path: reuse existing
        if (_assemblies.TryGetValue(assemblyName, out var existing))
            return existing;

        // Use Lazy<T> to guarantee only one Assembly.Load call per name,
        // even under concurrent access. Losers get the winner's Assembly.
        var lazy = _loadLocks.GetOrAdd(assemblyName, _ => new Lazy<Assembly>(() =>
        {
            var loaded = Assembly.Load(bytes);
            var actualName = loaded.GetName().Name ?? assemblyName;
            _assemblies.TryAdd(actualName, loaded);
            return loaded;
        }));

        var result = lazy.Value;
        _loadLocks.TryRemove(assemblyName, out _);
        return _assemblies.TryGetValue(assemblyName, out var tracked) ? tracked : result;
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
