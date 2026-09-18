using System.Data.Common;
using Microsoft.Extensions.Configuration;
using redb.Tsak.Core.Audit;

namespace redb.Tsak.Core.Dlq;

/// <summary>
/// The dead-letter table and its statements. Lives on the same database and provider as the audit log
/// (<see cref="AuditStorage"/>); everything provider-specific — connection string, ADO provider, DDL
/// script, casts, paging — comes from the provider's <see cref="TsakSqlDialect"/>.
/// Every statement except the insert is scoped to the node's cluster plus the entries written before
/// cluster isolation (<see cref="ClusterColumn.ScopeCondition"/>, parameter <c>@cluster</c>).
/// </summary>
public static class DlqStorage
{
    public const string TableName = "tsak_dlq";

    /// <summary>Index on the cluster column, created by the schema initializer.</summary>
    public const string ClusterIndexName = "ix_tsak_dlq_cluster";

    /// <summary>Reuse the audit provider enum — the DLQ lives on the same database.</summary>
    public static AuditProvider ResolveProvider(IConfiguration configuration) =>
        AuditStorage.ResolveProvider(configuration);

    public static string? ResolveConnectionString(IConfiguration configuration, AuditProvider provider) =>
        AuditStorage.ResolveConnectionString(configuration, provider);

    public static string ScriptResource(AuditProvider provider) =>
        $"DlqSchema.{TsakSqlDialect.For(provider).ScriptSuffix}";

    public static DbConnection CreateConnection(AuditProvider provider, string connectionString) =>
        AuditStorage.CreateConnection(provider, connectionString);

    /// <summary>
    /// INSERT. Column list and parameter names are identical everywhere; the entry id is bound as text
    /// and cast by the dialect where the column is <c>uuid</c>. The timestamp is bound typed
    /// (<see cref="TsakSqlDialect.BindTimestamp"/>), so it needs no cast.
    /// </summary>
    public static string InsertSql(AuditProvider provider)
    {
        var entryId = TsakSqlDialect.For(provider).Uuid("@entry_id");
        return $"""
            INSERT INTO {TableName}
                (entry_id, occurred_at, context_name, route_id, marker_name, status,
                 exception_type, exception_message, correlation_id,
                 body_kind, body_type, body_data, headers_json, properties_json, replayable, cluster_name)
            VALUES
                ({entryId}, @occurred_at, @context_name, @route_id, @marker_name, @status,
                 @exception_type, @exception_message, @correlation_id,
                 @body_kind, @body_type, @body_data, @headers_json, @properties_json, @replayable, @cluster_name)
            """;
    }

    /// <summary>Paged SELECT, newest-first, all filters optional.</summary>
    public static string SelectSql(AuditProvider provider)
    {
        const string body = $"""
            SELECT entry_id, occurred_at, context_name, route_id, marker_name, status,
                   exception_type, exception_message, correlation_id, body_kind, replayable, replayed_at
            FROM {TableName}
            WHERE {ClusterColumn.ScopeCondition}
              AND (@context IS NULL OR context_name = @context)
              AND (@route IS NULL OR route_id = @route)
              AND (@status IS NULL OR status = @status)
              AND (@since IS NULL OR occurred_at >= @since)
              AND (@until IS NULL OR occurred_at <= @until)
            ORDER BY occurred_at DESC
            """;
        return body + "\n" + TsakSqlDialect.For(provider).Paging("@limit", "@offset");
    }

    /// <summary>Full row for one entry (includes the serialized payload) — used by replay.</summary>
    public static string SelectOneSql(AuditProvider provider)
    {
        var entryId = TsakSqlDialect.For(provider).Uuid("@entry_id");
        return $"""
            SELECT entry_id, occurred_at, context_name, route_id, marker_name, status,
                   exception_type, exception_message, correlation_id,
                   body_kind, body_type, body_data, headers_json, properties_json, replayable, replayed_at,
                   cluster_name
            FROM {TableName}
            WHERE entry_id = {entryId} AND {ClusterColumn.ScopeCondition}
            """;
    }

    public static string UpdateStatusSql(AuditProvider provider)
    {
        var entryId = TsakSqlDialect.For(provider).Uuid("@entry_id");
        return $"UPDATE {TableName} SET status = @status, replayed_at = @replayed_at " +
               $"WHERE entry_id = {entryId} AND {ClusterColumn.ScopeCondition}";
    }

    /// <summary>
    /// Atomic replay claim: flips a single entry <c>@from_status</c> → <c>@to_status</c>, but only if it
    /// is still in <c>@from_status</c>. The DB serializes the conditional UPDATE, so exactly one
    /// concurrent caller sees <c>affected == 1</c> (the winner) — the rest see <c>0</c>. Prevents two
    /// operators / a double-click from replaying the same failed exchange twice.
    /// </summary>
    public static string ClaimForReplaySql(AuditProvider provider)
    {
        var entryId = TsakSqlDialect.For(provider).Uuid("@entry_id");
        return $"UPDATE {TableName} SET status = @to_status, replayed_at = @claimed_at " +
               $"WHERE entry_id = {entryId} AND status = @from_status AND {ClusterColumn.ScopeCondition}";
    }

    public static string DeleteOneSql(AuditProvider provider)
    {
        var entryId = TsakSqlDialect.For(provider).Uuid("@entry_id");
        return $"DELETE FROM {TableName} WHERE entry_id = {entryId} AND {ClusterColumn.ScopeCondition}";
    }

    /// <summary>Retention sweep. Cutoff computed in C#, passed as a parameter — no server-side date math.</summary>
    public static string DeleteOlderThanSql() =>
        $"DELETE FROM {TableName} WHERE occurred_at < @cutoff AND {ClusterColumn.ScopeCondition}";
}
