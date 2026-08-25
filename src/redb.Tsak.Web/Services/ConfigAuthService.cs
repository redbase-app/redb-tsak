using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using redb.Core.Security;
using redb.Tsak.Web.Pro;

namespace redb.Tsak.Web.Services;

/// <summary>
/// Standalone implementation of <see cref="IAuthService"/>.
/// Validates credentials against config (<c>Tsak:Web:AdminLogin</c> / <c>Tsak:Web:AdminPassword</c>).
/// No database, no IUserProvider — pure config-driven. Stateless: the session is an auth cookie, not
/// an in-memory flag.
/// </summary>
public sealed class ConfigAuthService : IAuthService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigAuthService> _logger;
    private readonly IPasswordHasher _hasher = new BcryptPasswordHasher();
    private bool _warnedPlaintext;

    public ConfigAuthService(IConfiguration configuration, ILogger<ConfigAuthService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<AuthUser?> ValidateAsync(string login, string password)
    {
        _logger.LogInformation("Login attempt for '{Login}' (config auth)", login);

        var adminLogin = _configuration["Tsak:Web:AdminLogin"];
        var adminHash = _configuration["Tsak:Web:AdminPasswordHash"];
        var adminPassword = _configuration["Tsak:Web:AdminPassword"];

        if (string.IsNullOrEmpty(adminLogin)
            || (string.IsNullOrEmpty(adminHash) && string.IsNullOrEmpty(adminPassword)))
        {
            _logger.LogWarning("AdminLogin and AdminPasswordHash/AdminPassword not configured — login rejected");
            return Task.FromResult<AuthUser?>(null);
        }

        var loginOk = string.Equals(login, adminLogin, StringComparison.OrdinalIgnoreCase);

        // Prefer a stored BCrypt hash (Tsak:Web:AdminPasswordHash). BCrypt verification is itself
        // constant-time and also accepts the core's legacy SHA256 hashes. Fall back to a plaintext
        // AdminPassword for back-compat, compared in constant time. (Plaintext in config is
        // discouraged — set AdminPasswordHash instead; see docs.)
        bool passwordOk;
        if (!string.IsNullOrEmpty(adminHash))
        {
            passwordOk = _hasher.VerifyPassword(password, adminHash);
        }
        else
        {
            if (_warnedPlaintext is false)
            {
                _warnedPlaintext = true;
                _logger.LogWarning(
                    "Tsak:Web:AdminPassword is stored in plaintext config. Prefer Tsak:Web:AdminPasswordHash "
                    + "(a BCrypt hash) so the dashboard password is not recoverable from config.");
            }
            passwordOk = FixedTimeEquals(password, adminPassword!);
        }

        if (!loginOk || !passwordOk)
        {
            _logger.LogWarning("Login failed for '{Login}' — invalid credentials", login);
            return Task.FromResult<AuthUser?>(null);
        }

        _logger.LogInformation("Login succeeded: '{Login}' (config auth)", login);
        return Task.FromResult<AuthUser?>(new AuthUser(login, "Administrator", "admin"));
    }

    /// <summary>Length-independent constant-time string comparison over UTF-8 bytes.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(ba), SHA256.HashData(bb));
    }
}
