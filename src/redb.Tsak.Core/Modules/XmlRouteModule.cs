using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Components.Bean;
using redb.Route.Core;
using redb.Route.Xml;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// The third module convention (Route-XML Ф5.2): XML route artifacts from the package manifest,
/// loaded into the context through the standard <see cref="XmlRouteLoader"/> — which registers
/// routes via <c>AddRoutes</c> by construction, so the issue #3 trap (routes silently never
/// compiling) cannot recur. Bean type names resolve package-first (entry-point ALC, then
/// companions, then everything the default resolver sees — shared and Default ALC); artifact
/// file-references resolve from the package's extracted resources. Discovery order guarantees
/// assemblies are scanned before XML, so <c>bean:#name</c> objects registered by module code
/// are visible to the artifacts.
/// </summary>
public sealed class XmlRouteModule : ITsakModule, IEmbeddedConfigModule
{
    private readonly ModulePackage _package;
    private readonly ILogger? _logger;

    public XmlRouteModule(ModulePackage package, string? sourceDirectory,
        string? embeddedConfigJson, ILogger? logger = null)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        SourceDirectory = sourceDirectory;
        EmbeddedConfigJson = embeddedConfigJson;
        _logger = logger;
    }

    public string ModuleName => _package.Manifest.Name;
    public string Version => _package.Manifest.Version;
    public string Description =>
        $"XML route module: {_package.Manifest.Artifacts.Count} artifact(s) from {Path.GetFileName(_package.PackagePath)}";
    public IReadOnlyList<string> Dependencies => _package.Manifest.Dependencies;
    public bool CanInitialize => true;
    public TsakModuleStatus Status { get; internal set; } = TsakModuleStatus.Loaded;
    public string? SourceDirectory { get; }

    /// <inheritdoc />
    public string? EmbeddedConfigJson { get; }

    public IRouteContext Initialize(IRouteContext context)
    {
        if (context is not RouteContext routeContext)
        {
            Status = TsakModuleStatus.Faulted;
            throw new InvalidOperationException(
                $"XmlRouteModule '{ModuleName}' needs a RouteContext to load its routes, but got " +
                $"{context?.GetType().FullName ?? "null"}.");
        }

        // Fail fast on missing configuration (Ф5.4 / эскиз 11 §1.4): every key the manifest
        // requires must have a value in the MERGED context configuration — which the
        // coordinator has already written into the context's properties before Initialize.
        // Owner decision 2026-09-03: module-isolated failure — this module faults, its routes
        // never register, the context lives on.
        var missing = _package.Manifest.RequiredConfigKeys
            .Where(key => routeContext.GetProperty<object>(key) is null)
            .ToList();
        if (missing.Count > 0)
        {
            Status = TsakModuleStatus.Faulted;
            throw new InvalidOperationException(
                $"XmlRouteModule '{ModuleName}': the merged context configuration provides no value for " +
                $"required key(s): {string.Join(", ", missing)}. Supply them via host appsettings, the " +
                "external context.json, or an Override environment variable.");
        }

        if (_package.XmlArtifacts.Count != _package.Manifest.Artifacts.Count)
        {
            Status = TsakModuleStatus.Faulted;
            var found = _package.XmlArtifacts.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            throw new InvalidOperationException(
                $"XmlRouteModule '{ModuleName}': artifact(s) listed in the manifest are missing from the package: " +
                string.Join(", ", _package.Manifest.Artifacts.Where(a => !found.Contains(a))));
        }

        // Package-first resolution, chained over whatever the context already has — several
        // packages share one context, and each keeps its own package-first view.
        routeContext.AddService(typeof(IBeanTypeResolver),
            new PackageFirstTypeResolver(_package, routeContext.GetService<IBeanTypeResolver>()));
        if (_package.ResourcesRoot is { } resourcesRoot)
        {
            routeContext.AddService(typeof(IRouteResourceResolver),
                new PackageFirstResourceResolver(resourcesRoot, routeContext.GetService<IRouteResourceResolver>()));
        }

        // Every markup contribution reachable in this worker (Р21): the package's own
        // assemblies first, then everything the process carries (the shared layer above all —
        // <cache>, <rest>, <redb>, … ship their elements as contributions). Without this an XML
        // module could use only the core elements.
        var options = new XmlRouteLoaderOptions { Extensions = DiscoverContributions() };
        var packageSource = Path.GetFileName(_package.PackagePath);

        // The package's context.xml first (components, beans, onInit, context-level blocks
        // like <redb>) — route artifacts may rely on all of it.
        if (_package.ContextXml is { } contextXml)
        {
            var contextDocument = System.Xml.Linq.XDocument.Parse(contextXml, System.Xml.Linq.LoadOptions.SetLineInfo);
            new XmlContextLoader(routeContext, options).Load(contextDocument, $"{packageSource}::context.xml");
        }

        var loader = new XmlRouteLoader(routeContext, options);
        foreach (var (name, content) in _package.XmlArtifacts)
        {
            var document = System.Xml.Linq.XDocument.Parse(content, System.Xml.Linq.LoadOptions.SetLineInfo);
            loader.Load(document, $"{packageSource}::{name}");
        }

        _logger?.LogInformation("XmlRouteModule {Module}: loaded {Count} artifact(s)",
            ModuleName, _package.XmlArtifacts.Count);
        Status = TsakModuleStatus.Initialized;
        return context;
    }

    /// <summary>
    /// Public parameterless <see cref="IXmlElementContribution"/> implementations from the
    /// package's assemblies and the whole process (shared layer, host) — mirroring how the
    /// worker feeds components to a context (everything available is available). Types that
    /// fail to load are skipped the way the engine's AddComponents skips them.
    /// </summary>
    private List<IXmlElementContribution> DiscoverContributions()
    {
        var contributions = new List<IXmlElementContribution>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var assemblies = _package.LoadedAssemblies
            .Concat(_package.CompanionAssemblies)
            .Concat(AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("redb.", StringComparison.Ordinal) == true));

        foreach (var assembly in assemblies.Distinct())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.Where(t => t is not null)!];
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface
                    || !typeof(IXmlElementContribution).IsAssignableFrom(type)
                    || type.GetConstructor(Type.EmptyTypes) is null
                    || !seen.Add(type.FullName ?? type.Name))
                    continue;
                contributions.Add((IXmlElementContribution)Activator.CreateInstance(type)!);
            }
        }

        return contributions;
    }

    /// <summary>Entry-point ALC first, companions second, then everything the default resolver sees.</summary>
    private sealed class PackageFirstTypeResolver(ModulePackage package, IBeanTypeResolver? next) : IBeanTypeResolver
    {
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

    /// <summary>The package's extracted resources first, then whatever the context had.</summary>
    private sealed class PackageFirstResourceResolver(string resourcesRoot, IRouteResourceResolver? next) : IRouteResourceResolver
    {
        public string? Resolve(string reference)
        {
            if (Path.IsPathRooted(reference))
                return File.Exists(reference) ? reference : next?.Resolve(reference);
            var candidate = Path.GetFullPath(Path.Combine(resourcesRoot, reference));
            if (candidate.StartsWith(resourcesRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                return candidate;
            return next?.Resolve(reference);
        }

        public string DescribeSearch(string reference)
            => $"{Path.Combine(resourcesRoot, reference)}; {next?.DescribeSearch(reference) ?? reference}";
    }
}
