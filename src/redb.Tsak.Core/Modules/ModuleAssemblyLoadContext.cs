using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Isolated <see cref="AssemblyLoadContext"/> for module isolation.
/// Each dynamically loaded module gets its own ALC.
/// When collectible, the ALC can be unloaded and replaced without restarting the process.
/// When non-collectible (default), a replaced ALC stays in memory until the process restarts.
/// Loads from bytes so the original DLL file is never locked.
/// Probes configured directories for dependencies so modules don't need
/// to be in the host application's bin folder.
/// </summary>
public sealed class ModuleAssemblyLoadContext : AssemblyLoadContext
{
    // Every module context made in the process, weakly. AssemblyLoadContext.All drops a context the moment its Unload
    // starts, while the context stays in memory until it is collected: the count of what is in memory needs its own list.
    private static readonly List<WeakReference<ModuleAssemblyLoadContext>> Created = [];

    private readonly string[] _probePaths;

    public ModuleAssemblyLoadContext(string name, string[]? probePaths = null, bool isCollectible = false)
        : base(name: name, isCollectible: isCollectible)
    {
        _probePaths = probePaths ?? [];
        lock (Created)
            Created.Add(new WeakReference<ModuleAssemblyLoadContext>(this));
    }

    /// <summary>
    /// The module contexts in memory, unloading ones included, until they are collected. The list holds them only for
    /// as long as the caller does.
    /// </summary>
    internal static List<ModuleAssemblyLoadContext> InMemory()
    {
        var alive = new List<ModuleAssemblyLoadContext>();
        lock (Created)
        {
            Created.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (var reference in Created)
            {
                if (reference.TryGetTarget(out var context))
                    alive.Add(context);
            }
        }
        return alive;
    }

    /// <summary>
    /// The companion assemblies of the package this context belongs to. They load into the Default context, shared
    /// by name across packages, yet they are the module's code: its type resolver searches them along with
    /// <see cref="AssemblyLoadContext.Assemblies"/>.
    /// </summary>
    internal IReadOnlyList<Assembly> Companions { get; set; } = [];

    /// <summary>
    /// The manifest of the package this context belongs to, null for a module loaded from a bare DLL: its
    /// <see cref="ModuleManifest.Dependencies"/> order the contexts of the package's modules at start.
    /// </summary>
    internal ModuleManifest? Manifest { get; set; }

    /// <summary>
    /// When Tsak let this context go (<see cref="TryUnload"/>); null while a module, or the rollback history, still uses
    /// it. From then on the context should leave memory: <see cref="ModuleContextMonitor"/> counts it while it does not.
    /// </summary>
    internal DateTime? ReleasedAtUtc { get; private set; }

    /// <summary>Set by <see cref="ModuleContextMonitor"/> once it has taken this released context into its unload check.</summary>
    internal bool UnloadChecked { get; set; }

    /// <summary>Set by <see cref="ModuleContextMonitor"/> when this collectible context outlived the collections of its check.</summary>
    internal bool OutlivedUnload { get; set; }

    /// <summary>
    /// Lets the context go: unloads it if collectible. A context that is not collectible stays in memory until the process
    /// restarts. Either way it is marked released, so <see cref="ModuleContextMonitor"/> sees it.
    /// </summary>
    public void TryUnload()
    {
        ReleasedAtUtc ??= DateTime.UtcNow;
        if (IsCollectible)
            Unload();
    }

    /// <summary>Reads <paramref name="dllPath"/> into memory and loads from stream — file is never locked.</summary>
    public Assembly LoadFromBytes(string dllPath)
    {
        var bytes = File.ReadAllBytes(dllPath);
        return LoadFromStream(new MemoryStream(bytes));
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // First, try to resolve from the default (host) ALC.
        // This ensures shared contract types (IRouteContext, ITsakModule, etc.)
        // use the same identity as the host — preventing type mismatch across ALCs.
        try
        {
            var hostAssembly = Default.LoadFromAssemblyName(assemblyName);
            if (hostAssembly is not null)
                return hostAssembly;
        }
        catch (FileNotFoundException) { }
        catch (FileLoadException) { }

        // Second, check centralized tracker — covers byte-loaded assemblies from
        // packages, bare DLLs, and shared layer. This ensures that dependencies
        // shared across modules use the same Assembly instance (same static fields).
        if (assemblyName.Name is not null
            && LoadedAssemblyTracker.TryGet(assemblyName.Name, out var tracked)
            && tracked is not null)
            return tracked;

        // Not in host or tracker — probe module directories. This is a PRIVATE module dependency
        // (a shared/host/contract assembly would have resolved above via Default or the tracker), so
        // load it into THIS module's ALC, not the Default ALC (review item 3.7a). That keeps it
        // unloadable together with the module, and lets two modules carry DIFFERENT versions of the
        // same private dependency instead of silently sharing whichever loaded first into Default.
        foreach (var dir in _probePaths)
        {
            var candidate = Path.Combine(dir, assemblyName.Name + ".dll");
            if (File.Exists(candidate))
            {
                var bytes = File.ReadAllBytes(candidate);
                return LoadFromStream(new MemoryStream(bytes));
            }
        }

        return null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var dir in _probePaths)
        {
            // Direct match in probe directory
            var candidate = Path.Combine(dir, unmanagedDllName);
            if (File.Exists(candidate))
                return LoadUnmanagedDllFromPath(candidate);

            // NuGet runtimes layout: runtimes/{rid}/native/{lib}
            var rid = RuntimeInformation.RuntimeIdentifier;
            candidate = Path.Combine(dir, "runtimes", rid, "native", unmanagedDllName);
            if (File.Exists(candidate))
                return LoadUnmanagedDllFromPath(candidate);
        }

        return nint.Zero;
    }
}
