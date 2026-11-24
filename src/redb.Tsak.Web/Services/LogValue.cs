namespace redb.Tsak.Web.Services;

/// <summary>
/// A value taken from the request (a node id in the page address) before it goes into a log line. The address is
/// percent-decoded, so <c>%0D%0A</c> would arrive as a real line break and could start a forged log line; the line
/// breaks are written escaped instead (<c>\r</c>, <c>\n</c>, and the Unicode line separators).
/// </summary>
public static class LogValue
{
    /// <summary>The value with its line breaks escaped; an absent value is an empty text.</summary>
    public static string Of(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOfAny(LineBreaks) < 0)
            return value ?? string.Empty;

        return value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\u0085", "\\u0085", StringComparison.Ordinal)
            .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);
    }

    private static readonly char[] LineBreaks = ['\r', '\n', '\u0085', '\u2028', '\u2029'];
}
