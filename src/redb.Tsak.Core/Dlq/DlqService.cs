using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Audit;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Dlq;

/// <summary>
/// The dead-letter store and its operations: capture a failed exchange (from a route checkpoint),
/// query/page the store, replay an entry back through its route, discard, and prune. Raw ADO.NET on
/// purpose — the table is flat and provider-specific, like the audit store.
/// </summary>
public sealed class DlqService
{
    private readonly IConfiguration _configuration;
    private readonly ITsakContextManager _contextManager;
    private readonly ILogger<DlqService> _logger;

    public DlqService(IConfiguration configuration, ITsakContextManager contextManager, ILogger<DlqService> logger)
    {
        _configuration = configuration;
        _contextManager = contextManager;
        _logger = logger;
    }

    public AuditProvider Provider => DlqStorage.ResolveProvider(_configuration);

    public bool IsAvailable =>
        _configuration.GetValue("Tsak:Dlq:Enabled", true)
        && Provider != AuditProvider.None
        && !string.IsNullOrEmpty(DlqStorage.ResolveConnectionString(_configuration, Provider));

    // ── capture ──────────────────────────────────────────────────────

    /// <summary>Persists a failed exchange captured at a route checkpoint. Never throws.</summary>
    public async Task CaptureAsync(string contextName, RouteCheckpoint checkpoint, Exception exception, CancellationToken ct = default)
    {
        if (!IsAvailable) return;

        try
        {
            var s = ExchangeSnapshotCodec.Serialize(checkpoint.Snapshot);
            var correlationId = checkpoint.Snapshot.In.Headers.TryGetValue("CorrelationId", out var c) ? c?.ToString() : null;

            var provider = Provider;
            var connStr = DlqStorage.ResolveConnectionString(_configuration, provider)!;
            await using var conn = DlqStorage.CreateConnection(provider, connStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = DlqStorage.InsertSql(provider);
            AddParam(cmd, "entry_id", Guid.NewGuid().ToString());
            AddParam(cmd, "occurred_at", NowIso());
            AddParam(cmd, "context_name", contextName);
            AddParam(cmd, "route_id", checkpoint.RouteId);
            AddParam(cmd, "marker_name", checkpoint.MarkerName);
            AddParam(cmd, "status", "pending");
            AddParam(cmd, "exception_type", exception.GetType().Name);
            AddParam(cmd, "exception_message", Truncate(exception.Message, 2000));
            AddParam(cmd, "correlation_id", correlationId);
            AddParam(cmd, "body_kind", s.BodyKind);
            AddParam(cmd, "body_type", s.BodyType);
            AddParam(cmd, "body_data", s.BodyData);
            AddParam(cmd, "headers_json", s.HeadersJson);
            AddParam(cmd, "properties_json", s.PropertiesJson);
            AddParam(cmd, "replayable", s.Replayable ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);

            _logger.LogInformation("DLQ captured failed exchange: route={Route} marker={Marker} ex={Ex}",
                checkpoint.RouteId, checkpoint.MarkerName, exception.GetType().Name);
        }
        catch (Exception ex)
        {
            // Capturing a dead-letter must never break the failure path further.
            _logger.LogWarning(ex, "DLQ capture failed for route {Route}", checkpoint.RouteId);
        }
    }

    // ── query ────────────────────────────────────────────────────────

    public async Task<FailedExchangeQueryResult> QueryAsync(
        string? context = null, string? route = null, string? status = null,
        DateTimeOffset? since = null, DateTimeOffset? until = null,
        int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        if (!IsAvailable) return new FailedExchangeQueryResult { Available = false, Entries = [] };

        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(offset, 0);
        var provider = Provider;
        var connStr = DlqStorage.ResolveConnectionString(_configuration, provider)!;

        try
        {
            await using var conn = DlqStorage.CreateConnection(provider, connStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = DlqStorage.SelectSql(provider);
            AddParam(cmd, "context", context);
            AddParam(cmd, "route", route);
            AddParam(cmd, "status", status);
            AddParam(cmd, "since", Iso(since));
            AddParam(cmd, "until", Iso(until));
            AddParam(cmd, "limit", limit);
            AddParam(cmd, "offset", offset);

            var entries = new List<FailedExchangeEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                entries.Add(MapSummary(reader));

            return new FailedExchangeQueryResult
            {
                Available = true, Count = entries.Count, Limit = limit, Offset = offset, Entries = entries.ToArray()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DLQ query failed ({Provider})", provider);
            return new FailedExchangeQueryResult { Available = false, Error = ex.Message, Entries = [] };
        }
    }

    // ── replay / discard / prune ─────────────────────────────────────

    public async Task<ExchangeReplayResult> ReplayAsync(string entryId, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return new ExchangeReplayResult { Success = false, Message = "DLQ is not available (no database)." };

        var provider = Provider;
        var connStr = DlqStorage.ResolveConnectionString(_configuration, provider)!;

        DlqRow? row;
        try
        {
            await using var conn = DlqStorage.CreateConnection(provider, connStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = DlqStorage.SelectOneSql(provider);
            AddParam(cmd, "entry_id", entryId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            row = await reader.ReadAsync(ct) ? MapFull(reader) : null;
        }
        catch (Exception ex)
        {
            return new ExchangeReplayResult { Success = false, Message = $"Load failed: {ex.Message}", EntryId = entryId };
        }

        if (row is null)
            return new ExchangeReplayResult { Success = false, Message = "Entry not found.", EntryId = entryId };
        if (!row.Replayable)
            return new ExchangeReplayResult { Success = false, Message = "Entry body did not round-trip; not replayable.", EntryId = entryId };

        var context = _contextManager.GetContext(row.ContextName);
        if (context is null)
            return new ExchangeReplayResult { Success = false, Message = $"Context '{row.ContextName}' is not loaded.", EntryId = entryId };

        try
        {
            var snapshot = ExchangeSnapshotCodec.Rehydrate(new SerializedSnapshot(
                row.BodyKind, row.BodyType, row.BodyData, row.HeadersJson, row.PropertiesJson, row.Replayable));

            await context.ReplayAsync(row.RouteId, row.MarkerName, snapshot, ct);
            await UpdateStatusAsync(entryId, "replayed", NowIso(), ct);

            return new ExchangeReplayResult { Success = true, Message = "Replayed.", EntryId = entryId };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DLQ replay failed for entry {Entry} (route {Route})", entryId, row.RouteId);
            return new ExchangeReplayResult { Success = false, Message = $"Replay failed: {ex.Message}", EntryId = entryId };
        }
    }

    public async Task<bool> DiscardAsync(string entryId, CancellationToken ct = default)
    {
        if (!IsAvailable) return false;
        var provider = Provider;
        var connStr = DlqStorage.ResolveConnectionString(_configuration, provider)!;
        await using var conn = DlqStorage.CreateConnection(provider, connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = DlqStorage.DeleteOneSql(provider);
        AddParam(cmd, "entry_id", entryId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        if (!IsAvailable) return 0;
        var provider = Provider;
        var connStr = DlqStorage.ResolveConnectionString(_configuration, provider)!;
        await using var conn = DlqStorage.CreateConnection(provider, connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = DlqStorage.DeleteOlderThanSql();
        AddParam(cmd, "cutoff", Iso(cutoff));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateStatusAsync(string entryId, string status, string replayedAtIso, CancellationToken ct)
    {
        var provider = Provider;
        var connStr = DlqStorage.ResolveConnectionString(_configuration, provider)!;
        await using var conn = DlqStorage.CreateConnection(provider, connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = DlqStorage.UpdateStatusSql(provider);
        AddParam(cmd, "status", status);
        AddParam(cmd, "replayed_at", replayedAtIso);
        AddParam(cmd, "entry_id", entryId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── mapping / helpers ────────────────────────────────────────────

    private sealed record DlqRow(
        string EntryId, string ContextName, string RouteId, string MarkerName,
        string BodyKind, string? BodyType, string? BodyData, string HeadersJson, string PropertiesJson, bool Replayable);

    private static FailedExchangeEntry MapSummary(DbDataReader r) => new()
    {
        EntryId = GetString(r, "entry_id") ?? "",
        OccurredAt = ParseTs(r, "occurred_at"),
        ContextName = GetString(r, "context_name") ?? "",
        RouteId = GetString(r, "route_id") ?? "",
        MarkerName = GetString(r, "marker_name") ?? "",
        Status = GetString(r, "status") ?? "pending",
        ExceptionType = GetString(r, "exception_type"),
        ExceptionMessage = GetString(r, "exception_message"),
        CorrelationId = GetString(r, "correlation_id"),
        BodyKind = GetString(r, "body_kind") ?? "none",
        Replayable = GetBool(r, "replayable"),
        ReplayedAt = r.IsDBNull(r.GetOrdinal("replayed_at")) ? null : ParseTs(r, "replayed_at")
    };

    private static DlqRow MapFull(DbDataReader r) => new(
        GetString(r, "entry_id") ?? "", GetString(r, "context_name") ?? "", GetString(r, "route_id") ?? "",
        GetString(r, "marker_name") ?? "", GetString(r, "body_kind") ?? "none", GetString(r, "body_type"),
        GetString(r, "body_data"), GetString(r, "headers_json") ?? "{}", GetString(r, "properties_json") ?? "{}",
        GetBool(r, "replayable"));

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static string NowIso() => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
    private static string? Iso(DateTimeOffset? v) => v?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static string? Truncate(string? v, int max) => v is null || v.Length <= max ? v : v[..max];

    private static string? GetString(DbDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return null;
        var v = r.GetValue(i);
        return v as string ?? v.ToString();
    }

    private static bool GetBool(DbDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return false;
        return r.GetValue(i) switch
        {
            bool b => b,
            long l => l != 0,
            int n => n != 0,
            _ => Convert.ToInt64(r.GetValue(i)) != 0
        };
    }

    private static DateTimeOffset ParseTs(DbDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return default;
        return r.GetValue(i) switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var p) => p,
            _ => default
        };
    }
}
