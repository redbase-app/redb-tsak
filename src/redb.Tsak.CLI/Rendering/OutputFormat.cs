namespace redb.Tsak.CLI.Rendering;

/// <summary>
/// Output format for CLI rendering.
/// </summary>
public enum OutputFormat
{
    /// <summary>Human-readable table/colored output.</summary>
    Table,

    /// <summary>Machine-readable JSON output.</summary>
    Json,

    /// <summary>Minimal output (IDs, names, status codes only).</summary>
    Quiet
}
