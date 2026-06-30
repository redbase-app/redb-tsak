namespace redb.Tsak.Core.Security;

/// <summary>
/// Marks a controller action as an audited admin operation.
/// When applied, <see cref="AdminAuditFilter"/> records an
/// <see cref="AdminAuditEvent"/> through the registered <see cref="IAdminAuditService"/>
/// after the action completes (success or failure).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AuditAdminActionAttribute : Attribute
{
    /// <summary>
    /// Optional logical action name. If not specified, the controller method name is used.
    /// </summary>
    public string? ActionName { get; init; }

    /// <summary>
    /// Optional name of the action parameter that identifies the target resource
    /// (e.g. <c>contextName</c>, <c>routeId</c>, <c>userId</c>). When set, the filter
    /// records the resolved argument value into <see cref="AdminAuditEvent.TargetResource"/>.
    /// </summary>
    public string? TargetParam { get; init; }
}

/// <summary>
/// Marks a property of an audit-payload DTO as containing sensitive data
/// (passwords, secrets, API keys, tokens). The property is replaced with the literal
/// string <c>"***"</c> in the audit payload.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class AuditSensitiveAttribute : Attribute
{
}
