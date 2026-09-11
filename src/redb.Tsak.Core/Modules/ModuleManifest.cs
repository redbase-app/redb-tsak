using System.Text.Json.Serialization;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Manifest embedded in a .tpkg package (manifest.json).
/// Package metadata: Name, Version (informational for logs), EntryPoints, Dependencies.
/// Module configuration (ContextName, settings) lives in {Name}.config.json.
/// XML route artifacts (Route-XML Ф5.1) ride in the same package as a third artifact type:
/// <see cref="Artifacts"/> is the explicit ordered list of .route.xml entries,
/// <see cref="Resources"/> the in-package directory file-references resolve from, and
/// <see cref="RequiredConfigKeys"/> the configuration keys the module fails fast on when the
/// merged context configuration provides no value. All four are optional — a package without
/// them behaves exactly as before.
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

    /// <summary>Explicit ordered list of XML route artifacts inside the package (e.g. "routes/orders.route.xml").</summary>
    [JsonPropertyName("Artifacts")]
    public List<string> Artifacts { get; set; } = [];

    /// <summary>Route-format version the artifacts were packaged for; the loader refuses a newer one with a clear message.</summary>
    [JsonPropertyName("SchemaVersion")]
    public string? SchemaVersion { get; set; }

    /// <summary>In-package directory the artifacts' file references (XSLT, schemas) resolve from.</summary>
    [JsonPropertyName("Resources")]
    public string Resources { get; set; } = "resources";

    /// <summary>Configuration keys ({{key}} without a default) the module requires from the merged context configuration.</summary>
    [JsonPropertyName("RequiredConfigKeys")]
    public List<string> RequiredConfigKeys { get; set; } = [];
}
