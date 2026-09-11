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

// AuditSensitiveAttribute moved to redb.Tsak.Contracts (2026-09-02): the DTOs that need the mark
// live there, and Contracts cannot reference Core — which is why the attribute was applied to
// nothing while it lived here (review К2).
