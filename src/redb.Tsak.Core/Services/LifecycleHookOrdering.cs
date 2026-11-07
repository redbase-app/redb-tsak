using redb.Tsak.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace redb.Tsak.Core.Services;

/// <summary>
/// Deterministic ordering for <see cref="ITsakLifecycleHook"/> instances. Sorts by
/// <c>(Order asc, Type.FullName asc)</c> so that two hooks sharing the same <c>Order</c>
/// still execute in a stable, reproducible sequence across processes and runs.
/// Emits a single WRN per Order-collision so operators can fix ambiguous configurations.
/// </summary>
internal static class LifecycleHookOrdering
{
    /// <summary>Stable ascending order with WRN on Order collisions.</summary>
    public static IEnumerable<ITsakLifecycleHook> OrderAscending(
        IEnumerable<ITsakLifecycleHook> hooks,
        ILogger logger)
    {
        var materialized = Materialize(hooks);
        WarnOnTies(materialized, logger);
        return materialized
            .OrderBy(h => h.Order)
            .ThenBy(h => h.GetType().FullName, StringComparer.Ordinal);
    }

    /// <summary>Stable descending order (mirror of <see cref="OrderAscending"/>) for shutdown.</summary>
    public static IEnumerable<ITsakLifecycleHook> OrderDescending(
        IEnumerable<ITsakLifecycleHook> hooks,
        ILogger logger)
    {
        var materialized = Materialize(hooks);
        WarnOnTies(materialized, logger);
        return materialized
            .OrderByDescending(h => h.Order)
            .ThenByDescending(h => h.GetType().FullName, StringComparer.Ordinal);
    }

    private static IReadOnlyCollection<ITsakLifecycleHook> Materialize(IEnumerable<ITsakLifecycleHook> hooks)
        => hooks as IReadOnlyCollection<ITsakLifecycleHook> ?? hooks.ToList();

    private static void WarnOnTies(IReadOnlyCollection<ITsakLifecycleHook> hooks, ILogger logger)
    {
        foreach (var grp in hooks.GroupBy(h => h.Order).Where(g => g.Count() > 1))
        {
            var names = string.Join(", ",
                grp.Select(h => h.GetType().FullName).OrderBy(n => n, StringComparer.Ordinal));
            logger.LogWarning(
                "Lifecycle hooks share Order={Order} — tie broken by type FullName (deterministic but implicit). " +
                "Set distinct Order values to make execution order explicit. Hooks: {Hooks}",
                grp.Key, names);
        }
    }
}
