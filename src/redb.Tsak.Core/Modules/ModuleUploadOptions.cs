namespace redb.Tsak.Core.Modules;

/// <summary>
/// Deployment security options, bound from <c>Tsak:Modules</c>.
/// <para>
/// A module is executable code that Tsak loads in-process, so every default here is the safe one:
/// remote upload is <b>off</b>, and when signature enforcement is on, unsigned or tampered packages
/// are refused at the load boundary regardless of how they arrived.
/// </para>
/// </summary>
public sealed class ModuleUploadOptions
{
    /// <summary>
    /// Enables <c>POST /api/modules/upload</c> and <c>/rollback</c>. Default: <c>false</c>. A node
    /// that does not need remote deploy exposes no upload surface at all.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Maximum accepted <c>.tpkg</c> size in MB. Default: 100.</summary>
    public int MaxSizeMB { get; set; } = 100;

    /// <summary>
    /// Directory the uploaded package is written to (must be one of <c>Tsak:Modules:AssemblyPaths</c>
    /// so the hot-reload scanner picks it up). When unset, the first configured assembly path is used.
    /// </summary>
    public string? TargetPath { get; set; }

    /// <summary>
    /// Previous package versions to keep on disk for rollback (<c>{name}.tpkg.v{n}</c>). Default: 3.
    /// </summary>
    public int KeepVersions { get; set; } = 3;

    /// <summary>
    /// Require a valid signature for uploads even when no public key is configured to be impossible —
    /// i.e. when <c>true</c> and no key is set, uploads are refused. When <c>false</c>, an upload with
    /// no configured key is allowed but logged as a loud warning. Default: <c>true</c> (safe).
    /// </summary>
    public bool RequireSignatureForUpload { get; set; } = true;
}

/// <summary>
/// Signature-enforcement options, bound from <c>Tsak:Modules:Signature</c>. Governs the load
/// boundary — applies to <b>every</b> <c>.tpkg</c>, whether uploaded via API or dropped into the
/// directory by an operator.
/// </summary>
public sealed class ModuleSignatureOptions
{
    /// <summary>
    /// Reject any <c>.tpkg</c> without a valid <c>.tpkg.sig</c> at load time. Default: <c>false</c>
    /// (trust = filesystem access, the historical behaviour). Turn this on together with remote
    /// upload so a leaked API key cannot deploy arbitrary code.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>Path to the PEM public key used to verify package signatures.</summary>
    public string? PublicKeyPath { get; set; }

    /// <summary>Inline PEM public key (alternative to <see cref="PublicKeyPath"/>).</summary>
    public string? PublicKeyPem { get; set; }

    /// <summary>Resolves the configured PEM from inline value or file, or <c>null</c> when unset.</summary>
    public string? ResolvePem()
    {
        if (!string.IsNullOrWhiteSpace(PublicKeyPem)) return PublicKeyPem;
        if (!string.IsNullOrWhiteSpace(PublicKeyPath) && File.Exists(PublicKeyPath))
            return File.ReadAllText(PublicKeyPath);
        return null;
    }
}
