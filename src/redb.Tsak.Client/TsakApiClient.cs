using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using redb.Tsak.Contracts;

namespace redb.Tsak.Client;

/// <summary>
/// HTTP implementation of the Tsak runtime API client.
/// </summary>
public sealed class TsakApiClient : ITsakApiClient
{
    /// <summary>Default request timeout used when neither the constructor nor a per-call <see cref="CancellationToken"/> overrides it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Initializes a new Tsak API client.
    /// </summary>
    /// <param name="baseUrl">Base URL of the Tsak runtime (e.g. http://localhost:5100).</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="timeout">
    /// Optional default <see cref="HttpClient.Timeout"/> for all requests. Defaults to <see cref="DefaultTimeout"/>
    /// (5s) — backwards-compatible with the previous behavior. For long-running operations
    /// (diagnostics dump, force-stop, module install) pass a larger value here, or supply a
    /// per-call <see cref="CancellationToken"/> with the desired deadline.
    /// </param>
    public TsakApiClient(string baseUrl, string? apiKey = null, TimeSpan? timeout = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = timeout ?? DefaultTimeout
        };
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    }

    public void Dispose() => _http.Dispose();

    // ── Auth ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ApiKeyCreatedResponse> CreateApiKeyAsync(
        string name, string[]? roles = null, string? userId = null,
        DateTime? expiresAt = null, CancellationToken ct = default)
    {
        var body = new { Name = name, Roles = roles is not null ? string.Join(",", roles) : null, UserId = userId, ExpiresAt = expiresAt };
        var response = await _http.PostAsJsonAsync("api/auth/keys", body, ct);
        return await ReadAsync<ApiKeyCreatedResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ApiKeyInfo[]> ListApiKeysAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/auth/keys", ct);
        return await ReadAsync<ApiKeyInfo[]>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ApiKeyRevokedResponse> RevokeApiKeyAsync(string id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/auth/keys/{Uri.EscapeDataString(id)}", ct);
        return await ReadAsync<ApiKeyRevokedResponse>(response, ct);
    }

    // ── Cluster ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ClusterStatusResponse> GetClusterStatusAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/cluster", ct);
        return await ReadAsync<ClusterStatusResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ClusterNodesResponse> ListClusterNodesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/cluster/nodes", ct);
        return await ReadAsync<ClusterNodesResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ClusterRebalanceResponse> RebalanceClusterAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("api/cluster/rebalance", null, ct);
        return await ReadAsync<ClusterRebalanceResponse>(response, ct);
    }

    public async Task<ClusterNodeRemovedResponse> RemoveClusterNodeAsync(string nodeId, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/cluster/nodes/{Uri.EscapeDataString(nodeId)}", ct);
        return await ReadAsync<ClusterNodeRemovedResponse>(response, ct);
    }

    // ── Contexts ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ContextSummary[]> ListContextsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/contexts", ct);
        return await ReadAsync<ContextSummary[]>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ContextDetail> GetContextAsync(string name, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/contexts/{Uri.EscapeDataString(name)}", ct);
        return await ReadAsync<ContextDetail>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ContextActionResponse> StartContextAsync(string name, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"api/contexts/{Uri.EscapeDataString(name)}/start", null, ct);
        return await ReadAsync<ContextActionResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ContextActionResponse> StopContextAsync(string name, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"api/contexts/{Uri.EscapeDataString(name)}/stop", null, ct);
        return await ReadAsync<ContextActionResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ContextActionResponse> RestartContextAsync(string name, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"api/contexts/{Uri.EscapeDataString(name)}/restart", null, ct);
        return await ReadAsync<ContextActionResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ContextActionResponse> ResetRouteStatesAsync(string name, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"api/contexts/{Uri.EscapeDataString(name)}/reset-route-states", null, ct);
        return await ReadAsync<ContextActionResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ContextActionResponse> RemoveContextAsync(string name, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/contexts/{Uri.EscapeDataString(name)}", ct);
        return await ReadAsync<ContextActionResponse>(response, ct);
    }

    // ── Routes ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TsakRouteInfo[]> ListRoutesAsync(string contextName, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/contexts/{Uri.EscapeDataString(contextName)}/routes", ct);
        return await ReadAsync<TsakRouteInfo[]>(response, ct);
    }

    /// <inheritdoc />
    public async Task<RouteDetail> GetRouteAsync(string contextName, string routeId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/contexts/{Uri.EscapeDataString(contextName)}/routes/{Uri.EscapeDataString(routeId)}", ct);
        return await ReadAsync<RouteDetail>(response, ct);
    }

    /// <inheritdoc />
    public async Task<TsakEndpointInfo[]> ListEndpointsAsync(string contextName, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/contexts/{Uri.EscapeDataString(contextName)}/endpoints", ct);
        return await ReadAsync<TsakEndpointInfo[]>(response, ct);
    }

    /// <inheritdoc />
    public async Task<RouteActionResponse> StartRouteAsync(string contextName, string routeId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"api/contexts/{Uri.EscapeDataString(contextName)}/routes/{Uri.EscapeDataString(routeId)}/start", null, ct);
        return await ReadAsync<RouteActionResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<RouteActionResponse> StopRouteAsync(string contextName, string routeId, int? timeoutSeconds = null, CancellationToken ct = default)
    {
        var query = timeoutSeconds.HasValue ? $"?timeoutSeconds={timeoutSeconds.Value}" : "";
        var response = await _http.PostAsync($"api/contexts/{Uri.EscapeDataString(contextName)}/routes/{Uri.EscapeDataString(routeId)}/stop{query}", null, ct);
        return await ReadAsync<RouteActionResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<InflightExchangeDto[]> GetRouteInflightAsync(string contextName, string routeId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/contexts/{Uri.EscapeDataString(contextName)}/routes/{Uri.EscapeDataString(routeId)}/inflight", ct);
        return await ReadAsync<InflightExchangeDto[]>(response, ct);
    }

    /// <inheritdoc />
    public async Task<InflightExchangeDto[]> GetContextInflightAsync(string contextName, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/contexts/{Uri.EscapeDataString(contextName)}/inflight", ct);
        return await ReadAsync<InflightExchangeDto[]>(response, ct);
    }

    // ── Watchdog ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<WatchdogState> GetWatchdogStatusAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/watchdog/status", ct);
        return await ReadAsync<WatchdogState>(response, ct);
    }

    /// <inheritdoc />
    public async Task<WatchdogAlert[]> GetWatchdogAlertsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/watchdog/alerts", ct);
        return await ReadAsync<WatchdogAlert[]>(response, ct);
    }

    /// <inheritdoc />
    public async Task EnableWatchdogAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("api/watchdog/enable", null, ct);
        if (!response.IsSuccessStatusCode)
            await ReadAsync<object>(response, ct); // triggers ApiException
    }

    /// <inheritdoc />
    public async Task DisableWatchdogAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("api/watchdog/disable", null, ct);
        if (!response.IsSuccessStatusCode)
            await ReadAsync<object>(response, ct);
    }

    // ── Lifecycle ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<LifecycleEvent[]> GetLifecycleEventsAsync(
        string? context = null, string? route = null,
        string? type = null, DateTime? since = null,
        int? limit = null, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("context", context),
            ("route", route),
            ("type", type),
            ("since", since?.ToString("O")),
            ("limit", limit?.ToString()));
        var response = await _http.GetAsync($"api/lifecycle{query}", ct);
        return await ReadAsync<LifecycleEvent[]>(response, ct);
    }

    // ── Metrics & Dashboard ──────────────────────────────────────────

    /// <inheritdoc />
    public async Task<RouteMetricsSummary> GetRouteMetricsAsync(string contextName, string routeId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"api/contexts/{Uri.EscapeDataString(contextName)}/routes/{Uri.EscapeDataString(routeId)}/metrics", ct);
        return await ReadAsync<RouteMetricsSummary>(response, ct);
    }

    /// <inheritdoc />
    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/dashboard", ct);
        return await ReadAsync<DashboardSnapshot>(response, ct);
    }

    // ── Force-Stop & Diagnostics ─────────────────────────────────────

    /// <inheritdoc />
    public async Task<RouteActionResponse> ForceStopRouteAsync(string contextName, string routeId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"api/contexts/{Uri.EscapeDataString(contextName)}/routes/{Uri.EscapeDataString(routeId)}/force-stop", null, ct);
        return await ReadAsync<RouteActionResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<DiagnosticDump> GetDiagnosticDumpAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/diagnostics/dump", ct);
        return await ReadAsync<DiagnosticDump>(response, ct);
    }

    /// <inheritdoc />
    public async Task<RouteDiagnosticDump> GetRouteDiagnosticsAsync(string contextName, string routeId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"api/diagnostics/{Uri.EscapeDataString(contextName)}/{Uri.EscapeDataString(routeId)}", ct);
        return await ReadAsync<RouteDiagnosticDump>(response, ct);
    }

    // ── Logs ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<LogsResponse> GetLogsAsync(long? afterId = null, int? limit = null, string? level = null, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("afterId", afterId?.ToString()),
            ("limit", limit?.ToString()),
            ("level", level));
        var response = await _http.GetAsync($"api/logs{query}", ct);
        return await ReadAsync<LogsResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<LogFilesResponse> GetLogFilesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/logs/files", ct);
        return await ReadAsync<LogFilesResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<byte[]> DownloadLogFileAsync(string filename, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"api/logs/files/{Uri.EscapeDataString(filename)}", ct);
        if (!response.IsSuccessStatusCode)
            throw ApiException.Create((int)response.StatusCode, $"Failed to download log file '{filename}'");
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // ── Modules ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ModuleInfo[]> ListModulesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/modules", ct);
        return await ReadAsync<ModuleInfo[]>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ModuleInfo> GetModuleAsync(string name, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/modules/{Uri.EscapeDataString(name)}", ct);
        return await ReadAsync<ModuleInfo>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ModuleRemovedResponse> RemoveModuleAsync(string name, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/modules/{Uri.EscapeDataString(name)}", ct);
        return await ReadAsync<ModuleRemovedResponse>(response, ct);
    }

    // ── Scheduler ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<SchedulerStatusResponse> GetSchedulerStatusAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/scheduler/status", ct);
        return await ReadAsync<SchedulerStatusResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ScheduledJobsResponse> ListScheduledJobsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/scheduler/scheduled", ct);
        return await ReadAsync<ScheduledJobsResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<RunningJobsResponse> ListRunningJobsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/scheduler/jobs", ct);
        return await ReadAsync<RunningJobsResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<SchedulerActionResponse> StartSchedulerAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("api/scheduler/start", null, ct);
        return await ReadAsync<SchedulerActionResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<SchedulerActionResponse> StandbySchedulerAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("api/scheduler/standby", null, ct);
        return await ReadAsync<SchedulerActionResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<SchedulerActionResponse> PauseJobAsync(string jobKey, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"api/scheduler/pause-job?key={Uri.EscapeDataString(jobKey)}", null, ct);
        return await ReadAsync<SchedulerActionResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<SchedulerActionResponse> ResumeJobAsync(string jobKey, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"api/scheduler/resume-job?key={Uri.EscapeDataString(jobKey)}", null, ct);
        return await ReadAsync<SchedulerActionResponse>(response, ct);
    }

    // ── System ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<HealthCheckResult> GetHealthAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/system/health", ct);
        return await ReadAsync<HealthCheckResult>(response, ct);
    }

    /// <inheritdoc />
    public async Task<MetricsResponse> GetMetricsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/system/metrics", ct);
        return await ReadAsync<MetricsResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<MetricsHistoryResponse> GetMetricsHistoryAsync(int minutes = 5, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/system/metrics/history?minutes={minutes}", ct);
        return await ReadAsync<MetricsHistoryResponse>(response, ct);
    }

    /// <inheritdoc />
    public async Task<SystemInfoResponse> GetInfoAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/system/info", ct);
        return await ReadAsync<SystemInfoResponse>(response, ct);
    }

    // ── Users ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TsakUserInfo[]> ListUsersAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/users", ct);
        return await ReadAsync<TsakUserInfo[]>(response, ct);
    }

    /// <inheritdoc />
    public async Task<TsakUserInfo> GetUserAsync(string login, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/users/{Uri.EscapeDataString(login)}", ct);
        return await ReadAsync<TsakUserInfo>(response, ct);
    }

    /// <inheritdoc />
    public async Task<TsakUserInfo> CreateUserAsync(TsakCreateUserRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("api/users", request, ct);
        return await ReadAsync<TsakUserInfo>(response, ct);
    }

    /// <inheritdoc />
    public async Task<TsakUserInfo> UpdateUserAsync(string login, TsakUpdateUserRequest request, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"api/users/{Uri.EscapeDataString(login)}", request, ct);
        return await ReadAsync<TsakUserInfo>(response, ct);
    }

    /// <inheritdoc />
    public async Task<TsakUserActionResponse> DeleteUserAsync(string login, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/users/{Uri.EscapeDataString(login)}", ct);
        return await ReadAsync<TsakUserActionResponse>(response, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads and deserializes the response, throwing <see cref="ApiException"/> on non-success status.
    /// </summary>
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            ApiErrorResponse? errorBody = null;
            try
            {
                errorBody = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions, ct);
            }
            catch
            {
                // Response body is not a valid ApiErrorResponse — that's fine
            }

            var message = errorBody?.Message
                          ?? $"API request failed with status {(int)response.StatusCode} {response.StatusCode}";
            throw ApiException.Create((int)response.StatusCode, message, errorBody);
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
               ?? throw ApiException.Create((int)response.StatusCode, "API returned null response body");
    }

    /// <summary>
    /// Builds a query string from name/value pairs, skipping null values.
    /// </summary>
    private static string BuildQuery(params (string name, string? value)[] parameters)
    {
        var pairs = parameters
            .Where(p => p.value is not null)
            .Select(p => $"{Uri.EscapeDataString(p.name)}={Uri.EscapeDataString(p.value!)}");

        var query = string.Join("&", pairs);
        return query.Length > 0 ? "?" + query : string.Empty;
    }
}
