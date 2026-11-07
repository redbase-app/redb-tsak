namespace redb.Tsak.CLI.Rendering;

/// <summary>
/// Factory for creating the appropriate renderer based on the output format.
/// </summary>
public static class RendererFactory
{
    /// <summary>
    /// Creates a renderer for the specified output format.
    /// </summary>
    /// <param name="format">Desired output format.</param>
    /// <param name="noColor">Disable color in table output.</param>
    public static IOutputRenderer Create(OutputFormat format, bool noColor = false) => format switch
    {
        OutputFormat.Json => new JsonRenderer(),
        OutputFormat.Quiet => new QuietRenderer(),
        _ => new TableRenderer(noColor)
    };
}
