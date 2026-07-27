using System.Data.Common;
using Microsoft.Extensions.Configuration;

namespace redb.Tsak.Core.Dlq;

/// <summary>
/// Provider-specific pieces of the dead-letter store: which DDL script to run, which ADO provider to
/// open, and the statements whose dialects differ. Mirrors <c>AuditStorage</c> — same config key,
/// same connection-string names, same embedded-script approach — because the DLQ is another flat,
/// append-mostly table on whichever provider Tsak is configured with.
/// </summary>
public static class DlqStorage
{
    public const string TableName = "tsak_dlq";

    /// <summary>Reuse the audit provider enum — the DLQ lives on the same database.</summary>
    public static Audit.AuditProvider ResolveProvider(IConfiguration configuration) =>
        Audit.AuditStorage.ResolveProvider(configuration);

    public static string? ResolveConnectionString(IConfiguration configuration, Audit.AuditProvider provider) =>
        Audit.AuditStorage.ResolveConnectionString(configuration, provider);

    public static string ScriptResource(Audit.AuditProvider provider) => provider switch
    {
        Audit.AuditProvider.SqlServer => "DlqSchema.SqlServer",
        Audit.AuditProvider.Sqlite => "DlqSchema.Sqlite",
        _ => "DlqSchema.Postgres"
    };

    public static DbConnection CreateConnection(Audit.AuditProvider provider, string connectionString) =>
        Audit.AuditStorage.CreateConnection(provider, connectionString);

    /// <summary>
    /// INSERT. Column list and parameter names are identical everywhere; only PostgreSQL needs a cast
    /// for the <c>uuid</c> entry id.
    /// </summary>
    public static string InsertSql(Audit.AuditProvider provider)
    {
        var entryId = provider == Audit.AuditProvider.Postgres ? "@entry_id::uuid" : "@entry_id";
        return $"""
            INSERT INTO {TableName}
                (entry_id, occurred_at, context_name, route_id, marker_name, status,
                 exception_type, exception_message, correlation_id,
                 body_kind, body_type, body_data, headers_json, properties_json, replayable)
            VALUES
                ({entryId}, @occurred_at, @context_name, @route_id, @marker_name, @status,
                 @exception_type, @exception_message, @correlation_id,
                 @body_kind, @body_type, @body_data, @headers_json, @properties_json, @replayable)
            """;
    }

    /// <summary>Paged SELECT, newest-first, all filters optional. Paging differs only on SQL Server.</summary>
    public static string SelectSql(Audit.AuditProvider provider)
    {
        const string body = $"""
            SELECT entry_id, occurred_at, context_name, route_id, marker_name, status,
                   exception_type, exception_message, correlation_id, body_kind, replayable, replayed_at
            FROM {TableName}
            WHERE (@context IS NULL OR context_name = @context)
              AND (@route IS NULL OR route_id = @route)
              AND (@status IS NULL OR status = @status)
              AND (@since IS NULL OR occurred_at >= @since)
              AND (@until IS NULL OR occurred_at <= @until)
            ORDER BY occurred_at DESC
            """;
        return provider == Audit.AuditProvider.SqlServer
            ? body + "\nOFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY"
            : body + "\nLIMIT @limit OFFSET @offset";
    }

    /// <summary>Full row for one entry (includes the serialized payload) — used by replay.</summary>
    public static string SelectOneSql(Audit.AuditProvider provider)
    {
        var idParam = provider == Audit.AuditProvider.Postgres ? "@entry_id::uuid" : "@entry_id";
        return $"""
            SELECT entry_id, occurred_at, context_name, route_id, marker_name, status,
                   exception_type, exception_message, correlation_id,
                   body_kind, body_type, body_data, headers_json, properties_json, replayable, replayed_at
            FROM {TableName}
            WHERE entry_id = {idParam}
            """;
    }

    public static string UpdateStatusSql(Audit.AuditProvider provider)
    {
        var idParam = provider == Audit.AuditProvider.Postgres ? "@entry_id::uuid" : "@entry_id";
        return $"UPDATE {TableName} SET status = @status, replayed_at = @replayed_at WHERE entry_id = {idParam}";
    }

    public static string DeleteOneSql(Audit.AuditProvider provider)
    {
        var idParam = provider == Audit.AuditProvider.Postgres ? "@entry_id::uuid" : "@entry_id";
        return $"DELETE FROM {TableName} WHERE entry_id = {idParam}";
    }

    /// <summary>Retention sweep. Cutoff computed in C#, passed as a parameter — no server-side date math.</summary>
    public static string DeleteOlderThanSql() => $"DELETE FROM {TableName} WHERE occurred_at < @cutoff";
}
