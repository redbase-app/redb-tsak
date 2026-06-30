using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using redb.Route.Controllers;
using redb.Route.Http;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Cross-cutting filter that emits an <see cref="AdminAuditEvent"/> for every action
/// marked with <see cref="AuditAdminActionAttribute"/>. Runs at very high priority
/// (Order = -1000) so audit metadata is captured before any other filter can mutate it.
/// </summary>
public sealed class AdminAuditFilter : IControllerActionFilter
{
    private readonly IAdminAuditService _sink;
    private readonly ILogger<AdminAuditFilter> _logger;

    private const string ItemsKey = "tsak.admin-audit.attr";

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { ApplySensitiveModifier }
        }
    };

    public AdminAuditFilter(IAdminAuditService sink, ILogger<AdminAuditFilter> logger)
    {
        _sink = sink;
        _logger = logger;
    }

    public int Order => -1000;

    public Task BeforeAsync(ControllerActionContext context, CancellationToken ct)
    {
        // Resolve attribute once and stash for AfterAsync.
        var attr = context.Action.Method.GetCustomAttribute<AuditAdminActionAttribute>();
        if (attr is not null)
            context.Items[ItemsKey] = attr;
        return Task.CompletedTask;
    }

    public async Task AfterAsync(ControllerActionContext context, CancellationToken ct)
    {
        if (!context.Items.TryGetValue(ItemsKey, out var raw) || raw is not AuditAdminActionAttribute attr)
            return;

        try
        {
            var ev = BuildEvent(context, attr);
            await _sink.RecordAsync(ev, ct);
        }
        catch (Exception ex)
        {
            // Best-effort: never propagate audit failures to the request.
            _logger.LogError(ex, "Failed to record admin audit event for action {Action}", context.Action.Method.Name);
        }
    }

    private static AdminAuditEvent BuildEvent(ControllerActionContext context, AuditAdminActionAttribute attr)
    {
        var actionName = attr.ActionName ?? context.Action.Method.Name;
        var ex = context.Exchange;

        string? GetProp(string key) => ex.Properties.TryGetValue(key, out var v) ? v?.ToString() : null;
        string? GetHeader(string key) => ex.In.Headers.TryGetValue(key, out var v) ? v?.ToString() : null;

        string? targetResource = null;
        if (!string.IsNullOrEmpty(attr.TargetParam))
        {
            // First check route params (path tokens), then resolved arguments by name.
            if (context.RouteParams.TryGetValue(attr.TargetParam, out var tp))
            {
                targetResource = tp;
            }
            else
            {
                var paramInfos = context.Action.Method.GetParameters();
                for (var i = 0; i < paramInfos.Length && i < context.Arguments.Length; i++)
                {
                    if (string.Equals(paramInfos[i].Name, attr.TargetParam, StringComparison.Ordinal))
                    {
                        targetResource = context.Arguments[i]?.ToString();
                        break;
                    }
                }
            }
        }

        var payload = SerializePayload(context.Action.Method, context.Arguments);

        return new AdminAuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Action = actionName,
            ControllerType = context.Action.ControllerType.FullName ?? context.Action.ControllerType.Name,
            ActorPrincipalName = GetProp("auth.user-id") ?? GetProp("auth.key-name"),
            ActorApiKeyId = GetProp("auth.key-id"),
            RemoteIp = GetHeader(HttpHeaders.RemoteAddress),
            UserAgent = GetHeader("User-Agent"),
            HttpMethod = GetHeader(HttpHeaders.Method),
            RequestPath = GetHeader(HttpHeaders.Path),
            TargetResource = targetResource,
            Payload = payload,
            StatusCode = context.StatusCode,
            DurationMs = context.Elapsed.TotalMilliseconds,
            ExceptionType = context.Exception?.GetType().FullName,
            ExceptionMessage = context.Exception?.Message
        };
    }

    private static string? SerializePayload(MethodInfo method, object?[] arguments)
    {
        if (arguments is null || arguments.Length == 0) return null;
        var paramInfos = method.GetParameters();

        var dict = new Dictionary<string, object?>(arguments.Length, StringComparer.Ordinal);
        for (var i = 0; i < paramInfos.Length && i < arguments.Length; i++)
        {
            var name = paramInfos[i].Name ?? $"arg{i}";
            dict[name] = arguments[i];
        }

        try
        {
            return JsonSerializer.Serialize(dict, PayloadJsonOptions);
        }
        catch
        {
            // Defensive: never fail audit because payload couldn't serialize.
            return null;
        }
    }

    /// <summary>
    /// JSON contract modifier that replaces the value of any property/field marked with
    /// <see cref="AuditSensitiveAttribute"/> with the literal string <c>"***"</c>.
    /// </summary>
    private static void ApplySensitiveModifier(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object) return;

        foreach (var prop in typeInfo.Properties)
        {
            var attrs = prop.AttributeProvider?.GetCustomAttributes(typeof(AuditSensitiveAttribute), inherit: true);
            if (attrs is not null && attrs.Length > 0)
            {
                prop.CustomConverter = SensitiveConverter.Instance;
            }
        }
    }

    private sealed class SensitiveConverter : JsonConverter<object>
    {
        public static readonly SensitiveConverter Instance = new();

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            writer.WriteStringValue("***");
        }
    }
}
