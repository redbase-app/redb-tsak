namespace redb.Tsak.CLI.Rendering;

/// <summary>
/// Minimal output renderer — prints only essential identifiers and statuses.
/// </summary>
public sealed class QuietRenderer : IOutputRenderer
{
    /// <inheritdoc />
    public void Render<T>(T data)
    {
        if (data is not null)
            Console.WriteLine(data);
    }

    /// <inheritdoc />
    public void RenderTable<T>(IEnumerable<T> items, params (string Header, Func<T, string> Value)[] columns)
    {
        // Print only the first column value per row
        foreach (var item in items)
        {
            if (columns.Length > 0)
                Console.WriteLine(columns[0].Value(item));
        }
    }

    /// <inheritdoc />
    public void RenderDetail(params (string Label, string Value)[] rows)
    {
        foreach (var (_, value) in rows)
            Console.WriteLine(value);
    }

    /// <inheritdoc />
    public void Success(string message)
    {
        // Quiet — no output on success
    }

    /// <inheritdoc />
    public void Error(string message)
    {
        Console.Error.WriteLine(message);
    }
}
