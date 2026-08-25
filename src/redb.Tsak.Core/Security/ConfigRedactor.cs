using System.Text.RegularExpressions;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Redacts secrets from effective-configuration output (<c>GET /api/system/config</c>).
/// <para>
/// Three layers, applied in order:
/// <list type="number">
///   <item><b>Sensitive key</b> — the value is masked wholesale when any segment of the key path
///     (not just the leaf) looks sensitive: password / secret / token / apikey / authorization /
///     webhook / … . A webhook URL is itself a bearer credential, so the whole value goes.</item>
///   <item><b>Embedded connection-string password</b> — a <c>Password=</c>/<c>Pwd=</c> segment is
///     masked inside <em>any</em> value, not only under the <c>ConnectionStrings:</c> prefix, so the
///     host/database stay visible for diagnostics while the password does not.</item>
///   <item><b>URI userinfo</b> — <c>scheme://user:pass@host</c> in any value has its userinfo masked
///     (<c>scheme://***@host</c>), catching broker/endpoint URIs like <c>amqp://u:p@host</c>.</item>
/// </list>
/// Fail-closed bias: the key-path check errs toward over-masking (a benign value under a sensitive-
/// looking path is masked) rather than leaking.
/// </para>
/// </summary>
public static partial class ConfigRedactor
{
    // Matched against the WHOLE key path (case-insensitive substring), so e.g.
    // "Tsak:Alerts:Webhook:Url" masks on "webhook" even though its leaf is "Url", and
    // "…:Headers:Authorization" masks on "authorization". Deliberately NOT including a bare
    // "connectionstring" marker: real connection strings are scrubbed partially (rule 2) so their
    // host/database stay visible.
    private static readonly string[] SensitiveKeyMarkers =
    {
        "password", "pwd", "passwd", "secret", "token", "apikey", "keyhash",
        "privatekey", "credential", "authorization", "accountkey", "sastoken", "webhook",
    };

    /// <summary>Redacts one key/value pair. Returns (value, wasRedacted).</summary>
    public static (string? value, bool redacted) Redact(string key, string? value)
    {
        if (value is null) return (null, false);

        // Rule 1: sensitive key path → mask the value wholesale (empty stays empty, nothing to hide).
        foreach (var marker in SensitiveKeyMarkers)
            if (key.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return (value.Length == 0 ? value : "***", value.Length != 0);

        // Rules 2 & 3: scrub embedded secrets inside the value, wherever the value lives.
        var scrubbed = PasswordAssignment().Replace(value, m => m.Groups[1].Value + "***");
        scrubbed = UriUserInfo().Replace(scrubbed, m => m.Groups[1].Value + "***@");

        return (scrubbed, scrubbed != value);
    }

    // "Password=" / "Pwd=" up to the next ';' or end — connection-string style, in any value.
    [GeneratedRegex(@"(?i)(\b(?:password|pwd)\s*=\s*)[^;]*", RegexOptions.Compiled)]
    private static partial Regex PasswordAssignment();

    // "scheme://userinfo@" — the userinfo (user[:pass]) of a URI authority. [^/@\s] keeps the match
    // inside the authority so an '@' later in a path/query is never mistaken for userinfo.
    [GeneratedRegex(@"(?i)([a-z][a-z0-9+.\-]*://)[^/@\s]*@", RegexOptions.Compiled)]
    private static partial Regex UriUserInfo();
}
