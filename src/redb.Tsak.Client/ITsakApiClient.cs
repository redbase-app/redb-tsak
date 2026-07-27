using redb.Tsak.Contracts;

namespace redb.Tsak.Client;

/// <summary>
/// Contract for the Tsak runtime REST API client.
/// All methods throw <see cref="ApiException"/> on non-success responses.
/// </summary>
public interface ITsakApiClient : IDisposable
{
    // ── Auth ──────────────────────────────────────────────────────────

    /// <summary>Create a new API key.</summary>
    /// <param name="name">Key name.</param>
    /// <param name="roles">Roles to assign.</param>
    /// <param name="userId">Optional user ID.</param>
    /// <param name="expiresAt">Optional expiration timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ApiKeyCreatedResponse> CreateApiKeyAsync(string name, string[]? roles = null, string? userId = null, DateTime? expiresAt = null, CancellationToken ct = default);

    /// <summary>List all API keys.</summary>
    Task<ApiKeyInfo[]> ListApiKeysAsync(CancellationToken ct = default);

    /// <summary>Revoke an API key by ID.</summary>
    Task<ApiKeyRevokedResponse> RevokeApiKeyAsync(string id, CancellationToken ct = default);

    // ── Cluster ──────────────────────────────────────────────────────

    /// <summary>Get cluster status.</summary>
    Task<ClusterStatusResponse> GetClusterStatusAsync(CancellationToken ct = default);

    /// <summary>List cluster nodes.</summary>
    Task<ClusterNodesResponse> ListClusterNodesAsync(CancellationToken ct = default);

    /// <summary>Trigger cluster rebalance.</summary>
    Task<ClusterRebalanceResponse> RebalanceClusterAsync(CancellationToken ct = default);

    /// <summary>Remove a dead/offline cluster node.</summary>
    Task<ClusterNodeRemovedResponse> RemoveClusterNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>Cordon a node (take on no new work; drain to peers) for planned maintenance.</summary>
    Task<ClusterCordonResponse> CordonNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>Uncordon a node — it starts taking on work again.</summary>
    Task<ClusterCordonResponse> UncordonNodeAsync(string nodeId, CancellationToken ct = default);

    // ── Contexts ─────────────────────────────────────────────────────

    /// <summary>List all route contexts.</summary>
    Task<ContextSummary[]> ListContextsAsync(CancellationToken ct = default);

    /// <summary>Get details of a specific context.</summary>
    Task<ContextDetail> GetContextAsync(string name, CancellationToken ct = default);

    /// <summary>Start a context.</summary>
    Task<ContextActionResponse> StartContextAsync(string name, CancellationToken ct = default);

    /// <summary>Stop a context.</summary>
    Task<ContextActionResponse> StopContextAsync(string name, CancellationToken ct = default);

    /// <summary>Restart a context.</summary>
    Task<ContextActionResponse> RestartContextAsync(string name, CancellationToken ct = default);

    /// <summary>Reset persisted route states and restart a context.</summary>
    Task<ContextActionResponse> ResetRouteStatesAsync(string name, CancellationToken ct = default);

    /// <summary>Remove a context.</summary>
    Task<ContextActionResponse> RemoveContextAsync(string name, CancellationToken ct = default);

    // ── Routes ───────────────────────────────────────────────────────

    /// <summary>List all routes in a context.</summary>
    Task<TsakRouteInfo[]> ListRoutesAsync(string contextName, CancellationToken ct = default);

    /// <summary>Get detailed info for a specific route.</summary>
    Task<RouteDetail> GetRouteAsync(string contextName, string routeId, CancellationToken ct = default);

    /// <summary>List endpoints for a context.</summary>
    Task<TsakEndpointInfo[]> ListEndpointsAsync(string contextName, CancellationToken ct = default);

    /// <summary>Start a route.</summary>
    Task<RouteActionResponse> StartRouteAsync(string contextName, string routeId, CancellationToken ct = default);

    /// <summary>Stop a route with optional timeout.</summary>
    Task<RouteActionResponse> StopRouteAsync(string contextName, string routeId, int? timeoutSeconds = null, CancellationToken ct = default);

    /// <summary>Browse in-flight exchanges for a specific route.</summary>
    Task<InflightExchangeDto[]> GetRouteInflightAsync(string contextName, string routeId, CancellationToken ct = default);

    /// <summary>Browse all in-flight exchanges across all routes in a context.</summary>
    Task<InflightExchangeDto[]> GetContextInflightAsync(string contextName, CancellationToken ct = default);

    // ── Watchdog ─────────────────────────────────────────────────────

    /// <summary>Get watchdog status and state.</summary>
    Task<WatchdogState> GetWatchdogStatusAsync(CancellationToken ct = default);

    /// <summary>Get active watchdog alerts.</summary>
    Task<WatchdogAlert[]> GetWatchdogAlertsAsync(CancellationToken ct = default);

    /// <summary>Enable watchdog monitoring.</summary>
    Task EnableWatchdogAsync(CancellationToken ct = default);

    /// <summary>Disable watchdog monitoring.</summary>
    Task DisableWatchdogAsync(CancellationToken ct = default);

    /// <summary>Alert-delivery status: whether delivery is active and which channels are enabled.</summary>
    Task<AlertDeliveryStatus> GetAlertStatusAsync(CancellationToken ct = default);

    /// <summary>Send a synthetic alert through every enabled channel; returns the per-channel outcome.</summary>
    Task<AlertTestResult> TestAlertAsync(CancellationToken ct = default);

    // ── Lifecycle ────────────────────────────────────────────────────

    /// <summary>Get lifecycle events with optional filters.</summary>
    Task<LifecycleEvent[]> GetLifecycleEventsAsync(
        string? context = null, string? route = null,
        string? type = null, DateTime? since = null,
        int? limit = null, CancellationToken ct = default);

    // ── Metrics & Dashboard ──────────────────────────────────────────

    /// <summary>Get per-route metrics summary.</summary>
    Task<RouteMetricsSummary> GetRouteMetricsAsync(string contextName, string routeId, CancellationToken ct = default);

    /// <summary>Get aggregated dashboard snapshot.</summary>
    Task<DashboardSnapshot> GetDashboardAsync(CancellationToken ct = default);

    // ── Force-Stop & Diagnostics ─────────────────────────────────────

    /// <summary>Force-stop a route immediately (no drain).</summary>
    Task<RouteActionResponse> ForceStopRouteAsync(string contextName, string routeId, CancellationToken ct = default);

    /// <summary>Get full system diagnostic dump.</summary>
    Task<DiagnosticDump> GetDiagnosticDumpAsync(CancellationToken ct = default);

    /// <summary>Get diagnostic dump for a specific route.</summary>
    Task<RouteDiagnosticDump> GetRouteDiagnosticsAsync(string contextName, string routeId, CancellationToken ct = default);

    // ── Logs ─────────────────────────────────────────────────────────

    /// <summary>Get buffered log entries. Supports incremental fetch via afterId.</summary>
    Task<LogsResponse> GetLogsAsync(long? afterId = null, int? limit = null, string? level = null, CancellationToken ct = default);

    /// <summary>List available log files on the worker node.</summary>
    Task<LogFilesResponse> GetLogFilesAsync(CancellationToken ct = default);

    /// <summary>Download a log file as ZIP bytes.</summary>
    Task<byte[]> DownloadLogFileAsync(string filename, CancellationToken ct = default);

    // ── Audit ────────────────────────────────────────────────────────

    /// <summary>
    /// Query the persistent admin-action audit trail, newest-first. All filters are optional;
    /// use <paramref name="limit"/>/<paramref name="offset"/> to page (limit is clamped to 1..1000
    /// server-side). Returns <c>Available = false</c> when the node runs without a database.
    /// </summary>
    Task<AuditQueryResult> GetAuditAsync(
        string? actor = null,
        string? action = null,
        string? target = null,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        int? limit = null,
        int? offset = null,
        CancellationToken ct = default);

    // ── Modules ──────────────────────────────────────────────────────

    /// <summary>List all modules.</summary>
    Task<ModuleInfo[]> ListModulesAsync(CancellationToken ct = default);

    /// <summary>Get details of a specific module.</summary>
    Task<ModuleInfo> GetModuleAsync(string name, CancellationToken ct = default);

    /// <summary>Remove a module.</summary>
    Task<ModuleRemovedResponse> RemoveModuleAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Upload a <c>.tpkg</c> package for deployment. <paramref name="signatureBase64"/> is the
    /// detached signature (sent as the <c>X-Tsak-Signature</c> header); pass null when unsigned.
    /// Requires the node to have upload enabled and the caller to hold the <c>admin</c> role.
    /// </summary>
    Task<ModuleDeployResponse> UploadModuleAsync(byte[] packageBytes, string? signatureBase64 = null, CancellationToken ct = default);

    /// <summary>Dry-run validate a `.tpkg` without installing it.</summary>
    Task<ModuleDeployResponse> ValidateModuleAsync(byte[] packageBytes, string? signatureBase64 = null, CancellationToken ct = default);

    /// <summary>Roll a module back to its previous on-disk version.</summary>
    Task<ModuleDeployResponse> RollbackModuleAsync(string name, CancellationToken ct = default);

    // ── Dead-letter queue ────────────────────────────────────────────

    /// <summary>Query dead-lettered exchanges (failed at a route checkpoint), newest-first, paged.</summary>
    Task<FailedExchangeQueryResult> GetFailedExchangesAsync(
        string? context = null, string? route = null, string? status = null,
        DateTimeOffset? since = null, DateTimeOffset? until = null,
        int? limit = null, int? offset = null, CancellationToken ct = default);

    /// <summary>Replay a dead-lettered exchange: re-run its route tail from the captured snapshot.</summary>
    Task<ExchangeReplayResult> ReplayExchangeAsync(string entryId, CancellationToken ct = default);

    /// <summary>Discard a dead-lettered exchange.</summary>
    Task<ExchangeReplayResult> DiscardExchangeAsync(string entryId, CancellationToken ct = default);

    // ── Scheduler ────────────────────────────────────────────────────

    /// <summary>Get scheduler status.</summary>
    Task<SchedulerStatusResponse> GetSchedulerStatusAsync(CancellationToken ct = default);

    /// <summary>List scheduled jobs.</summary>
    Task<ScheduledJobsResponse> ListScheduledJobsAsync(CancellationToken ct = default);

    /// <summary>List currently running jobs.</summary>
    Task<RunningJobsResponse> ListRunningJobsAsync(CancellationToken ct = default);

    /// <summary>Start the scheduler.</summary>
    Task<SchedulerActionResponse> StartSchedulerAsync(CancellationToken ct = default);

    /// <summary>Put scheduler in standby mode.</summary>
    Task<SchedulerActionResponse> StandbySchedulerAsync(CancellationToken ct = default);

    /// <summary>Pause a specific job.</summary>
    Task<SchedulerActionResponse> PauseJobAsync(string jobKey, CancellationToken ct = default);

    /// <summary>Resume a specific job.</summary>
    Task<SchedulerActionResponse> ResumeJobAsync(string jobKey, CancellationToken ct = default);

    /// <summary>Fire a scheduled job immediately (out of schedule).</summary>
    Task<SchedulerActionResponse> FireJobAsync(string jobKey, CancellationToken ct = default);

    // ── System ───────────────────────────────────────────────────────

    /// <summary>Get system health status.</summary>
    Task<HealthCheckResult> GetHealthAsync(CancellationToken ct = default);

    /// <summary>Get system metrics.</summary>
    Task<MetricsResponse> GetMetricsAsync(CancellationToken ct = default);

    /// <summary>Get metrics history over a time window.</summary>
    Task<MetricsHistoryResponse> GetMetricsHistoryAsync(int minutes = 5, CancellationToken ct = default);

    /// <summary>Get system information.</summary>
    Task<SystemInfoResponse> GetInfoAsync(CancellationToken ct = default);

    /// <summary>Get the effective (merged, redacted) configuration of this node (admin).</summary>
    Task<EffectiveConfigResult> GetConfigAsync(CancellationToken ct = default);

    // ── Users ────────────────────────────────────────────────────────

    /// <summary>List all users.</summary>
    Task<TsakUserInfo[]> ListUsersAsync(CancellationToken ct = default);

    /// <summary>Get user by login.</summary>
    Task<TsakUserInfo> GetUserAsync(string login, CancellationToken ct = default);

    /// <summary>Create a new user.</summary>
    Task<TsakUserInfo> CreateUserAsync(TsakCreateUserRequest request, CancellationToken ct = default);

    /// <summary>Update an existing user.</summary>
    Task<TsakUserInfo> UpdateUserAsync(string login, TsakUpdateUserRequest request, CancellationToken ct = default);

    /// <summary>Delete a user.</summary>
    Task<TsakUserActionResponse> DeleteUserAsync(string login, CancellationToken ct = default);
}
