using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using redb.Tsak.Web.Pro;

namespace redb.Tsak.Web.Services;

/// <summary>
/// Standalone implementation of <see cref="IAuthService"/>.
/// Validates credentials against config (<c>Tsak:Web:AdminLogin</c> / <c>Tsak:Web:AdminPassword</c>).
/// No database, no IUserProvider — pure config-driven.
/// </summary>
public sealed class ConfigAuthService : IAuthService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigAuthService> _logger;

    private string? _login;
    private string? _displayName;
    private string? _role;

    public event Action? OnAuthStateChanged;

    public ConfigAuthService(IConfiguration configuration, ILogger<ConfigAuthService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAuthenticated => _login is not null;

    /// <inheritdoc />
    public string? Login => _login;

    /// <inheritdoc />
    public string? DisplayName => _displayName;

    /// <inheritdoc />
    public string? Role => _role;

    /// <inheritdoc />
    public bool IsAdmin => string.Equals(_role, "admin", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<bool> LoginAsync(string login, string password)
    {
        _logger.LogInformation("Login attempt for '{Login}' (config auth)", login);

        var adminLogin = _configuration["Tsak:Web:AdminLogin"];
        var adminPassword = _configuration["Tsak:Web:AdminPassword"];

        if (string.IsNullOrEmpty(adminLogin) || string.IsNullOrEmpty(adminPassword))
        {
            _logger.LogWarning("AdminLogin/AdminPassword not configured — login rejected");
            return Task.FromResult(false);
        }

        if (!string.Equals(login, adminLogin, StringComparison.OrdinalIgnoreCase)
            || password != adminPassword)
        {
            _logger.LogWarning("Login failed for '{Login}' — invalid credentials", login);
            return Task.FromResult(false);
        }

        _login = login;
        _displayName = "Administrator";
        _role = "admin";

        _logger.LogInformation("Login succeeded: '{Login}' (config auth)", login);
        OnAuthStateChanged?.Invoke();
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public void Logout()
    {
        _logger.LogInformation("User '{Login}' logged out", _login);
        _login = null;
        _displayName = null;
        _role = null;
        OnAuthStateChanged?.Invoke();
    }
}
