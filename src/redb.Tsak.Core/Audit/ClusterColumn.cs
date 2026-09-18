using System.Data.Common;

namespace redb.Tsak.Core.Audit;

/// <summary>
/// The <c>cluster_name</c> column of Tsak's flat tables (<c>tsak_dlq</c>, <c>tsak_audit_log</c>).
/// Several Tsak clusters may share one database, so every row carries the cluster that wrote it, and
/// every read, write and sweep of a node is scoped to its cluster plus the rows written before cluster
/// isolation (null). The DDL scripts create the column on a fresh database; on an existing one their
/// <c>CREATE TABLE</c> is a no-op, so <see cref="EnsureAsync"/> adds it through the provider's dialect.
/// </summary>
internal static class ClusterColumn
{
    public const string Name = "cluster_name";

    /// <summary>Width of the column where the provider has one (the DDL scripts declare the same).</summary>
    public const int MaxLength = 200;

    /// <summary>Scope condition of a node's statements; binds <c>@cluster</c>.</summary>
    public const string ScopeCondition = "(cluster_name = @cluster OR cluster_name IS NULL)";

    /// <summary>The parameter <see cref="ScopeCondition"/> reads.</summary>
    public static void BindScope(DbCommand cmd, string clusterName) =>
        DbParameterBinding.AddString(cmd, "cluster", clusterName);

    public static Task EnsureAsync(
        DbConnection conn, AuditProvider provider, string table, string indexName, CancellationToken ct) =>
        TsakSqlDialect.For(provider).EnsureNullableTextColumnAsync(conn, table, Name, MaxLength, indexName, ct);
}
