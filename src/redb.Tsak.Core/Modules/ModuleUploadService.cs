using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace redb.Tsak.Core.Modules;

/// <summary>Outcome of an upload or rollback attempt.</summary>
public sealed record ModuleDeployResult(bool Success, string Message, string? ModuleName = null, string? Version = null);

/// <summary>
/// Handles remote module deployment: validates and installs an uploaded <c>.tpkg</c>, and rolls a
/// module back to its previous on-disk version. Every defense lives here, so the controller stays
/// thin:
/// <list type="bullet">
///   <item>upload disabled unless <c>Tsak:Modules:Upload:Enabled=true</c>;</item>
///   <item>size ceiling;</item>
///   <item>the bytes must be a valid ZIP containing <c>manifest.json</c> with a name;</item>
///   <item>the stored filename is derived from the manifest name and sanitized — never from client
///     input — so a crafted name cannot escape the target directory (zip-slip / path traversal);</item>
///   <item>signature verified <b>before</b> anything touches the live directory (fail-fast), in
///     addition to the authoritative load-boundary check in <c>HotReloadService</c>;</item>
///   <item>atomic install (temp file → move), with the previous version archived for rollback.</item>
/// </list>
/// </summary>
public sealed class ModuleUploadService
{
    private readonly ModuleUploadOptions _options;
    private readonly ModuleSignatureOptions _signature;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ModuleUploadService> _logger;

    public ModuleUploadService(
        ModuleUploadOptions options,
        ModuleSignatureOptions signature,
        IConfiguration configuration,
        ILogger<ModuleUploadService> logger)
    {
        _options = options;
        _signature = signature;
        _configuration = configuration;
        _logger = logger;
    }

    public bool UploadEnabled => _options.Enabled;

    /// <summary>
    /// Validates and installs an uploaded package. <paramref name="signatureBytes"/> is the detached
    /// signature (base64 or raw); may be null when signing is not enforced.
    /// </summary>
    public async Task<ModuleDeployResult> InstallAsync(byte[] packageBytes, byte[]? signatureBytes, CancellationToken ct)
    {
        if (!_options.Enabled)
            return new ModuleDeployResult(false, "Module upload is disabled (Tsak:Modules:Upload:Enabled=false).");

        // 1. Size ceiling — reject before doing any work.
        var maxBytes = (long)_options.MaxSizeMB * 1024 * 1024;
        if (packageBytes is null || packageBytes.Length == 0)
            return new ModuleDeployResult(false, "Empty upload.");
        if (packageBytes.Length > maxBytes)
            return new ModuleDeployResult(false, $"Package exceeds the {_options.MaxSizeMB} MB limit.");

        // 2. Signature — fail fast before the package reaches the live directory.
        var sigResult = VerifySignature(packageBytes, signatureBytes);
        if (!sigResult.ok)
            return new ModuleDeployResult(false, sigResult.message);

        // 3. Structural validation — must be a valid ZIP with a named manifest.
        string moduleName;
        string? version;
        try
        {
            (moduleName, version) = ReadManifest(packageBytes);
        }
        catch (Exception ex)
        {
            return new ModuleDeployResult(false, $"Invalid package: {ex.Message}");
        }

        // 4. Sanitize the stored name — derived from the manifest, never from a client filename.
        var safeName = SanitizeName(moduleName);
        if (safeName is null)
            return new ModuleDeployResult(false, $"Unsafe module name in manifest: '{moduleName}'.");

        // 5. Resolve and confine the target directory.
        var targetDir = ResolveTargetDir();
        if (targetDir is null)
            return new ModuleDeployResult(false, "No module directory configured (Tsak:Modules:AssemblyPaths).");
        Directory.CreateDirectory(targetDir);

        var finalPath = Path.Combine(targetDir, safeName + ".tpkg");
        var finalFull = Path.GetFullPath(finalPath);
        if (!finalFull.StartsWith(Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return new ModuleDeployResult(false, "Resolved path escapes the module directory."); // belt-and-braces

        try
        {
            // 6. Archive the current version for rollback, then atomically install.
            ArchivePrevious(finalFull, safeName, targetDir);

            var tempPath = finalFull + ".uploading";
            await File.WriteAllBytesAsync(tempPath, packageBytes, ct).ConfigureAwait(false);
            if (signatureBytes is { Length: > 0 })
                await File.WriteAllBytesAsync(finalFull + ".sig", signatureBytes, ct).ConfigureAwait(false);

            // Move is atomic on the same volume; overwrite the live file in one step.
            File.Move(tempPath, finalFull, overwrite: true);

            _logger.LogInformation("Module '{Name}' (v{Version}) installed to {Path}", safeName, version, finalFull);
            return new ModuleDeployResult(true,
                "Package installed. Hot-reload will load it within the scan interval.", safeName, version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install module '{Name}'", safeName);
            return new ModuleDeployResult(false, $"Install failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Dry-run validation of an uploaded package — same structural + signature checks as
    /// <see cref="InstallAsync"/>, but nothing is written. Lets CI verify a package (size, valid ZIP,
    /// manifest, name, signature) before deploying it. Does NOT load the assemblies; the load-time
    /// staged check in <c>HotReloadService</c> covers that on actual install.
    /// </summary>
    public ModuleDeployResult Validate(byte[] packageBytes, byte[]? signatureBytes)
    {
        if (!_options.Enabled)
            return new ModuleDeployResult(false, "Module upload is disabled.");

        var maxBytes = (long)_options.MaxSizeMB * 1024 * 1024;
        if (packageBytes is null || packageBytes.Length == 0)
            return new ModuleDeployResult(false, "Empty package.");
        if (packageBytes.Length > maxBytes)
            return new ModuleDeployResult(false, $"Package exceeds the {_options.MaxSizeMB} MB limit.");

        var sig = VerifySignature(packageBytes, signatureBytes);
        if (!sig.ok)
            return new ModuleDeployResult(false, sig.message);

        try
        {
            var (name, version) = ReadManifest(packageBytes);
            if (SanitizeName(name) is null)
                return new ModuleDeployResult(false, $"Unsafe module name in manifest: '{name}'.");
            return new ModuleDeployResult(true, "Package is valid.", name, version);
        }
        catch (Exception ex)
        {
            return new ModuleDeployResult(false, $"Invalid package: {ex.Message}");
        }
    }

    /// <summary>Restores the most recent archived version of a module.</summary>
    public ModuleDeployResult Rollback(string moduleName)
    {
        if (!_options.Enabled)
            return new ModuleDeployResult(false, "Module upload is disabled.");

        var safeName = SanitizeName(moduleName);
        if (safeName is null)
            return new ModuleDeployResult(false, $"Unsafe module name: '{moduleName}'.");

        var targetDir = ResolveTargetDir();
        if (targetDir is null)
            return new ModuleDeployResult(false, "No module directory configured.");

        var live = Path.Combine(targetDir, safeName + ".tpkg");
        var versions = Directory.GetFiles(targetDir, safeName + ".tpkg.v*")
            .OrderByDescending(f => f) // v-numbers are zero-padded so lexical == numeric
            .ToList();
        if (versions.Count == 0)
            return new ModuleDeployResult(false, $"No previous version archived for '{safeName}'.");

        var previous = versions[0];
        try
        {
            File.Copy(previous, live, overwrite: true);
            var sig = previous + ".sig";
            if (File.Exists(sig)) File.Copy(sig, live + ".sig", overwrite: true);
            File.Delete(previous);
            if (File.Exists(sig)) File.Delete(sig);

            _logger.LogWarning("Module '{Name}' rolled back to {Previous}", safeName, Path.GetFileName(previous));
            return new ModuleDeployResult(true, "Rolled back to the previous version.", safeName);
        }
        catch (Exception ex)
        {
            return new ModuleDeployResult(false, $"Rollback failed: {ex.Message}");
        }
    }

    // ── internals ────────────────────────────────────────────────────

    private (bool ok, string message) VerifySignature(byte[] packageBytes, byte[]? signatureBytes)
    {
        var pem = _signature.ResolvePem();

        if (pem is not null)
        {
            if (signatureBytes is null || signatureBytes.Length == 0)
                return (false, "A signature is required (a signing public key is configured) but none was provided.");

            var verifier = new ModuleSignatureVerifier(pem);
            return verifier.VerifyWithEncodedSignature(packageBytes, signatureBytes)
                ? (true, "")
                : (false, "Signature verification failed — the package is unsigned by the trusted key or has been modified.");
        }

        // No key configured.
        if (_options.RequireSignatureForUpload)
            return (false, "Uploads require a signature but no signing public key is configured " +
                           "(Tsak:Modules:Signature:PublicKeyPath). Configure one, or set " +
                           "Tsak:Modules:Upload:RequireSignatureForUpload=false to allow unsigned uploads.");

        _logger.LogWarning(
            "Accepting an UNSIGNED module upload — no signing key configured and RequireSignatureForUpload=false. " +
            "This trusts any admin API key with code execution; configure a signing key for production.");
        return (true, "");
    }

    private static (string name, string? version) ReadManifest(byte[] packageBytes)
    {
        using var ms = new MemoryStream(packageBytes, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read); // throws if not a valid ZIP
        var entry = zip.GetEntry("manifest.json")
                    ?? throw new InvalidDataException("manifest.json is missing.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(stream)
                       ?? throw new InvalidDataException("manifest.json could not be parsed.");
        if (string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidDataException("manifest.json has no module name.");
        return (manifest.Name, manifest.Version);
    }

    /// <summary>Allows only a safe file-name fragment: letters, digits, dot, dash, underscore.</summary>
    private static string? SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) return null;
        foreach (var ch in name)
            if (!char.IsLetterOrDigit(ch) && ch is not '.' and not '-' and not '_')
                return null;
        return name;
    }

    private string? ResolveTargetDir()
    {
        if (!string.IsNullOrWhiteSpace(_options.TargetPath))
            return _options.TargetPath;
        var paths = _configuration.GetSection("Tsak:Modules:AssemblyPaths").Get<string[]>();
        return paths is { Length: > 0 } ? paths[0] : null;
    }

    private void ArchivePrevious(string livePath, string safeName, string targetDir)
    {
        if (!File.Exists(livePath)) return;

        // Find the next version slot (zero-padded so lexical ordering matches numeric).
        var existing = Directory.GetFiles(targetDir, safeName + ".tpkg.v*").Length;
        var archived = Path.Combine(targetDir, $"{safeName}.tpkg.v{existing + 1:D4}");
        File.Copy(livePath, archived, overwrite: true);
        var liveSig = livePath + ".sig";
        if (File.Exists(liveSig)) File.Copy(liveSig, archived + ".sig", overwrite: true);

        // Prune old versions beyond KeepVersions.
        var all = Directory.GetFiles(targetDir, safeName + ".tpkg.v*")
            .Where(f => !f.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f)
            .Skip(Math.Max(_options.KeepVersions, 0))
            .ToList();
        foreach (var old in all)
        {
            try { File.Delete(old); if (File.Exists(old + ".sig")) File.Delete(old + ".sig"); }
            catch { /* best effort */ }
        }
    }
}
