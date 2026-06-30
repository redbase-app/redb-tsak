using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// API key management: create, list, revoke keys.
/// POST   /api/auth/keys    — create new key (returns raw key once)
/// GET    /api/auth/keys    — list all keys (no raw keys)
/// DELETE /api/auth/keys/{id} — revoke a key
/// </summary>
[Route("/api/auth")]
public class AuthController : RedbController
{
    private ApiKeyService GetKeyService() => Context.GetService<ApiKeyService>()
        ?? throw new InvalidOperationException("ApiKeyService not registered in context");

    [HttpPost("/keys")]
    [AuditAdminAction(ActionName = "CreateApiKey")]
    public async Task<object> CreateKey([FromBody] CreateKeyRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            ApiResponse.BadRequest(Exchange, "Name is required.");
            Exchange.Stop();
            return null!;
        }

        var svc = GetKeyService();
        var (rawKey, record) = await svc.CreateKeyAsync(
            request.Name,
            request.UserId,
            request.Roles,
            request.ExpiresAt);

        return new ApiKeyCreatedResponse
        {
            Id = record.Id,
            Key = rawKey,
            Name = record.Name,
            Roles = record.Roles ?? "",
            UserId = record.UserId,
            CreatedAt = record.CreatedAt,
            ExpiresAt = record.ExpiresAt,
            Message = "Store this key securely. It will not be shown again."
        };
    }

    [HttpGet("/keys")]
    public async Task<object> ListKeys()
    {
        var svc = GetKeyService();
        var keys = await svc.ListKeysAsync();
        return keys.Select(k => new ApiKeyInfo
        {
            Id = k.Id, Name = k.Name, Roles = k.Roles ?? "", UserId = k.UserId,
            CreatedAt = k.CreatedAt, ExpiresAt = k.ExpiresAt, Revoked = k.Revoked, IsValid = k.IsValid
        }).ToList();
    }

    [HttpDelete("/keys/{id}")]
    [AuditAdminAction(ActionName = "RevokeApiKey", TargetParam = "id")]
    public async Task<object?> RevokeKey([FromRoute("id")] string id)
    {
        var svc = GetKeyService();
        var revoked = await svc.RevokeKeyAsync(id);
        if (!revoked)
        {
            ApiResponse.NotFound(Exchange, $"Key '{id}' not found.");
            Exchange.Stop();
            return null;
        }
        return new ApiKeyRevokedResponse { Id = id, Revoked = true };
    }
}

public sealed record CreateKeyRequest
{
    public string Name { get; init; } = "";
    public string? UserId { get; init; }
    public string? Roles { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}
