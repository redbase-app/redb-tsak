namespace redb.Tsak.Core.Modules;

/// <summary>
/// The order packages are opened in: a package after every package its <see cref="ModuleManifest.Dependencies"/> names
/// (a carrier of shared companion DLLs before the packages that use them), otherwise in the order given. The startup
/// discovery and the hot-reload scan open packages in this one order, so a dependent never loads before its carrier.
/// </summary>
internal static class PackageDependencyOrder
{
    /// <summary>
    /// Sorts <paramref name="packages"/> by their dependencies (Kahn's algorithm, stable for the given order). A
    /// dependency on a package that is not in the list is ignored. Packages in a cycle come last, in the given order, and
    /// are returned in <paramref name="cyclic"/> for the caller to report.
    /// </summary>
    public static List<T> Sort<T>(IReadOnlyList<T> packages, Func<T, string> path, Func<T, ModuleManifest> manifest,
        out List<T> cyclic)
    {
        cyclic = [];
        if (packages.Count <= 1)
            return [.. packages];

        var byName = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (!string.IsNullOrWhiteSpace(manifest(package).Name))
                byName[manifest(package).Name] = package;
        }

        var dependencies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            var deps = new List<string>();
            foreach (var dep in manifest(package).Dependencies)
            {
                if (!byName.TryGetValue(dep, out var dependency)
                    || string.Equals(path(dependency), path(package), StringComparison.OrdinalIgnoreCase)
                    || deps.Contains(path(dependency), StringComparer.OrdinalIgnoreCase))
                    continue;
                deps.Add(path(dependency));
            }
            dependencies[path(package)] = deps;
            inDegree[path(package)] = deps.Count;
        }

        var queue = new Queue<T>();
        foreach (var package in packages)
        {
            if (inDegree[path(package)] == 0)
                queue.Enqueue(package);
        }

        var ordered = new List<T>(packages.Count);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            ordered.Add(current);
            emitted.Add(path(current));

            foreach (var package in packages)
            {
                if (emitted.Contains(path(package)))
                    continue;
                if (!dependencies[path(package)].Any(p => string.Equals(p, path(current), StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (--inDegree[path(package)] == 0)
                    queue.Enqueue(package);
            }
        }

        if (ordered.Count < packages.Count)
        {
            cyclic = packages.Where(p => !emitted.Contains(path(p))).ToList();
            ordered.AddRange(cyclic);
        }
        return ordered;
    }
}
