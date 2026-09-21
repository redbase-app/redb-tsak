using redb.Tsak.Contracts;

namespace redb.Tsak.Web.Services;

/// <summary>What the storage page says about one index, beyond the numbers the catalogs gave.</summary>
public enum IndexAdviceKind
{
    /// <summary>The counters say nothing has read it since they started counting.</summary>
    Unused,

    /// <summary>Another index keys on exactly the same columns.</summary>
    Duplicate,

    /// <summary>Its key columns are the leading part of another index, which can serve the same lookups.</summary>
    CoveredByWiderIndex
}

/// <summary>One remark about one index, with the index it is about and, where there is one, the other index.</summary>
/// <param name="Kind">What kind of remark this is.</param>
/// <param name="Schema">Schema of the table; null on an engine without schemas.</param>
/// <param name="Table">Table the index belongs to.</param>
/// <param name="Index">The index the remark is about.</param>
/// <param name="OtherIndex">The index that duplicates or covers it, when the remark is about a pair.</param>
/// <param name="Detail">The remark in words, as the page prints it.</param>
public sealed record IndexAdviceItem(
    IndexAdviceKind Kind,
    string? Schema,
    string Table,
    string Index,
    string? OtherIndex,
    string Detail);

/// <summary>
/// Reads a set of index statistics and points out what an operator could act on. Three rules only, and all
/// three refuse to speak about an index the engine depends on.
/// <para>
/// The refusal is the point. An index that holds a primary key, a uniqueness constraint or a foreign key —
/// and every index of redb's own tables — reads zero because it enforces something, not because nobody needs
/// it. Dropping one on that evidence breaks the database, so <c>IsSystemCritical</c> from the engine
/// ends every rule before it starts.
/// </para>
/// </summary>
public static class IndexAdvice
{
    /// <summary>
    /// Collects the remarks. <paramref name="countersAreMeaningful"/> is false when the engine keeps no usage
    /// counters (SQLite) or when nothing says since when they have been counting — then "nothing read it" is
    /// not a fact about the index, only about our ignorance, and no unused remark is made.
    /// </summary>
    public static IReadOnlyList<IndexAdviceItem> Collect(
        IReadOnlyList<RedbIndexInfo>? indexes, bool countersAreMeaningful)
    {
        if (indexes is null || indexes.Count == 0)
            return [];

        var items = new List<IndexAdviceItem>();

        if (countersAreMeaningful)
            items.AddRange(indexes.Where(IsUnused).Select(UnusedItem));

        foreach (var group in indexes.GroupBy(i => (i.Schema ?? "", i.Table), TableKeyComparer))
        {
            var ofTable = group.ToArray();
            items.AddRange(Duplicates(ofTable));
            items.AddRange(Covered(ofTable));
        }

        return items;
    }

    private static readonly IEqualityComparer<(string Schema, string Table)> TableKeyComparer =
        EqualityComparer<(string Schema, string Table)>.Create(
            (a, b) => string.Equals(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase)
                      && string.Equals(a.Table, b.Table, StringComparison.OrdinalIgnoreCase),
            k => HashCode.Combine(k.Schema.ToLowerInvariant(), k.Table.ToLowerInvariant()));

    /// <summary>
    /// An index nothing structural depends on. Uniqueness counts as structural even when the catalog reports
    /// it outside a constraint: such an index is a rule about the data, and its read counter says nothing
    /// about whether the rule is still wanted.
    /// </summary>
    private static bool IsAdvisable(RedbIndexInfo i) =>
        !i.IsSystemCritical && !i.IsPrimaryKey && !i.IsUniqueConstraint && !i.BacksForeignKey && i.IsUnique != true;

    private static bool IsUnused(RedbIndexInfo i) =>
        IsAdvisable(i) && (i.Seeks ?? 0) == 0 && (i.Scans ?? 0) == 0;

    private static IndexAdviceItem UnusedItem(RedbIndexInfo i) => new(
        IndexAdviceKind.Unused, i.Schema, i.Table, i.Name, null,
        "No read has touched this index in the window the counters cover.");

    private static IEnumerable<IndexAdviceItem> Duplicates(IReadOnlyList<RedbIndexInfo> ofTable)
    {
        foreach (var group in ofTable.GroupBy(Signature, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < 2)
                continue;

            // The pair is reported on the advisable members only: when one of two identical indexes holds a
            // key, it is the other one that is spare.
            var names = group.Select(i => i.Name).ToArray();
            foreach (var index in group.Where(IsAdvisable))
            {
                var others = string.Join(", ", names.Where(n => n != index.Name));
                yield return new IndexAdviceItem(
                    IndexAdviceKind.Duplicate, index.Schema, index.Table, index.Name, others,
                    $"Keys on the same columns as {others}.");
            }
        }
    }

    private static IEnumerable<IndexAdviceItem> Covered(IReadOnlyList<RedbIndexInfo> ofTable)
    {
        foreach (var index in ofTable.Where(IsAdvisable))
        {
            if (index.Columns.Count == 0)
                continue;

            var wider = ofTable.FirstOrDefault(other =>
                other.Name != index.Name
                && other.Columns.Count > index.Columns.Count
                && IsPrefix(index.Columns, other.Columns));

            if (wider is not null)
            {
                yield return new IndexAdviceItem(
                    IndexAdviceKind.CoveredByWiderIndex, index.Schema, index.Table, index.Name, wider.Name,
                    $"Its columns are the leading part of {wider.Name}, which serves the same lookups.");
            }
        }
    }

    private static bool IsPrefix(IReadOnlyList<string> shorter, IReadOnlyList<string> longer)
    {
        for (var i = 0; i < shorter.Count; i++)
        {
            if (!string.Equals(shorter[i], longer[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Key columns in order, then the carried columns as a set: two indexes are the same index when they key
    /// on the same columns in the same order and carry the same payload, and differ otherwise.
    /// </summary>
    private static string Signature(RedbIndexInfo i) =>
        string.Join(",", i.Columns)
        + "|"
        + string.Join(",", i.IncludedColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
}
