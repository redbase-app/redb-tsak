using redb.Tsak.Contracts;

namespace redb.Tsak.Web.Services;

/// <summary>One database, and every instance declaration that points at it.</summary>
/// <param name="Key">Identity of the group, used as the value of the picker.</param>
/// <param name="Title">The database as the page names it.</param>
/// <param name="Label">The title plus how many contexts declare it, for the picker.</param>
/// <param name="Primary">The declaration the page queries through.</param>
/// <param name="Members">Every declaration pointing at this database.</param>
public sealed record StorageInstanceGroup(
    string Key,
    string Title,
    string Label,
    RedbInstanceInfo Primary,
    IReadOnlyList<RedbInstanceInfo> Members);

/// <summary>
/// Turns the instances a node reports into the databases they actually are.
/// <para>
/// Contexts declare instances, not databases. Identity alone declares the same three databases in five of its
/// contexts, which listed one per row is fifteen near-identical lines to choose from. They are grouped by the
/// fingerprint the node computes over the connection string — never by name, because two contexts may use one
/// name for different databases, and merging those would point the page's analyze button at the wrong one.
/// </para>
/// </summary>
public static class StorageInstanceGrouping
{
    /// <summary>Groups the instances; the host's own storage comes first, the rest by name.</summary>
    public static IReadOnlyList<StorageInstanceGroup> Group(IReadOnlyList<RedbInstanceInfo>? instances)
    {
        if (instances is null || instances.Count == 0)
            return [];

        return instances
            // Without a fingerprint — a declaration whose connection string the node could not read — the
            // instance stands alone rather than being merged with anything on a guess.
            .GroupBy(i => string.IsNullOrEmpty(i.Fingerprint) ? $"?{i.ContextName}/{i.Name}" : i.Fingerprint)
            .Select(g =>
            {
                var members = g.ToArray();
                // The host's own storage speaks for its group; otherwise an active declaration, since a
                // non-active one is the copy that was registered without ever being connected.
                var primary = members.FirstOrDefault(m => m.ContextName is null)
                              ?? members.FirstOrDefault(m => m.IsActive)
                              ?? members[0];
                var title = TitleOf(primary, members);
                var label = members.Length > 1 ? $"{title} — {members.Length} contexts" : title;
                return new StorageInstanceGroup(g.Key, title, label, primary, members);
            })
            .OrderBy(g => g.Primary.ContextName is null ? 0 : 1)
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// What to call the database. The host's own storage is "this node"; a declared one is its name, and the
    /// names of all its declarations when contexts named the same database differently.
    /// </summary>
    private static string TitleOf(RedbInstanceInfo primary, IReadOnlyList<RedbInstanceInfo> members)
    {
        if (primary.ContextName is null)
            return "this node";

        var names = members
            .Select(m => m.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return names.Length == 1 ? names[0] : string.Join(" / ", names);
    }
}
