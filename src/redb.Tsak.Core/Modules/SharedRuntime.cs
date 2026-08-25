using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Early, DI-free entry point to the shared assembly layer (<c>Libs/shared</c>).
/// Installed as the very FIRST action in <c>Program.Main</c> — before any redb.* type is
/// touched — so that framework assemblies (redb.Core/Route/providers) can live in the
/// shared layer instead of the application bin and still be resolved at runtime.
/// See <c>docs/SHARED_RUNTIME_PLAN.md</c>.
/// <para>
/// Reuses exactly the same primitives as <see cref="SharedAssemblyLoader"/>:
/// <list type="bullet">
///   <item>byte-load via <c>LoadFromStream</c> — the DLL file is never locked, so it can be
///   swapped for a patch release without rebuilding the host;</item>
///   <item><see cref="LoadedAssemblyTracker"/> registration — modules (isolated ALCs)
///   unify their redb.* types against the single Default-ALC instance;</item>
///   <item>per-assembly native resolver over <c>runtimes/&lt;rid&gt;/native</c>;</item>
///   <item>version-tolerant forwarding for higher-version transitive references.</item>
/// </list>
/// This type adds only two things on top: a logger-free early call path, and a required-set
/// preload whose "what failed to load" result feeds the caller's fail-fast (stage B2).
/// </para>
/// This class references ONLY BCL + <see cref="LoadedAssemblyTracker"/> (also BCL-only), so
/// invoking it does not force any redb.* assembly to load.
/// </summary>
public static class SharedRuntime
{
    private static readonly HashSet<string> _resolverPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _gate = new();

    /// <summary>Default shared-layer location: <c>&lt;app-base&gt;/Libs/shared</c>.</summary>
    public static string DefaultSharedPath => Path.Combine(AppContext.BaseDirectory, "Libs", "shared");

    /// <summary>
    /// EARLY entry point for <c>Program.Main</c>. Installs the resolver over
    /// <paramref name="sharedPath"/> and eagerly byte-loads <paramref name="requiredNames"/>
    /// (the framework set) so they are present in the Default ALC before Tsak.Core touches any
    /// redb type. Returns the names that could NOT be loaded — the caller decides whether that
    /// is fatal (fail-fast is scoped to this required set; the lazy transitive tail stays soft).
    /// </summary>
    public static IReadOnlyList<string> InstallEarly(
        string sharedPath, IReadOnlyCollection<string> requiredNames, Action<string>? log = null)
    {
        var full = Path.GetFullPath(sharedPath);
        if (!Directory.Exists(full))
        {
            log?.Invoke($"[shared] path not found: {full}");
            return requiredNames.Where(n => !IsLoaded(n)).ToList();
        }

        EnsureSharedPathResolver(full, log);
        var nativeDirs = GetNativeLibraryDirs(full);

        var missing = new List<string>();
        foreach (var name in requiredNames)
        {
            if (IsLoaded(name)) continue;                       // already provided (e.g. from bin)
            var dll = Path.Combine(full, name + ".dll");
            if (!File.Exists(dll)) { missing.Add(name); continue; }
            var asm = LoadAndTrack(dll, nativeDirs, log);
            if (asm is null && !IsLoaded(name)) missing.Add(name);
        }
        return missing;
    }

    /// <summary>True if an assembly with this simple name is already loaded/tracked.</summary>
    public static bool IsLoaded(string simpleName)
        => LoadedAssemblyTracker.TryGet(simpleName, out _)
           || AssemblyLoadContext.Default.Assemblies.Any(a =>
               string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Version of a loaded assembly by simple name (tracker first, then Default ALC), or null.</summary>
    public static Version? GetLoadedVersion(string simpleName)
    {
        if (LoadedAssemblyTracker.TryGet(simpleName, out var tracked) && tracked is not null)
            return tracked.GetName().Version;
        return AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            ?.GetName().Version;
    }

    /// <summary>
    /// Registers the Default ALC resolver over <paramref name="fullSharedPath"/> once
    /// (idempotent per path). Probes the shared dir for not-yet-loaded assemblies and forwards
    /// higher-version references to an already-loaded lower version. Shared by the early path
    /// and by <see cref="SharedAssemblyLoader"/> so the handler is installed only once per path.
    /// </summary>
    public static void EnsureSharedPathResolver(string fullSharedPath, Action<string>? log = null)
    {
        LoadedAssemblyTracker.EnsureResolverRegistered();       // tracked-assembly handler first
        lock (_gate)
        {
            if (!_resolverPaths.Add(fullSharedPath)) return;    // already installed for this path
        }

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            // 1. Load a transitive dep from the shared dir on demand (byte-load, no lock).
            if (name.Name is not null)
            {
                var candidate = Path.Combine(fullSharedPath, name.Name + ".dll");
                if (File.Exists(candidate))
                {
                    try
                    {
                        var loaded = AssemblyLoadContext.Default.LoadFromStream(
                            new MemoryStream(File.ReadAllBytes(candidate)));
                        LoadedAssemblyTracker.Track(loaded);
                        return loaded;
                    }
                    catch (FileLoadException) { /* identity/version mismatch → forward below */ }
                    catch (Exception ex)
                    {
                        // A Resolving handler must NEVER throw — that aborts the runtime's whole
                        // resolution instead of letting it try version-tolerant forwarding or another
                        // handler. A transient IOException (file locked mid-copy by build-shared),
                        // UnauthorizedAccessException, or a partial/corrupt image (BadImageFormatException)
                        // are all soft-skipped here and we fall through to forwarding (review item 3.6).
                        log?.Invoke($"[shared] soft-skip loading '{name.Name}' from shared dir: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            // 2. Version-tolerant forwarding: a package may reference a higher framework version
            //    than the runtime provides — return the already-loaded lower version.
            return ctx.Assemblies.FirstOrDefault(a =>
                string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
        };
        log?.Invoke($"[shared] resolver installed over {fullSharedPath}");
    }

    /// <summary>
    /// Byte-loads a single DLL into the Default ALC and tracks it (file never locked). Registers
    /// a native resolver for it when native dirs exist. Returns the assembly, or <c>null</c> when
    /// the runtime already provides it (graceful — a shared copy of a framework assembly is not
    /// an error, the runtime one wins).
    /// </summary>
    public static Assembly? LoadAndTrack(string dllPath, List<string> nativeDirs, Action<string>? log = null)
    {
        try
        {
            var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(File.ReadAllBytes(dllPath)));
            LoadedAssemblyTracker.Track(asm);
            if (nativeDirs.Count > 0)
                RegisterNativeResolver(asm, nativeDirs);
            log?.Invoke($"[shared] loaded {asm.GetName().Name} {asm.GetName().Version}");
            return asm;
        }
        catch (FileLoadException)
        {
            return null; // runtime already supplies this assembly
        }
    }

    /// <summary>Native lib dirs under <c>shared/runtimes</c> for the current RID (+ OS-only fallback).</summary>
    public static List<string> GetNativeLibraryDirs(string fullSharedPath)
    {
        var runtimesPath = Path.Combine(fullSharedPath, "runtimes");
        if (!Directory.Exists(runtimesPath)) return [];

        var rid = RuntimeInformation.RuntimeIdentifier;
        var dirs = new List<string>();

        var exact = Path.Combine(runtimesPath, rid, "native");
        if (Directory.Exists(exact)) dirs.Add(exact);

        var osOnly = rid.Contains('-') ? rid[..rid.IndexOf('-')] : rid;
        var osDir = Path.Combine(runtimesPath, osOnly, "native");
        if (Directory.Exists(osDir) && !dirs.Contains(osDir)) dirs.Add(osDir);

        return dirs;
    }

    private static void RegisterNativeResolver(Assembly assembly, List<string> nativeDirs)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => TryLoadNative(name, nativeDirs));
        }
        catch (InvalidOperationException) { /* already has a resolver */ }
    }

    private static IntPtr TryLoadNative(string libraryName, List<string> nativeDirs)
    {
        foreach (var dir in nativeDirs)
        {
            var candidates = new[]
            {
                Path.Combine(dir, libraryName),
                Path.Combine(dir, libraryName + ".dll"),
                Path.Combine(dir, libraryName + ".so"),
                Path.Combine(dir, "lib" + libraryName + ".so"),
                Path.Combine(dir, libraryName + ".dylib"),
                Path.Combine(dir, "lib" + libraryName + ".dylib"),
            };
            foreach (var c in candidates)
                if (NativeLibrary.TryLoad(c, out var handle))
                    return handle;
        }
        return IntPtr.Zero;
    }
}
