using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using redb.Tsak.Contracts;

namespace redb.Tsak.Core.Audit;

/// <summary>
/// Reads and prunes <c>tsak_audit_log</c>. Raw ADO.NET on purpose — the table is flat,
/// append-only and provider-specific, so going through redb would buy nothing and cost the
/// PVT round-trip on every filter.
/// <para>
/// All filtering and paging happens server-side; nothing is materialised and filtered in
/// memory. Timestamps are passed as ISO-8601 strings, which every provider compares correctly
/// against its own column type (<c>timestamptz</c>, <c>datetimeoffset</c>, or the ISO-8601
/// <c>TEXT</c> used by SQLite).
/// </para>
/// </summary>
public sealed class AuditQueryService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuditQueryService> _logger;

    public AuditQueryService(IConfiguration configuration, ILogger<AuditQueryService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Provider Tsak is configured with; <see cref="AuditProvider.None"/> means no database.</summary>
    public AuditProvider Provider => AuditStorage.ResolveProvider(_configuration);

    /// <summary>True when the audit table exists and can be queried.</summary>
    public bool IsAvailable =>
        _configuration.GetValue("Tsak:Audit:Enabled", true)
        && Provider != AuditProvider.None
        && !string.IsNullOrEmpty(AuditStorage.ResolveConnectionString(_configuration, Provider));

    /// <summary>Newest-first page of audit entries matching the (all optional) filters.</summary>
    public async Task<AuditQueryResult> QueryAsync(
        string? actor = null,
        string? action = null,
        string? target = null,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return new AuditQueryResult { Available = false, Entries = [] };

        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(offset, 0);

        var provider = Provider;
        var connStr = AuditStorage.ResolveConnectionString(_configuration, provider)!;

        try
        {
            await using var conn = AuditStorage.CreateConnection(provider, connStr);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = AuditStorage.SelectSql(provider);
            AddParam(cmd, "actor", actor);
            AddParam(cmd, "action", action);
            AddParam(cmd, "target", target);
            AddParam(cmd, "since", Format(since));
            AddParam(cmd, "until", Format(until));
            AddParam(cmd, "limit", limit);
            AddParam(cmd, "offset", offset);

            var entries = new List<AuditEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                entries.Add(Map(reader));

            return new AuditQueryResult
            {
                Available = true,
                Count = entries.Count,
                Limit = limit,
                Offset = offset,
                Entries = entries.ToArray()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit query failed ({Provider})", provider);
            return new AuditQueryResult { Available = false, Error = ex.Message, Entries = [] };
        }
    }

    /// <summary>Deletes entries older than the cutoff. Returns the number of rows removed.</summary>
    public async Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        if (!IsAvailable) return 0;

        var provider = Provider;
        var connStr = AuditStorage.ResolveConnectionString(_configuration, provider)!;

        await using var conn = AuditStorage.CreateConnection(provider, connStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = AuditStorage.DeleteOlderThanSql();
        AddParam(cmd, "cutoff", Format(cutoff));

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static string? Format(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static AuditEntry Map(DbDataReader r) => new()
    {
        EventId = GetString(r, "event_id") ?? "",
        Timestamp = ParseTimestamp(r, "ts"),
        Action = GetString(r, "action") ?? "",
        ControllerType = GetString(r, "controller_type"),
        ActorPrincipal = GetString(r, "actor_principal"),
        ActorKeyId = GetString(r, "actor_key_id"),
        RemoteIp = GetString(r, "remote_ip"),
        UserAgent = GetString(r, "user_agent"),
        HttpMethod = GetString(r, "http_method"),
        RequestPath = GetString(r, "request_path"),
        TargetResource = GetString(r, "target_resource"),
        StatusCode = r.GetInt32(r.GetOrdinal("status_code")),
        DurationMs = r.GetDouble(r.GetOrdinal("duration_ms")),
        ExceptionType = GetString(r, "exception_type"),
        ExceptionMessage = GetString(r, "exception_message"),
        Payload = GetString(r, "payload")
    };

    private static string? GetString(DbDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        if (r.IsDBNull(i)) return null;
        // uuid (PG) and uniqueidentifier (MSSQL) come back as Guid, not string.
        var value = r.GetValue(i);
        return value as string ?? value.ToString();
    }

    /// <summary>
    /// PostgreSQL and SQL Server return a native date type; SQLite returns the ISO-8601 string
    /// this code wrote.
    /// </summary>
    private static DateTimeOffset ParseTimestamp(DbDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        if (r.IsDBNull(i)) return default;

        return r.GetValue(i) switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed) => parsed,
            _ => default
        };
    }
}
