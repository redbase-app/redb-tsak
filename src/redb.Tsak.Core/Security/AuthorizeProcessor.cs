using redb.Route.Abstractions;
using Serilog;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Route pipeline processor that enforces API key authentication.
/// Extracts key from Authorization header (Bearer tsak_...) or X-Api-Key header,
/// validates via <see cref="ApiKeyService"/>, and sets auth properties on the exchange.
/// On failure, writes 401/403 to exchange.Out and stops the pipeline.
/// </summary>
public sealed class AuthorizeProcessor : IProcessor
{
    private static readonly ILogger Log = Serilog.Log.ForContext<AuthorizeProcessor>();

    private readonly ApiKeyService _keyService;
    private readonly string[]? _requiredRoles;

    public AuthorizeProcessor(ApiKeyService keyService, string[]? requiredRoles = null)
    {
        _keyService = keyService ?? throw new ArgumentNullException(nameof(keyService));
        _requiredRoles = requiredRoles;
    }

    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var rawKey = ExtractApiKey(exchange);

        if (string.IsNullOrWhiteSpace(rawKey))
        {
            Log.Warning("API request rejected: missing API key");
            ApiResponse.Unauthorized(exchange, "Missing API key. Provide via Authorization: Bearer <key> or X-Api-Key header.");
            exchange.Stop();
            return;
        }

        var record = await _keyService.ValidateAsync(rawKey, ct);
        if (record is null)
        {
            Log.Warning("API request rejected: invalid or expired API key");
            ApiResponse.Unauthorized(exchange, "Invalid or expired API key.");
            exchange.Stop();
            return;
        }

        // Check required roles if specified
        if (_requiredRoles is { Length: > 0 })
        {
            var userRoles = record.GetRoles();
            var hasRole = false;
            foreach (var role in _requiredRoles)
            {
                if (userRoles.Contains(role))
                {
                    hasRole = true;
                    break;
                }
            }

            if (!hasRole)
            {
                Log.Warning("API request rejected: insufficient roles for key {KeyId}. Required: {Required}, Has: {Has}",
                    record.Id, string.Join(",", _requiredRoles), record.Roles);
                ApiResponse.Forbidden(exchange, "Insufficient permissions.");
                exchange.Stop();
                return;
            }
        }

        // Auth passed — set properties for downstream processors/controllers
        exchange.Properties["auth.key-id"] = record.Id;
        exchange.Properties["auth.key-name"] = record.Name;
        exchange.Properties["auth.user-id"] = record.UserId;
        exchange.Properties["auth.roles"] = record.Roles;
        exchange.Properties["auth.authenticated"] = true;

        Log.Debug("API key authenticated: {KeyName} ({KeyId}), roles: {Roles}", record.Name, record.Id, record.Roles);
    }

    private static string? ExtractApiKey(IExchange exchange)
    {
        // Try Authorization: Bearer <key>
        var auth = exchange.In.getHeader("Authorization")?.ToString();
        if (!string.IsNullOrWhiteSpace(auth))
        {
            const string bearerPrefix = "Bearer ";
            if (auth.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
                return auth[bearerPrefix.Length..].Trim();
        }

        // Fallback: X-Api-Key header
        var apiKey = exchange.In.getHeader("X-Api-Key")?.ToString();
        if (!string.IsNullOrWhiteSpace(apiKey))
            return apiKey;

        return null;
    }
}
