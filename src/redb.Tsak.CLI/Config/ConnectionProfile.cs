namespace redb.Tsak.CLI.Config;

/// <summary>
/// A named connection profile for a Tsak runtime instance.
/// </summary>
public sealed class ConnectionProfile
{
    /// <summary>Profile name (used as file name without extension).</summary>
    public required string Name { get; set; }

    /// <summary>Base URL of the Tsak runtime API.</summary>
    public required string Url { get; set; }

    /// <summary>API key for authentication.</summary>
    public string? ApiKey { get; set; }
}
