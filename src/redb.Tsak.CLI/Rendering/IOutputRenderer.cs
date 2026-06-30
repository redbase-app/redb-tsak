namespace redb.Tsak.CLI.Rendering;

/// <summary>
/// Contract for CLI output renderers.
/// </summary>
public interface IOutputRenderer
{
    /// <summary>Render a single object.</summary>
    void Render<T>(T data);

    /// <summary>Render a collection as a table with named columns.</summary>
    void RenderTable<T>(IEnumerable<T> items, params (string Header, Func<T, string> Value)[] columns);

    /// <summary>Render a key-value detail view.</summary>
    void RenderDetail(params (string Label, string Value)[] rows);

    /// <summary>Render a success message.</summary>
    void Success(string message);

    /// <summary>Render an error message.</summary>
    void Error(string message);
}
