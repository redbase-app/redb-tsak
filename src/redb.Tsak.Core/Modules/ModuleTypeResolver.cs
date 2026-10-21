using System.Reflection;
using System.Runtime.Loader;
using redb.Route.Abstractions;
using redb.Route.Components.Bean;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// The <see cref="IBeanTypeResolver"/> of a module's route context: a type name (<c>bean:</c>, <c>&lt;bean class&gt;</c>,
/// the SQL <c>outputClass</c>) resolves to the module's own class first, then, as before, through the process
/// (<see cref="DefaultBeanTypeResolver"/>). Apache Camel's ClassResolver under OSGi works the same way: the bundle's
/// class loader before the platform's.
/// <para>
/// Searching the process first picked whichever assembly of the name loaded first: after a module was reloaded
/// under the same name, its previous version (still loaded when the load context is not collectible); with a host
/// or another module declaring a class of the same name, theirs.
/// </para>
/// </summary>
internal sealed class ModuleTypeResolver : IBeanTypeResolver
{
    private readonly Assembly[] _moduleAssemblies;

    internal ModuleTypeResolver(IEnumerable<Assembly> moduleAssemblies)
        => _moduleAssemblies = moduleAssemblies.Distinct().ToArray();

    /// <summary>The module code searched first, in order.</summary>
    internal IReadOnlyList<Assembly> ModuleAssemblies => _moduleAssemblies;

    /// <summary>
    /// The resolver of a context that runs <paramref name="modules"/>: the assemblies of their packages, or of their
    /// load contexts, or, for a module of the Default context, its own assembly.
    /// </summary>
    internal static ModuleTypeResolver For(IEnumerable<ITsakModule> modules)
        => new(modules.SelectMany(AssembliesOf));

    private static IEnumerable<Assembly> AssembliesOf(ITsakModule module)
    {
        if (module is XmlRouteModule xml)
            return xml.PackageAssemblies;

        var assembly = module.GetType().Assembly;
        return AssemblyLoadContext.GetLoadContext(assembly) switch
        {
            ModuleAssemblyLoadContext own => own.Assemblies.Concat(own.Companions),
            { } other when other != AssemblyLoadContext.Default => other.Assemblies,
            _ => [assembly],
        };
    }

    /// <inheritdoc />
    public Type? Resolve(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);

        var comma = typeName.IndexOf(',');
        var fullName = (comma >= 0 ? typeName[..comma] : typeName).Trim();
        var assemblyName = comma >= 0 ? typeName[(comma + 1)..].Split(',')[0].Trim() : null;

        foreach (var assembly in _moduleAssemblies)
        {
            if (assemblyName is not null
                && !string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (assembly.GetType(fullName, throwOnError: false) is { } type)
                return type;
        }

        return DefaultBeanTypeResolver.Instance.Resolve(typeName);
    }
}
