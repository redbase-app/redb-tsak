namespace redb.Tsak.Contracts;

/// <summary>
/// One redb instance the node can reach: the host's own storage, or an instance a context declares in its
/// <c>Redb</c> section. What the engine behind it can report differs, and the dashboard hides what it cannot
/// answer instead of printing empty columns — hence the capability flags rather than "the value came back null".
/// </summary>
public sealed record RedbInstanceInfo
{
    /// <summary>Instance name; empty for the host's own storage.</summary>
    public string Name { get; init; } = "";

    /// <summary>Context that declares the instance; null for the host's own storage.</summary>
    public string? ContextName { get; init; }

    /// <summary>Provider as configured: postgres, mssql or sqlite.</summary>
    public string Provider { get; init; } = "";

    /// <summary>Whether the instance runs the Pro tier.</summary>
    public bool UsePro { get; init; }

    /// <summary>
    /// Whether the context named this instance as its active one. A non-active instance is registered but
    /// never connected on start, so asking it for statistics is what opens its connection.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Short fingerprint of provider and connection string: instances that share it address the same database.
    /// Identity alone declares the same three databases in five contexts, which listed one row each is fifteen
    /// rows of the same three things — the dashboard groups by this instead of guessing from the name, which
    /// two contexts may well reuse for different databases. The connection string itself never leaves the node.
    /// </summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>Whether the engine has schemas at all (SQLite does not).</summary>
    public bool SupportsSchemas { get; init; }

    /// <summary>Whether the engine counts index usage (SQLite does not).</summary>
    public bool SupportsUsageCounters { get; init; }

    /// <summary>Whether the engine records when statistics were last refreshed (SQLite does not).</summary>
    public bool SupportsAnalyzeTimestamps { get; init; }
}

/// <summary>Everything one instance reports in a single read: the counter window, its tables and its indexes.</summary>
public sealed record RedbStorageStats
{
    /// <summary>Instance the numbers came from; empty for the host's own storage.</summary>
    public string Instance { get; init; } = "";

    /// <summary>Context of that instance; null for the host's own storage.</summary>
    public string? ContextName { get; init; }

    /// <summary>
    /// Since when the usage counters have been counting: a statistics reset, or the server start. Null when
    /// the engine does not say — and then a zero counter means nothing at all.
    /// </summary>
    public DateTimeOffset? CountersSince { get; init; }

    /// <summary>Whether this node is a replica, so the counters describe reads served here and nowhere else.</summary>
    public bool? IsReplica { get; init; }

    /// <summary>Tables of the database, redb's own and everyone else's.</summary>
    public IReadOnlyList<RedbTableInfo> Tables { get; init; } = [];

    /// <summary>Indexes of those tables.</summary>
    public IReadOnlyList<RedbIndexInfo> Indexes { get; init; } = [];
}

/// <summary>One table as the database catalogs report it. Null means the engine does not say.</summary>
public sealed record RedbTableInfo
{
    /// <summary>Schema of the table; null on an engine without schemas.</summary>
    public string? Schema { get; init; }

    /// <summary>Table name.</summary>
    public string Table { get; init; } = "";

    /// <summary>Whether the table belongs to redb itself rather than to the application.</summary>
    public bool IsRedbOwned { get; init; }

    /// <summary>Row estimate.</summary>
    public long? EstimatedRows { get; init; }

    /// <summary>Size of the data, without the indexes.</summary>
    public long? DataSizeBytes { get; init; }

    /// <summary>Size of every index of this table together.</summary>
    public long? IndexesSizeBytes { get; init; }

    /// <summary>Dead rows awaiting vacuum (PostgreSQL).</summary>
    public long? DeadRows { get; init; }

    /// <summary>When statistics were last refreshed by hand.</summary>
    public DateTimeOffset? LastAnalyze { get; init; }

    /// <summary>When the engine last refreshed them on its own.</summary>
    public DateTimeOffset? LastAutoAnalyze { get; init; }

    /// <summary>When the table was last vacuumed (PostgreSQL).</summary>
    public DateTimeOffset? LastVacuum { get; init; }

    /// <summary>Whether statistics exist at all; the only answer SQLite can give about them.</summary>
    public bool? HasStatistics { get; init; }
}

/// <summary>One index as the database catalogs report it. Null means the engine does not say.</summary>
public sealed record RedbIndexInfo
{
    /// <summary>Schema of the table; null on an engine without schemas.</summary>
    public string? Schema { get; init; }

    /// <summary>Table the index belongs to.</summary>
    public string Table { get; init; } = "";

    /// <summary>Index name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Whether the index is unique.</summary>
    public bool? IsUnique { get; init; }

    /// <summary>Key columns, in index order.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Columns carried but not keyed on (SQL Server), which is what makes an index covering.</summary>
    public IReadOnlyList<string> IncludedColumns { get; init; } = [];

    /// <summary>Whether the index backs the primary key.</summary>
    public bool IsPrimaryKey { get; init; }

    /// <summary>Whether the index enforces a uniqueness constraint.</summary>
    public bool IsUniqueConstraint { get; init; }

    /// <summary>Whether the index is clustered (SQL Server).</summary>
    public bool? IsClustered { get; init; }

    /// <summary>Whether a foreign key relies on the index.</summary>
    public bool BacksForeignKey { get; init; }

    /// <summary>Whether the index belongs to a table of redb itself.</summary>
    public bool IsRedbOwned { get; init; }

    /// <summary>
    /// Whether removing the index would break the engine or a constraint. Such an index is shown and never
    /// offered for removal: some of them read zero because they hold a key, not because nobody uses them.
    /// </summary>
    public bool IsSystemCritical { get; init; }

    /// <summary>Why the index is critical: RedbMetadata, RedbSecurity, RedbData, PrimaryKey, Unique, ForeignKey or None.</summary>
    public string CriticalReason { get; init; } = "None";

    /// <summary>On-disk size.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Row estimate of the index.</summary>
    public long? EstimatedRows { get; init; }

    /// <summary>Point lookups; on PostgreSQL the engine's single combined scan counter.</summary>
    public long? Seeks { get; init; }

    /// <summary>Range and full scans, where the engine splits the counter.</summary>
    public long? Scans { get; init; }

    /// <summary>Maintenance writes into the index (SQL Server).</summary>
    public long? Updates { get; init; }

    /// <summary>When the index last served a read.</summary>
    public DateTimeOffset? LastUsed { get; init; }
}

/// <summary>Result of refreshing planner statistics.</summary>
public sealed record RedbAnalyzeResult
{
    /// <summary>Instance that was analyzed; empty for the host's own storage.</summary>
    public string Instance { get; init; } = "";

    /// <summary>Table that was analyzed; null when the whole database was.</summary>
    public string? Table { get; init; }

    /// <summary>Schema of that table.</summary>
    public string? Schema { get; init; }

    /// <summary>How long the refresh took.</summary>
    public long ElapsedMs { get; init; }
}
