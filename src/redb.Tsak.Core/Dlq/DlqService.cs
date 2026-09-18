using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Audit;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Services.Storage;

namespace redb.Tsak.Core.Dlq;

/// <summary>
/// The dead-letter store and its operations: capture a failed exchange (from a route checkpoint),
/// query/page the store, replay an entry back through its route, discard, and prune. Raw ADO.NET on
/// purpose — the table is flat and provider-specific, like the audit store.
/// <para>
/// Several Tsak clusters may share the database: an entry carries the cluster that captured it
/// (<c>Tsak:Cluster:ClusterName</c>), and every operation sees only this node's cluster, so another
/// cluster's entry is "not found" here. An entry captured before cluster isolation has no cluster: it is
/// listed and pruned by every cluster, and replayed only while the database holds a single cluster
/// (<see cref="ITsakClusterDirectory"/>; without one — outside cluster mode — it is this node's own).
/// </para>
/// </summary>
public sealed class DlqService
{
    private readonly IConfiguration _configuration;
    private readonly ITsakContextManager _contextManager;
    private readonly ILogger<DlqService> _logger;
    private readonly ITsakClusterDirectory? _clusterDirectory;
    private readonly string _clusterName;

    public DlqService(
        IConfiguration configuration,
        ITsakContextManager contextManager,
        ILogger<DlqService> logger,
        ITsakClusterDirectory? clusterDirectory = null)
    {
        _configuration = configuration;
        _contextManager = contextManager;
        _logger = logger;
        _clusterDirectory = clusterDirectory;
        _clusterName = TsakStorageScope.ClusterName(configuration);
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
            AddDateParam(cmd, provider, "occurred_at", DateTimeOffset.UtcNow);
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
            // Bind a real bool: PostgreSQL BOOLEAN has NO implicit cast from integer, so an int 1/0
            // makes the whole INSERT throw (silently, via the catch) — the DLQ then captures nothing
            // on Postgres. bool maps cleanly to PG boolean / SQL Server bit / SQLite 0/1.
            AddParam(cmd, "replayable", s.Replayable);
            AddParam(cmd, "cluster_name", _clusterName);
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
            BindQueryParameters(cmd, provider, context, route, status, since, until, limit, offset);
            ClusterColumn.BindScope(cmd, _clusterName);

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

        // Attribution first: every statement is scoped to this cluster, so another cluster's entry is not
        // found. An entry captured before cluster isolation carries no cluster — while several clusters
        // share the database, it cannot be told whose it is, and replaying it could run another cluster's
        // exchange through this node's same-named route.
        try
        {
            var existing = await TryLoadRowAsync(entryId, ct).ConfigureAwait(false);
            if (existing is null)
                return new ExchangeReplayResult { Success = false, Message = "Entry not found.", EntryId = entryId };

            if (existing.ClusterName is null && await IsSharedByClustersAsync(ct).ConfigureAwait(false))
                return new ExchangeReplayResult
                {
                    Success = false, EntryId = entryId,
                    Message = "Entry was captured before cluster isolation and several clusters share this database — "
                              + "it cannot be attributed to this cluster, so it is not replayed."
                };
        }
        catch (Exception ex)
        {
            return new ExchangeReplayResult { Success = false, Message = $"Load failed: {ex.Message}", EntryId = entryId };
        }

        // Atomic claim (review item 3.3): flip pending → replaying, but only if still pending.
        // The DB serializes the conditional UPDATE, so of two concurrent replays exactly one wins the
        // claim and actually replays — the other is told it is already being handled. This is what
        // stops a double-click / two operators from replaying the same failed exchange twice. A crash
        // between claim and replay leaves the row in 'replaying' (visible, not lost) rather than
        // silently pending-and-forgotten.
        int claimed;
        try
        {
            claimed = await ClaimForReplayAsync(entryId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ExchangeReplayResult { Success = false, Message = $"Claim failed: {ex.Message}", EntryId = entryId };
        }

        if (claimed == 0)
        {
            // Not pending: not found, already replayed, or another replay claimed it first.
            var current = await TryLoadRowAsync(entryId, ct).ConfigureAwait(false);
            return current is null
                ? new ExchangeReplayResult { Success = false, Message = "Entry not found.", EntryId = entryId }
                : new ExchangeReplayResult { Success = false, EntryId = entryId, Message =
                    $"Entry is not available for replay (status '{current.Status}') — already replayed or being replayed." };
        }

        // We own the claim. Load the full payload.
        DlqRow? row;
        try
        {
            row = await TryLoadRowAsync(entryId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ReleaseClaimAsync(entryId, ct).ConfigureAwait(false);
            return new ExchangeReplayResult { Success = false, Message = $"Load failed: {ex.Message}", EntryId = entryId };
        }

        if (row is null) // deleted between claim and load — nothing left to release
            return new ExchangeReplayResult { Success = false, Message = "Entry not found.", EntryId = entryId };

        if (!row.Replayable)
        {
            await ReleaseClaimAsync(entryId, ct).ConfigureAwait(false);
            return new ExchangeReplayResult { Success = false, Message = "Entry body did not round-trip; not replayable.", EntryId = entryId };
        }

        var context = _contextManager.GetContext(row.ContextName);
        if (context is null)
        {
            await ReleaseClaimAsync(entryId, ct).ConfigureAwait(false);
            return new ExchangeReplayResult { Success = false, Message = $"Context '{row.ContextName}' is not loaded.", EntryId = entryId };
        }

        try
        {
            var snapshot = ExchangeSnapshotCodec.Rehydrate(new SerializedSnapshot(
                row.BodyKind, row.BodyType, row.BodyData, row.HeadersJson, row.PropertiesJson, row.Replayable));

            await context.ReplayAsync(row.RouteId, row.MarkerName, snapshot, ct);
            await UpdateStatusAsync(entryId, "replayed", DateTimeOffset.UtcNow, ct);

            return new ExchangeReplayResult { Success = true, Message = "Replayed.", EntryId = entryId };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DLQ replay failed for entry {Entry} (route {Route})", entryId, row.RouteId);
            // Release the claim so the entry can be retried rather than being stuck in 'replaying'.
            await ReleaseClaimAsync(entryId, ct).ConfigureAwait(false);
            return new ExchangeReplayResult { Success = false, Message = $"Replay failed: {ex.Message}", EntryId = entryId };
        }
    }

    /// <summary>True when the database holds more than one Tsak cluster (known only in cluster mode).</summary>
    private async Task<bool> IsSharedByClustersAsync(CancellationToken ct) =>
        _clusterDirectory is not null
        && await _clusterDirectory.CountClustersAsync(ct).ConfigureAwait(false) > 1;

    /// <summary>Atomic pending → replaying claim. Returns rows affected (1 = won, 0 = lost/not pending).</summary>
    private async Task<int> ClaimForReplayAsync(string entryId, CancellationToken ct)
    {
        var provider = Provider;
        var connStr = DlqStorage.ResolveConnectionString(_configuration, provider)!;
        await using var conn = DlqStorage.CreateConnection(provider, connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = DlqStorage.ClaimForReplaySql(provider);
        AddParam(cmd, "entry_id", entryId);
        AddParam(cmd, "from_status", "pending");
        AddParam(cmd, "to_status", "replaying");
        AddDateParam(cmd, provider, "claimed_at", DateTimeOffset.UtcNow);
        ClusterColumn.BindScope(cmd, _clusterName);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Release a claim back to pending (with replayed_at cleared) so the entry can be retried.</summary>
    private Task ReleaseClaimAsync(string entryId, CancellationToken ct) =>
        UpdateStatusAsync(entryId, "pending", null, ct);

    /// <summary>Loads one full DLQ row (with payload) or null if it no longer exists.</summary>
    private async Task<DlqRow?> TryLoadRowAsync(string entryId, CancellationToken ct)
    {
        var provider = Provider;
        var connStr = DlqStorage.ResolveConnectionString(_configuration, provider)!;
        await using var conn = DlqStorage.CreateConnection(provider, connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = DlqStorage.SelectOneSql(provider);
        AddParam(cmd, "entry_id", entryId);
        ClusterColumn.BindScope(cmd, _clusterName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapFull(reader) : null;
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
        ClusterColumn.BindScope(cmd, _clusterName);
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
        AddDateParam(cmd, provider, "cutoff", cutoff);
        ClusterColumn.BindScope(cmd, _clusterName);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateStatusAsync(string entryId, string status, DateTimeOffset? replayedAt, CancellationToken ct)
    {
        var provider = Provider;
        var connStr = DlqStorage.ResolveConnectionString(_configuration, provider)!;
        await using var conn = DlqStorage.CreateConnection(provider, connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = DlqStorage.UpdateStatusSql(provider);
        AddParam(cmd, "status", status);
        AddDateParam(cmd, provider, "replayed_at", replayedAt);
        AddParam(cmd, "entry_id", entryId);
        ClusterColumn.BindScope(cmd, _clusterName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── mapping / helpers ────────────────────────────────────────────

    private sealed record DlqRow(
        string EntryId, string ContextName, string RouteId, string MarkerName,
        string BodyKind, string? BodyType, string? BodyData, string HeadersJson, string PropertiesJson, bool Replayable,
        string Status, string? ClusterName);

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
        GetBool(r, "replayable"), GetString(r, "status") ?? "pending", GetString(r, "cluster_name"));

    /// <summary>
    /// Binds the filter and paging parameters of <see cref="DlqStorage.SelectSql"/> with explicit types
    /// (see <see cref="DbParameterBinding"/> for why a typed null matters on PostgreSQL); the cluster scope
    /// is bound next to it by the caller. Internal so the bound shape can be checked without a database.
    /// </summary>
    internal static void BindQueryParameters(
        DbCommand cmd, AuditProvider provider,
        string? context, string? route, string? status,
        DateTimeOffset? since, DateTimeOffset? until,
        int limit, int offset)
    {
        DbParameterBinding.AddString(cmd, "context", context);
        DbParameterBinding.AddString(cmd, "route", route);
        DbParameterBinding.AddString(cmd, "status", status);
        DbParameterBinding.AddTimestamp(cmd, provider, "since", since);
        DbParameterBinding.AddTimestamp(cmd, provider, "until", until);
        DbParameterBinding.AddInt32(cmd, "limit", limit);
        DbParameterBinding.AddInt32(cmd, "offset", offset);
    }

    /// <summary>
    /// Typed binding by CLR type. A null string is a typed null: an untyped <see cref="DBNull"/> reaches
    /// PostgreSQL as <c>unknown</c> and breaks the <c>(@x IS NULL OR col = @x)</c> shape with 42P08.
    /// </summary>
    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        switch (value)
        {
            case null:
            case string:
                DbParameterBinding.AddString(cmd, name, (string?)value);
                return;
            case bool b:
                DbParameterBinding.AddBoolean(cmd, name, b);
                return;
            case int i:
                DbParameterBinding.AddInt32(cmd, name, i);
                return;
            default:
                throw new ArgumentException(
                    $"No typed binding for parameter '{name}' of type {value.GetType().Name}.", nameof(value));
        }
    }

    /// <summary>Timestamp in the column's own type per provider; see <see cref="DbParameterBinding.AddTimestamp"/>.</summary>
    private static void AddDateParam(DbCommand cmd, AuditProvider provider, string name, DateTimeOffset? value)
        => DbParameterBinding.AddTimestamp(cmd, provider, name, value);

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
