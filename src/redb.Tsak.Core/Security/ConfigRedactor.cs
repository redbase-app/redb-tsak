using System.Text.RegularExpressions;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Redacts secrets from effective-configuration output. Two rules: a value is masked wholesale when
/// its key name looks sensitive (password / secret / token / key-hash / …), and a connection-string
/// value has only its password segment masked so the host/database stay visible for diagnostics.
/// </summary>
public static partial class ConfigRedactor
{
    private static readonly string[] SensitiveKeyMarkers =
        { "password", "pwd", "secret", "token", "apikey", "keyhash", "privatekey", "credential" };

    /// <summary>Redacts one key/value pair. Returns (value, wasRedacted).</summary>
    public static (string? value, bool redacted) Redact(string key, string? value)
    {
        if (value is null) return (null, false);

        // Connection strings: mask only the password segment.
        if (key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase))
        {
            var masked = PasswordInConnString().Replace(value, m => m.Groups[1].Value + "***");
            return (masked, masked != value);
        }

        var leaf = key.Contains(':') ? key[(key.LastIndexOf(':') + 1)..] : key;
        foreach (var marker in SensitiveKeyMarkers)
            if (leaf.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return (value.Length == 0 ? value : "***", value.Length != 0);

        return (value, false);
    }

    // Matches "Password=" / "Pwd=" up to the next ';' or end.
    [GeneratedRegex(@"(?i)(\b(?:password|pwd)\s*=\s*)[^;]*", RegexOptions.Compiled)]
    private static partial Regex PasswordInConnString();
}
