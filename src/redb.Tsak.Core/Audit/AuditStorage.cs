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
/// Everything provider-specific about the audit table lives here: which DDL script to run,
/// which ADO provider to open the connection with, and the three statements whose dialects
/// actually differ. Mirrors the arrangement <c>QuartzSchemaInitializer</c> already uses for
/// the Quartz tables — same config key, same connection-string names, same embedded-script
/// approach.
/// </summary>
public static class AuditStorage
{
    /// <summary>Table name, shared by all three dialects.</summary>
    public const string TableName = "tsak_audit_log";

    /// <summary>Endpoint the audit writer route consumes from.</summary>
    public const string AuditEndpoint = "direct://tsak-audit";

    /// <summary>Registry key under which the audit data source is published to the route context.</summary>
    public const string DataSourceName = "tsak-audit-ds";

    /// <summary>Resolves the configured provider from <c>Tsak:Redb:Provider</c>.</summary>
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
        provider switch
        {
            AuditProvider.SqlServer => configuration.GetConnectionString("MSSql"),
            AuditProvider.Sqlite => configuration.GetConnectionString("Sqlite"),
            AuditProvider.Postgres => configuration.GetConnectionString("Postgres"),
            _ => null
        };

    /// <summary>Logical name of the embedded DDL script for the provider.</summary>
    public static string ScriptResource(AuditProvider provider) => provider switch
    {
        AuditProvider.SqlServer => "AuditSchema.SqlServer",
        AuditProvider.Sqlite => "AuditSchema.Sqlite",
        _ => "AuditSchema.Postgres"
    };

    /// <summary>ADO.NET invariant name, used to register the factory for redb.Route.Sql.</summary>
    public static string ProviderInvariantName(AuditProvider provider) => provider switch
    {
        AuditProvider.SqlServer => "Microsoft.Data.SqlClient",
        AuditProvider.Sqlite => "Microsoft.Data.Sqlite",
        _ => "Npgsql"
    };

    /// <summary>The <see cref="DbProviderFactory"/> backing <see cref="ProviderInvariantName"/>.</summary>
    public static DbProviderFactory ProviderFactory(AuditProvider provider) => provider switch
    {
        AuditProvider.SqlServer => Microsoft.Data.SqlClient.SqlClientFactory.Instance,
        AuditProvider.Sqlite => Microsoft.Data.Sqlite.SqliteFactory.Instance,
        _ => Npgsql.NpgsqlFactory.Instance
    };

    /// <summary>Opens a raw connection — used by the schema initializer and the retention sweep.</summary>
    public static DbConnection CreateConnection(AuditProvider provider, string connectionString)
    {
        var conn = ProviderFactory(provider).CreateConnection()
                   ?? throw new InvalidOperationException(
                       $"DbProviderFactory for {provider} returned null from CreateConnection().");
        conn.ConnectionString = connectionString;
        return conn;
    }

    /// <summary>
    /// INSERT statement. Column list and parameter names are identical everywhere — only
    /// PostgreSQL needs an explicit cast, because a plain string parameter cannot be assigned
    /// to <c>uuid</c> / <c>jsonb</c> columns.
    /// </summary>
    public static string InsertSql(AuditProvider provider)
    {
        var (eventId, payload) = provider == AuditProvider.Postgres
            ? ("@event_id::uuid", "@payload::jsonb")
            : ("@event_id", "@payload");

        return $"""
            INSERT INTO {TableName}
                (event_id, ts, action, controller_type, actor_principal, actor_key_id,
                 remote_ip, user_agent, http_method, request_path, target_resource,
                 status_code, duration_ms, exception_type, exception_message, payload)
            VALUES
                ({eventId}, @ts, @action, @controller_type, @actor_principal, @actor_key_id,
                 @remote_ip, @user_agent, @http_method, @request_path, @target_resource,
                 @status_code, @duration_ms, @exception_type, @exception_message, {payload})
            """;
    }

    /// <summary>
    /// Paged SELECT, newest first. Filters are all optional and expressed as
    /// <c>(@x IS NULL OR col = @x)</c> so a single statement serves every combination.
    /// Paging is the one construct SQL Server spells differently.
    /// </summary>
    public static string SelectSql(AuditProvider provider)
    {
        const string body = $"""
            SELECT event_id, ts, action, controller_type, actor_principal, actor_key_id,
                   remote_ip, user_agent, http_method, request_path, target_resource,
                   status_code, duration_ms, exception_type, exception_message, payload
            FROM {TableName}
            WHERE (@actor IS NULL OR actor_key_id = @actor OR actor_principal = @actor)
              AND (@action IS NULL OR action = @action)
              AND (@target IS NULL OR target_resource = @target)
              AND (@since IS NULL OR ts >= @since)
              AND (@until IS NULL OR ts <= @until)
            ORDER BY ts DESC
            """;

        return provider == AuditProvider.SqlServer
            ? body + "\nOFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY"
            : body + "\nLIMIT @limit OFFSET @offset";
    }

    /// <summary>
    /// Retention sweep. Identical on all three providers: the cutoff is computed in C# and
    /// passed as a parameter, so no server-side date arithmetic is involved.
    /// </summary>
    public static string DeleteOlderThanSql() =>
        $"DELETE FROM {TableName} WHERE ts < @cutoff";
}
