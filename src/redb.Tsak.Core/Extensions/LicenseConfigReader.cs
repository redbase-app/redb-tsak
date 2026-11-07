using Microsoft.Extensions.Configuration;

namespace redb.Tsak.Core.Extensions;

/// <summary>
/// Reads a license configuration value that may be either a single string or a JSON array of strings.
/// Multiple JWTs are joined with '|' so that <see cref="redb.Licensing.LicenseStore"/> can split them back.
///
/// Examples (all equivalent):
///   "License": "jwt1|jwt2"
///   "License": [ "jwt1", "jwt2" ]
///   Env: Tsak__Redb__License=jwt1|jwt2
///   Env: Tsak__Redb__License__0=jwt1, Tsak__Redb__License__1=jwt2
/// </summary>
public static class LicenseConfigReader
{
    /// <summary>
    /// Reads the license value at <paramref name="key"/> as either a string or a string array.
    /// Returns null if absent or empty.
    /// </summary>
    public static string? Read(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);

        // Array form: License: [ "...", "..." ]
        var arr = section.Get<string[]>();
        if (arr is { Length: > 0 })
        {
            var joined = string.Join("|", arr.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (!string.IsNullOrWhiteSpace(joined))
                return joined;
        }

        // Scalar form: License: "..."
        var scalar = section.Value ?? configuration[key];
        return string.IsNullOrWhiteSpace(scalar) ? null : scalar;
    }
}
