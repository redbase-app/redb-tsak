using System.Text.Json;

namespace redb.Tsak.CLI.Rendering;

/// <summary>
/// Renders output as indented JSON (for piping and scripting).
/// </summary>
public sealed class JsonRenderer : IOutputRenderer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc />
    public void Render<T>(T data)
    {
        Console.WriteLine(JsonSerializer.Serialize(data, Options));
    }

    /// <inheritdoc />
    public void RenderTable<T>(IEnumerable<T> items, params (string Header, Func<T, string> Value)[] columns)
    {
        // For JSON mode, just serialize the raw items
        Console.WriteLine(JsonSerializer.Serialize(items, Options));
    }

    /// <inheritdoc />
    public void RenderDetail(params (string Label, string Value)[] rows)
    {
        var dict = rows.ToDictionary(r => r.Label, r => r.Value);
        Console.WriteLine(JsonSerializer.Serialize(dict, Options));
    }

    /// <inheritdoc />
    public void Success(string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { status = "ok", message }, Options));
    }

    /// <inheritdoc />
    public void Error(string message)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "error", message }, Options));
    }
}
