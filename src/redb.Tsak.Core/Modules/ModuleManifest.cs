using System.Text.Json.Serialization;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Manifest embedded in a .tpkg package (manifest.json).
/// Package metadata: Name, Version (informational for logs), EntryPoints, Dependencies.
/// Module configuration (ContextName, settings) lives in {Name}.config.json.
/// </summary>
public sealed class ModuleManifest
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("EntryPoints")]
    public List<string> EntryPoints { get; set; } = [];

    [JsonPropertyName("Dependencies")]
    public List<string> Dependencies { get; set; } = [];
}
