namespace redb.Tsak.Web.Services;

/// <summary>
/// The node a dashboard address is about. Node pages live under <c>node/{nodeId}/...</c>; the node id is the segment
/// after <c>node/</c>, and everything after it (sub-page, entity, query) belongs to that node's page.
/// </summary>
public static class NodePath
{
    /// <summary>The node id of a base-relative path (<c>node/w2/monitoring?range=60</c> gives <c>w2</c>), or null.</summary>
    public static string? NodeIdOf(string baseRelativePath)
    {
        ArgumentNullException.ThrowIfNull(baseRelativePath);
        var path = StripQueryAndFragment(baseRelativePath);
        if (!path.StartsWith("node/", StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = path[5..];
        var slash = rest.IndexOf('/');
        var id = slash >= 0 ? rest[..slash] : rest;
        return id.Length == 0 ? null : Uri.UnescapeDataString(id);
    }

    /// <summary>
    /// The same address on another node: the node segment is replaced, the sub-page, the entity segments, the query
    /// and the fragment are kept. A path that is not a node page goes to the target node's overview.
    /// </summary>
    public static string WithNode(string baseRelativePath, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(baseRelativePath);
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        var escaped = Uri.EscapeDataString(nodeId);
        if (NodeIdOf(baseRelativePath) is null)
            return $"node/{escaped}";

        var rest = baseRelativePath[5..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var tail = end >= 0 ? rest[end..] : string.Empty;
        return $"node/{escaped}{tail}";
    }

    private static string StripQueryAndFragment(string path)
    {
        var cut = path.IndexOfAny(['?', '#']);
        return cut >= 0 ? path[..cut] : path;
    }
}
