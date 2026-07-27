using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Fallback <see cref="IAdminAuditService"/> used when Tsak runs without a database
/// (<c>Tsak:Storage:Type=InMemory</c> / no <c>Tsak:Redb:Provider</c>) or when audit
/// persistence is switched off.
/// <para>
/// The event is written as a single line: the literal anchor <c>[tsak-audit]</c> followed by
/// one JSON object. The anchor sits at the start of the message so a standalone deployment can
/// grep the line and parse everything after the anchor with any JSON reader — no log-format
/// parsing, no multi-line handling.
/// </para>
/// <para>
/// When a provider IS configured, <see cref="RouteAdminAuditService"/> takes over and writes to
/// <c>tsak_audit_log</c>; this sink then only receives events the database rejected.
/// </para>
/// </summary>
public sealed class LogAdminAuditService : IAdminAuditService
{
    /// <summary>Line prefix that makes audit events greppable in a plain log file.</summary>
    public const string Anchor = "[tsak-audit]";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly ILogger<LogAdminAuditService> _logger;

    public LogAdminAuditService(ILogger<LogAdminAuditService> logger)
    {
        _logger = logger;
    }

    public Task RecordAsync(AdminAuditEvent @event, CancellationToken ct)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(new AuditLogLine(@event), JsonOptions);
        }
        catch (Exception ex)
        {
            // Serialization must never cost us the event — fall back to a flat line.
            _logger.LogWarning(ex, "{Anchor} failed to serialize audit event {Action}", Anchor, @event.Action);
            return Task.CompletedTask;
        }

        // WRN so events stand out against an INF baseline. The anchor is a literal, not a
        // placeholder, so it is identical in both the rendered line and structured sinks.
        _logger.LogWarning("[tsak-audit] {AuditJson}", json);
        return Task.CompletedTask;
    }

    /// <summary>Wire shape of the JSON that follows the anchor — camelCase, nulls omitted.</summary>
    private sealed record AuditLogLine
    {
        public AuditLogLine(AdminAuditEvent e)
        {
            Ts = e.Timestamp;
            Action = e.Action;
            Controller = e.ControllerType;
            Actor = e.ActorPrincipalName;
            KeyId = e.ActorApiKeyId;
            Ip = e.RemoteIp;
            UserAgent = e.UserAgent;
            Method = e.HttpMethod;
            Path = e.RequestPath;
            Target = e.TargetResource;
            Status = e.StatusCode;
            DurationMs = Math.Round(e.DurationMs, 1);
            ExceptionType = e.ExceptionType;
            ExceptionMessage = e.ExceptionMessage;
            Payload = e.Payload;
        }

        [JsonPropertyName("ts")] public DateTimeOffset Ts { get; }
        [JsonPropertyName("action")] public string Action { get; }
        [JsonPropertyName("controller")] public string? Controller { get; }
        [JsonPropertyName("actor")] public string? Actor { get; }
        [JsonPropertyName("keyId")] public string? KeyId { get; }
        [JsonPropertyName("ip")] public string? Ip { get; }
        [JsonPropertyName("userAgent")] public string? UserAgent { get; }
        [JsonPropertyName("method")] public string? Method { get; }
        [JsonPropertyName("path")] public string? Path { get; }
        [JsonPropertyName("target")] public string? Target { get; }
        [JsonPropertyName("status")] public int Status { get; }
        [JsonPropertyName("durationMs")] public double DurationMs { get; }
        [JsonPropertyName("exceptionType")] public string? ExceptionType { get; }
        [JsonPropertyName("exceptionMessage")] public string? ExceptionMessage { get; }
        [JsonPropertyName("payload")] public string? Payload { get; }
    }
}
