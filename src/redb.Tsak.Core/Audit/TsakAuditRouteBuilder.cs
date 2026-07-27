using redb.Route.Core;
using redb.Route.Sql;

namespace redb.Tsak.Core.Audit;

/// <summary>
/// Audit writer: <c>direct://tsak-audit</c> → INSERT into <c>tsak_audit_log</c>.
/// Values arrive as <c>audit.*</c> headers, the sanitized argument JSON as the body.
/// <para>
/// The sink is an endpoint on purpose: pointing the same event stream at a file, a broker or
/// an HTTP collector is a configuration change, not a code change.
/// </para>
/// </summary>
public sealed class TsakAuditRouteBuilder : RouteBuilder
{
    /// <summary>Route id, so the writer can be found in the Routes API and the dashboard.</summary>
    public const string RouteIdName = "tsak-audit-writer";

    private readonly AuditProvider _provider;

    public TsakAuditRouteBuilder(AuditProvider provider)
    {
        _provider = provider;
    }

    protected override void Configure()
    {
        From(AuditStorage.AuditEndpoint)
            .RouteId(RouteIdName)
            .To(Sql.Execute(AuditStorage.InsertSql(_provider))
                .DataSource(Constant(AuditStorage.DataSourceName))
                .Param("event_id",          Header(AuditHeaders.EventId))
                .Param("ts",                Header(AuditHeaders.Timestamp))
                .Param("action",            Header(AuditHeaders.Action))
                .Param("controller_type",   Header(AuditHeaders.ControllerType))
                .Param("actor_principal",   Header(AuditHeaders.ActorPrincipal))
                .Param("actor_key_id",      Header(AuditHeaders.ActorKeyId))
                .Param("remote_ip",         Header(AuditHeaders.RemoteIp))
                .Param("user_agent",        Header(AuditHeaders.UserAgent))
                .Param("http_method",       Header(AuditHeaders.HttpMethod))
                .Param("request_path",      Header(AuditHeaders.RequestPath))
                .Param("target_resource",   Header(AuditHeaders.TargetResource))
                .Param("status_code",       Header(AuditHeaders.StatusCode))
                .Param("duration_ms",       Header(AuditHeaders.DurationMs))
                .Param("exception_type",    Header(AuditHeaders.ExceptionType))
                .Param("exception_message", Header(AuditHeaders.ExceptionMessage))
                .Param("payload",           Body()));
    }
}

/// <summary>Header names carrying a single audit event from the sink to the writer route.</summary>
public static class AuditHeaders
{
    public const string EventId = "audit.eventId";
    public const string Timestamp = "audit.ts";
    public const string Action = "audit.action";
    public const string ControllerType = "audit.controllerType";
    public const string ActorPrincipal = "audit.actorPrincipal";
    public const string ActorKeyId = "audit.actorKeyId";
    public const string RemoteIp = "audit.remoteIp";
    public const string UserAgent = "audit.userAgent";
    public const string HttpMethod = "audit.httpMethod";
    public const string RequestPath = "audit.requestPath";
    public const string TargetResource = "audit.targetResource";
    public const string StatusCode = "audit.statusCode";
    public const string DurationMs = "audit.durationMs";
    public const string ExceptionType = "audit.exceptionType";
    public const string ExceptionMessage = "audit.exceptionMessage";
}
