using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Reads <c>Tsak:Auth:Keys</c> into <see cref="ApiKeyRecord"/>s. The one place both stores use, so a key
/// written in configuration behaves the same whether Tsak keeps its keys in redb or in the file.
/// <para>
/// Two shapes of the same entry used to fail silently and cost a first-time user an evening.
/// <list type="bullet">
///   <item><b>Hash case.</b> <see cref="ApiKeyService.HashKey"/> produces UPPERCASE hex, and the stored value
///   was whatever the configuration said. A lowercase hash was seeded, logged as seeded, and then matched
///   nothing: the redb store looks the hash up by an exact <c>ValueUnique</c> comparison, which is
///   case-sensitive on PostgreSQL and case-insensitive on SQL Server's default collation — the same
///   configuration behaved differently per database. The hash is normalised here, once, for both stores.</item>
///   <item><b>Roles as an array.</b> <c>Keys:0:Roles:0=admin</c> (the shape environment variables force:
///   <c>Tsak__Auth__Keys__0__Roles__0</c>) reads as a section, not a value, so <c>child["Roles"]</c> was null
///   and the key ended up with no roles at all. With <c>Tsak:Auth:EnforceRoles=true</c> that key is refused
///   everywhere, with a message about permissions rather than about its own configuration.</item>
/// </list>
/// </para>
/// </summary>
internal static class ApiKeyConfigReader
{
    /// <summary>Length of an HMAC-SHA256 hash in hex characters.</summary>
    private const int HashLength = 64;

    /// <summary>
    /// The records of <c>Tsak:Auth:Keys</c>. An entry Tsak cannot use is skipped with an error naming it —
    /// never in silence, because a skipped key is indistinguishable from a wrong one at the API.
    /// </summary>
    public static IReadOnlyList<ApiKeyRecord> Read(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var section = configuration.GetSection("Tsak:Auth:Keys");
        if (!section.Exists())
            return [];

        var records = new List<ApiKeyRecord>();
        foreach (var child in section.GetChildren())
        {
            var id = child["Id"];
            var keyHash = child["KeyHash"];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(keyHash))
            {
                logger.LogError(
                    "API key at {Path} is ignored: both Id and KeyHash are required (Id={Id}, KeyHash={HasHash}).",
                    child.Path, id ?? "<unset>", string.IsNullOrWhiteSpace(keyHash) ? "<unset>" : "set");
                continue;
            }

            var normalized = NormalizeHash(keyHash);
            if (normalized is null)
            {
                logger.LogError(
                    "API key '{Id}' is ignored: KeyHash must be {Length} hexadecimal characters (an HMAC-SHA256 hash, "
                    + "as 'tsak auth create' prints it), got {Actual} character(s).",
                    id, HashLength, keyHash.Trim().Length);
                continue;
            }

            records.Add(new ApiKeyRecord
            {
                Id = id,
                KeyHash = normalized,
                Name = child["Name"] ?? "",
                UserId = child["UserId"],
                Roles = ReadRoles(child, id, logger),
                Revoked = bool.TryParse(child["Revoked"], out var r) && r,
                ExpiresAt = DateTimeOffset.TryParse(child["ExpiresAt"], out var exp) ? exp : null,
                CreatedAt = DateTimeOffset.TryParse(child["CreatedAt"], out var cr) ? cr : DateTimeOffset.UtcNow
            });
        }

        return records;
    }

    /// <summary>
    /// The stored form of a key hash: trimmed, uppercase. Returns null when the value is not a hash, so the
    /// caller can refuse it loudly instead of storing something no lookup will ever match.
    /// </summary>
    public static string? NormalizeHash(string? keyHash)
    {
        if (string.IsNullOrWhiteSpace(keyHash))
            return null;

        var trimmed = keyHash.Trim();
        if (trimmed.Length != HashLength)
            return null;

        foreach (var c in trimmed)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex) return null;
        }

        return trimmed.ToUpperInvariant();
    }

    /// <summary>
    /// Roles of one key, comma-separated. Accepts both the scalar form (<c>Roles=admin,ops</c>) and the array
    /// form (<c>Roles:0=admin</c>, <c>Roles:1=ops</c>) — environment variables can only express the latter.
    /// </summary>
    private static string ReadRoles(IConfigurationSection child, string id, ILogger logger)
    {
        var scalar = child["Roles"];
        if (!string.IsNullOrWhiteSpace(scalar))
            return scalar;

        var rolesSection = child.GetSection("Roles");
        if (!rolesSection.Exists())
            return "";

        var roles = rolesSection.GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();

        if (roles.Count == 0)
        {
            logger.LogWarning(
                "API key '{Id}' has a Roles section that yields no role. With Tsak:Auth:EnforceRoles=true such a "
                + "key is refused everywhere. Write roles as 'admin,ops' or as an array of values.",
                id);
            return "";
        }

        return string.Join(",", roles);
    }
}
