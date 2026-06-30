namespace redb.Tsak.Core.Services;

/// <summary>
/// Configuration for hot reload. Bound to <c>Tsak:HotReload</c>.
/// </summary>
public class HotReloadOptions
{
    /// <summary>Enable hot reload. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Filesystem scan interval in seconds. Default: 30.</summary>
    public int ScanIntervalSeconds { get; set; } = 30;

    /// <summary>Number of previous versions to keep for rollback. Default: 2.</summary>
    public int KeepVersions { get; set; } = 2;

    /// <summary>Timeout (seconds) for a new module to start before auto-rollback. Default: 60.</summary>
    public int StartupTimeoutSeconds { get; set; } = 60;

    /// <summary>Enable rolling update across cluster nodes in sequence. Default: true.</summary>
    public bool RollingUpdate { get; set; } = true;

    /// <summary>
    /// Number of scan cycles to wait before confirming a module removal (DLL deleted from disk).
    /// Protects against false removals during file replacement (delete + copy). Default: 1.
    /// With ScanIntervalSeconds=10 and RemovalDebounceScans=1, the effective delay is ~10s.
    /// </summary>
    public int RemovalDebounceScans { get; set; } = 1;

    /// <summary>
    /// Use collectible AssemblyLoadContext for module isolation. Default: false.
    /// When true, old module versions are fully unloaded from memory on hot-swap.
    /// When false (default), old versions stay in memory but there are no compatibility
    /// issues with XmlSerializer, compiled Regex, System.Text.Json source generators,
    /// or other Reflection.Emit-based tools.
    /// Set to true only if your modules do not use any Emit-based APIs.
    /// </summary>
    public bool Collectible { get; set; } = false;

    /// <summary>
    /// Path to the shared assembly directory. Default: "Libs/shared".
    /// Assemblies here are loaded into the Default ALC and auto-registered
    /// as components in all module contexts.
    /// </summary>
    public string SharedPath { get; set; } = "Libs/shared";

    /// <summary>
    /// When true, all contexts are restarted (in dependency order) when a shared
    /// assembly changes on disk. Default: true.
    /// </summary>
    public bool RestartContextsOnSharedChange { get; set; } = true;
}
