namespace redb.Tsak.Contracts;

/// <summary>
/// Marks a property of a wire DTO as containing sensitive data (passwords, secrets, API keys,
/// tokens). The admin-audit filter replaces the value with the literal string <c>"***"</c> in the
/// persisted audit payload. Lives in Contracts — the DTOs that need the mark are here, and
/// Contracts cannot reference Core (review 2026-09-02, К2: the attribute existed in Core and was
/// therefore applied to nothing, so plaintext passwords reached the audit trail).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class AuditSensitiveAttribute : Attribute
{
}
