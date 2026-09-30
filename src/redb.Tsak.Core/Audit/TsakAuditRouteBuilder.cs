using redb.Route.Abstractions;
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
/// <para>
/// The endpoint names the statement instead of carrying it. The INSERT is a named query the writer
/// publishes on its own context, and its values travel as a map keyed by column, which the sql
/// connector binds to <c>:#column</c> by itself. Before, the INSERT text and seventeen <c>param.*</c>
/// options were the endpoint URI — printed whole in every start log and on the Endpoints page.
/// Whoever mounts the writer still publishes only the data source, as before.
/// </para>
/// </summary>
public sealed class TsakAuditRouteBuilder : RouteBuilder
{
    /// <summary>Route id, so the writer can be found in the Routes API and the dashboard.</summary>
    public const string RouteIdName = "tsak-audit-writer";

    /// <summary>Name the INSERT is published under as a named query of the writer's context.</summary>
    public const string InsertQueryName = "tsak-audit-insert";

    private readonly AuditProvider _provider;
    private readonly string _clusterName;

    /// <param name="provider">Database provider of the audit table.</param>
    /// <param name="clusterName">Cluster the written events belong to (<c>Tsak:Cluster:ClusterName</c>);
    /// the default cluster name when omitted.</param>
    public TsakAuditRouteBuilder(AuditProvider provider, string? clusterName = null)
    {
        _provider = provider;
        _clusterName = string.IsNullOrWhiteSpace(clusterName)
            ? Services.Storage.TsakStorageScope.DefaultClusterName
            : clusterName;
    }

    protected override void Configure()
    {
        if (Context is not null)
            PublishInsertQuery(Context, AuditStorage.InsertSql(_provider));

        var clusterName = _clusterName;
        From(AuditStorage.AuditEndpoint)
            .RouteId(RouteIdName)
            .Process(exchange => exchange.In.Body = ToColumnValues(exchange.In, clusterName))
            .To(Sql.Execute(SqlNamedQueryRegistry.RefPrefix + InsertQueryName)
                .DataSource(Constant(AuditStorage.DataSourceName)));
    }

    /// <summary>
    /// Publishes the INSERT on the context. A builder is configured again when its context is rebuilt, so the
    /// same statement under the same name is already there and nothing is done; a different statement under
    /// the name is two writers disagreeing about one table, and that is refused.
    /// </summary>
    private static void PublishInsertQuery(IRouteContext context, string insertSql)
    {
        var namedQueries = context.GetService<ISqlNamedQueryRegistry>();
        if (namedQueries is null)
        {
            namedQueries = new SqlNamedQueryRegistry();
            context.AddService(typeof(ISqlNamedQueryRegistry), namedQueries);
        }

        if (!namedQueries.TryResolve(InsertQueryName, out var published))
        {
            namedQueries.Register(InsertQueryName, insertSql);
            return;
        }

        if (!string.Equals(published, insertSql, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Named query '{InsertQueryName}' is already published on this context with a different statement. "
                + "Two audit writers with different providers cannot share one context.");
    }

    /// <summary>
    /// The row to insert, keyed by column. An absent header is a present key with a null value, which the sql
    /// connector binds as NULL — exactly what the per-column header expressions did.
    /// </summary>
    internal static Dictionary<string, object?> ToColumnValues(IMessage message, string clusterName) => new()
    {
        ["event_id"] = HeaderOrNull(message, AuditHeaders.EventId),
        ["ts"] = HeaderOrNull(message, AuditHeaders.Timestamp),
        ["action"] = HeaderOrNull(message, AuditHeaders.Action),
        ["controller_type"] = HeaderOrNull(message, AuditHeaders.ControllerType),
        ["actor_principal"] = HeaderOrNull(message, AuditHeaders.ActorPrincipal),
        ["actor_key_id"] = HeaderOrNull(message, AuditHeaders.ActorKeyId),
        ["remote_ip"] = HeaderOrNull(message, AuditHeaders.RemoteIp),
        ["user_agent"] = HeaderOrNull(message, AuditHeaders.UserAgent),
        ["http_method"] = HeaderOrNull(message, AuditHeaders.HttpMethod),
        ["request_path"] = HeaderOrNull(message, AuditHeaders.RequestPath),
        ["target_resource"] = HeaderOrNull(message, AuditHeaders.TargetResource),
        ["status_code"] = HeaderOrNull(message, AuditHeaders.StatusCode),
        ["duration_ms"] = HeaderOrNull(message, AuditHeaders.DurationMs),
        ["exception_type"] = HeaderOrNull(message, AuditHeaders.ExceptionType),
        ["exception_message"] = HeaderOrNull(message, AuditHeaders.ExceptionMessage),
        ["payload"] = message.Body,
        ["cluster_name"] = clusterName
    };

    private static object? HeaderOrNull(IMessage message, string name) =>
        message.Headers.TryGetValue(name, out var value) ? value : null;
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
