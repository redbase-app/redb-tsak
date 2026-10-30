using redb.Route.Abstractions;
using redb.Route.Components.Bean;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Bean type names of a package's module: the entry-point assemblies first, the companions second, then the resolver
/// the context had before, then everything the default resolver sees. A reloaded package thus gets its own new types,
/// not the first loaded assembly of the same name.
/// </summary>
internal sealed class PackageFirstTypeResolver(ModulePackage package, IBeanTypeResolver? next) : IBeanTypeResolver
{
    /// <summary>Chains a package-first resolver over the one the context has, so each package keeps its own view.</summary>
    internal static void Install(IRouteContext context, ModulePackage package)
        => context.AddService(typeof(IBeanTypeResolver),
            new PackageFirstTypeResolver(package, context.GetService<IBeanTypeResolver>()));

    public Type? Resolve(string typeName)
    {
        var comma = typeName.IndexOf(',');
        var fullName = (comma >= 0 ? typeName[..comma] : typeName).Trim();
        var assemblyName = comma >= 0 ? typeName[(comma + 1)..].Trim() : null;

        foreach (var assembly in package.LoadedAssemblies.Concat(package.CompanionAssemblies))
        {
            if (assemblyName is not null && !string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (assembly.GetType(fullName) is { } type)
                return type;
        }
        return next?.Resolve(typeName) ?? DefaultBeanTypeResolver.Instance.Resolve(typeName);
    }
}
