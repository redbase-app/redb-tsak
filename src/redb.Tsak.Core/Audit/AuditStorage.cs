using System.Data.Common;
using Microsoft.Extensions.Configuration;

namespace redb.Tsak.Core.Audit;

/// <summary>
/// The three storage backends Tsak can be configured with, plus "none" for the
/// in-memory / no-database mode where the audit trail falls back to the log sink.
/// </summary>
public enum AuditProvider
{
    None,
    Postgres,
    SqlServer,
    Sqlite
}

/// <summary>
/// The audit table and its statements. Everything provider-specific — connection string, ADO provider,
/// DDL script, casts, paging — comes from the provider's <see cref="TsakSqlDialect"/>, so a statement is
/// written once and every provider gets its own spelling of it. The select and the retention sweep are
/// scoped to the node's cluster plus the entries written before cluster isolation
/// (<see cref="ClusterColumn.ScopeCondition"/>, parameter <c>@cluster</c>).
/// </summary>
public static class AuditStorage
{
    /// <summary>Table name, shared by all three dialects.</summary>
    public const string TableName = "tsak_audit_log";

    /// <summary>Index on the cluster column, created by the schema initializer.</summary>
    public const string ClusterIndexName = "ix_tsak_audit_cluster";

    /// <summary>Endpoint the audit writer route consumes from.</summary>
    public const string AuditEndpoint = "direct://tsak-audit";

    /// <summary>Registry key under which the audit data source is published to the route context.</summary>
    public const string DataSourceName = "tsak-audit-ds";

    /// <summary>
    /// Resolves the configured provider from <c>Tsak:Redb:Provider</c> — the one place the setting is
    /// parsed for Tsak's own tables and the Quartz job store.
    /// </summary>
    public static AuditProvider ResolveProvider(IConfiguration configuration) =>
        configuration["Tsak:Redb:Provider"]?.ToLowerInvariant() switch
        {
            "mssql" or "sqlserver" => AuditProvider.SqlServer,
            "sqlite" => AuditProvider.Sqlite,
            "postgres" or "postgresql" or "pg" or "npgsql" => AuditProvider.Postgres,
            null or "" => AuditProvider.None,
            // Unknown value: redb itself defaults to Postgres for anything it does not
            // recognise, so stay consistent rather than silently disabling the audit.
            _ => AuditProvider.Postgres
        };

    /// <summary>Connection string for the provider, read from the same names redb uses.</summary>
    public static string? ResolveConnectionString(IConfiguration configuration, AuditProvider provider) =>
        provider == AuditProvider.None
            ? null
            : configuration.GetConnectionString(TsakSqlDialect.For(provider).ConnectionStringName);

    /// <summary>Logical name of the embedded DDL script for the provider.</summary>
    public static string ScriptResource(AuditProvider provider) =>
        $"AuditSchema.{TsakSqlDialect.For(provider).ScriptSuffix}";

    /// <summary>ADO.NET invariant name, used to register the factory for redb.Route.Sql.</summary>
    public static string ProviderInvariantName(AuditProvider provider) =>
        TsakSqlDialect.For(provider).InvariantName;

    /// <summary>The <see cref="DbProviderFactory"/> backing <see cref="ProviderInvariantName"/>.</summary>
    public static DbProviderFactory ProviderFactory(AuditProvider provider) =>
        TsakSqlDialect.For(provider).Factory;

    /// <summary>Opens a raw connection — used by the schema initializers and the stores.</summary>
    public static DbConnection CreateConnection(AuditProvider provider, string connectionString)
    {
        var conn = ProviderFactory(provider).CreateConnection()
                   ?? throw new InvalidOperationException(
                       $"DbProviderFactory for {provider} returned null from CreateConnection().");
        conn.ConnectionString = connectionString;
        return conn;
    }

    /// <summary>
    /// INSERT statement. This is the one statement of the audit log that runs through the redb.Route.Sql
    /// connector (the writer route), so its placeholders are written <c>:#name</c>, as that connector reads
    /// them; <c>@</c> there belongs to the database. Every other statement here and in the dead-letter store
    /// runs on raw ADO.NET and keeps <c>@name</c>.
    /// The writer route binds every value as it arrives in the <c>audit.*</c> headers — the event id, the
    /// timestamp (ISO-8601 <c>"o"</c>) and the payload as text — so each goes through the dialect's cast for
    /// its column type: PostgreSQL refuses text for <c>uuid</c> / <c>timestamptz</c> / <c>jsonb</c> (42804)
    /// without one.
    /// </summary>
    public static string InsertSql(AuditProvider provider)
    {
        var d = TsakSqlDialect.For(provider);
        return $"""
            INSERT INTO {TableName}
                (event_id, ts, action, controller_type, actor_principal, actor_key_id,
                 remote_ip, user_agent, http_method, request_path, target_resource,
                 status_code, duration_ms, exception_type, exception_message, payload, cluster_name)
            VALUES
                ({d.Uuid(":#event_id")}, {d.TextTimestamp(":#ts")}, :#action, :#controller_type, :#actor_principal, :#actor_key_id,
                 :#remote_ip, :#user_agent, :#http_method, :#request_path, :#target_resource,
                 :#status_code, :#duration_ms, :#exception_type, :#exception_message, {d.Json(":#payload")}, :#cluster_name)
            """;
    }

    /// <summary>
    /// Paged SELECT, newest first. Filters are all optional and expressed as
    /// <c>(@x IS NULL OR col = @x)</c> so a single statement serves every combination.
    /// </summary>
    public static string SelectSql(AuditProvider provider)
    {
        const string body = $"""
            SELECT event_id, ts, action, controller_type, actor_principal, actor_key_id,
                   remote_ip, user_agent, http_method, request_path, target_resource,
                   status_code, duration_ms, exception_type, exception_message, payload
            FROM {TableName}
            WHERE {ClusterColumn.ScopeCondition}
              AND (@actor IS NULL OR actor_key_id = @actor OR actor_principal = @actor)
              AND (@action IS NULL OR action = @action)
              AND (@target IS NULL OR target_resource = @target)
              AND (@since IS NULL OR ts >= @since)
              AND (@until IS NULL OR ts <= @until)
            ORDER BY ts DESC
            """;

        return body + "\n" + TsakSqlDialect.For(provider).Paging("@limit", "@offset");
    }

    /// <summary>
    /// Retention sweep. Identical on all three providers: the cutoff is computed in C# and
    /// passed as a parameter, so no server-side date arithmetic is involved.
    /// </summary>
    public static string DeleteOlderThanSql() =>
        $"DELETE FROM {TableName} WHERE ts < @cutoff AND {ClusterColumn.ScopeCondition}";
}
