using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using redb.Core;
using redb.Core.Attributes;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Puts the redb schemes a module reads into the process's scheme cache before the module starts, reading them from
/// the database and changing nothing there.
/// <para>
/// <c>IRedbService.InitializeAsync</c> runs before modules load and looks for <c>[RedbScheme]</c> types in the default
/// load context only, where a module's own assembly never is. A module that syncs its schemes fills the cache itself;
/// one that only reads schemes another process owns (one worker syncs, the others read) would meet every scheme on
/// its first query: a lookup in the database on a blocked thread. Here they are read once, up front.
/// </para>
/// <para>
/// A scheme the database does not hold yet is skipped: the module that owns it creates it. A failure is logged and
/// the module starts anyway: the scheme is then read on its first query, as before.
/// </para>
/// </summary>
internal static class ModuleSchemeWarmup
{
    /// <summary>Reads into the cache the schemes of every <c>[RedbScheme]</c> type of <paramref name="modules"/>.</summary>
    public static async Task WarmAsync(IRedbService redb, IEnumerable<ITsakModule> modules, ILogger logger,
        CancellationToken ct = default)
    {
        foreach (var module in modules)
        {
            var types = SchemeTypes(module).ToList();
            if (types.Count == 0)
                continue;

            var cached = 0;
            try
            {
                foreach (var type in types)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await redb.GetSchemeByTypeAsync(type, ct).ConfigureAwait(false) is not null)
                        cached++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Module {Module}: could not read its redb schemes ahead of start; each is read on its first query instead",
                    module.ModuleName);
                continue;
            }

            logger.LogDebug("Module {Module}: {Cached} of {Total} redb schemes read into the cache ahead of start",
                module.ModuleName, cached, types.Count);
        }
    }

    /// <summary>
    /// The <c>[RedbScheme]</c> types of the module's assembly and of the assemblies it references directly (a domain
    /// library beside the module), loaded through the module's own load context.
    /// </summary>
    internal static IEnumerable<Type> SchemeTypes(ITsakModule module)
    {
        var entry = module switch
        {
            StaticMethodModule m => m.ModuleAssembly,
            RouteBuilderModule m => m.ModuleAssembly,
            _ => null,
        };
        if (entry is null)
            return [];

        var assemblies = new List<Assembly> { entry };
        var loadContext = AssemblyLoadContext.GetLoadContext(entry) ?? AssemblyLoadContext.Default;
        foreach (var reference in entry.GetReferencedAssemblies())
        {
            if (IsFramework(reference.Name))
                continue;
            try
            {
                assemblies.Add(loadContext.LoadFromAssemblyName(reference));
            }
            catch (Exception)
            {
                // A reference the module never loads (an optional dependency) holds no scheme it reads.
            }
        }

        return assemblies.Distinct().SelectMany(TypesWithScheme).Distinct();
    }

    private static bool IsFramework(string? name)
        => name is null
           || name.StartsWith("System", StringComparison.Ordinal)
           || name.StartsWith("Microsoft.", StringComparison.Ordinal)
           || name is "netstandard" or "mscorlib";

    private static IEnumerable<Type> TypesWithScheme(Assembly assembly)
    {
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }
        catch (Exception)
        {
            return [];
        }
        return types.Where(t => t is not null && t.GetCustomAttribute<RedbSchemeAttribute>() is not null)!;
    }
}
