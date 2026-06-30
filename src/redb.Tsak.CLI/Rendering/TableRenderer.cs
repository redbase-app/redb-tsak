using Spectre.Console;

namespace redb.Tsak.CLI.Rendering;

/// <summary>
/// Renders output as colored Spectre.Console tables.
/// </summary>
public sealed class TableRenderer : IOutputRenderer
{
    private readonly bool _noColor;

    /// <summary>
    /// Initializes the table renderer.
    /// </summary>
    /// <param name="noColor">Disable color output.</param>
    public TableRenderer(bool noColor = false)
    {
        _noColor = noColor;
    }

    /// <inheritdoc />
    public void Render<T>(T data)
    {
        if (data is null)
            return;

        var props = typeof(T).GetProperties();
        var table = new Table();
        if (_noColor) table.Border = TableBorder.Ascii;

        table.AddColumn("Property");
        table.AddColumn("Value");

        foreach (var prop in props)
        {
            var value = prop.GetValue(data);
            table.AddRow(
                Markup.Escape(prop.Name),
                Markup.Escape(FormatValue(value)));
        }

        AnsiConsole.Write(table);
    }

    /// <inheritdoc />
    public void RenderTable<T>(IEnumerable<T> items, params (string Header, Func<T, string> Value)[] columns)
    {
        var table = new Table();
        if (_noColor) table.Border = TableBorder.Ascii;

        foreach (var col in columns)
            table.AddColumn(col.Header);

        foreach (var item in items)
        {
            var values = columns.Select(c => Markup.Escape(c.Value(item))).ToArray();
            table.AddRow(values);
        }

        AnsiConsole.Write(table);
    }

    /// <inheritdoc />
    public void RenderDetail(params (string Label, string Value)[] rows)
    {
        var table = new Table().HideHeaders();
        if (_noColor) table.Border = TableBorder.Ascii;

        table.AddColumn("Label");
        table.AddColumn("Value");

        foreach (var (label, value) in rows)
            table.AddRow($"[bold]{Markup.Escape(label)}[/]", Markup.Escape(value));

        AnsiConsole.Write(table);
    }

    /// <inheritdoc />
    public void Success(string message)
    {
        AnsiConsole.MarkupLine(_noColor
            ? Markup.Escape($"OK: {message}")
            : $"[green]✓[/] {Markup.Escape(message)}");
    }

    /// <inheritdoc />
    public void Error(string message)
    {
        AnsiConsole.MarkupLine(_noColor
            ? Markup.Escape($"ERROR: {message}")
            : $"[red]✗[/] {Markup.Escape(message)}");
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "—",
        bool b => b ? "Yes" : "No",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
        string[] arr => string.Join(", ", arr),
        _ => value.ToString() ?? "—"
    };
}
