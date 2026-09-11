namespace redb.Tsak.Core.Contracts;

/// <summary>
/// A module that carries its per-module configuration ({Name}.config.json) embedded in the
/// .tpkg it was discovered from. The coordinator reads <see cref="EmbeddedConfigJson"/> for the
/// module's ContextName (layer-4 identity) and merges it into the context configuration —
/// matching on this contract, not on a concrete module type, so every packaged module kind
/// (static-method, XML routes, future ones) gets the same treatment.
/// </summary>
public interface IEmbeddedConfigModule
{
    /// <summary>Raw JSON of the embedded {Name}.config.json; null when the package carries none.</summary>
    string? EmbeddedConfigJson { get; }
}
