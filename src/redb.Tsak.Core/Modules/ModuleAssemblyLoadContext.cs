using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Isolated <see cref="AssemblyLoadContext"/> for module isolation.
/// Each dynamically loaded module gets its own ALC.
/// When collectible, the ALC can be unloaded and replaced without restarting the process.
/// When non-collectible (default), the ALC stays in memory but is compatible with
/// Reflection.Emit consumers (XmlSerializer, compiled Regex, source generators, etc.).
/// Loads from bytes so the original DLL file is never locked.
/// Probes configured directories for dependencies so modules don't need
/// to be in the host application's bin folder.
/// </summary>
public sealed class ModuleAssemblyLoadContext : AssemblyLoadContext
{
    private readonly string[] _probePaths;

    public ModuleAssemblyLoadContext(string name, string[]? probePaths = null, bool isCollectible = false)
        : base(name: name, isCollectible: isCollectible)
    {
        _probePaths = probePaths ?? [];
    }

    /// <summary>
    /// Unloads if collectible; no-op otherwise. Safe to call unconditionally.
    /// </summary>
    public void TryUnload()
    {
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

        // Not in host or tracker — probe module directories.
        // Load into Default ALC and register in tracker so other ALCs reuse it.
        foreach (var dir in _probePaths)
        {
            var candidate = Path.Combine(dir, assemblyName.Name + ".dll");
            if (File.Exists(candidate))
            {
                var bytes = File.ReadAllBytes(candidate);
                return LoadedAssemblyTracker.LoadOrReuse(assemblyName.Name!, bytes);
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
