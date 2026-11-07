using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// The ONE load-boundary trust gate for module code (review 2026-09-02, К3). Before this class the
/// signature check lived only inside HotReloadService's scan, so startup discovery executed .tpkg
/// packages unverified after every process restart, and a bare .dll dropped into a watched
/// directory bypassed the gate entirely. Every path that is about to load module code — startup
/// discovery, hot-reload scans, staged validation, package reloads — must go through here.
/// <para>
/// <see cref="ReadVerifiedTpkg"/> returns the verified BYTES, and the caller opens the package
/// from that buffer (<c>ModulePackage.Open(byte[], ...)</c>) — verifying a path and then
/// re-reading the file gave an attacker a swap window between the check and the load.
/// </para>
/// </summary>
public sealed class ModuleLoadGate
{
    private readonly ILogger _logger;

    public bool SignatureRequired { get; }
    private readonly ModuleSignatureVerifier? _verifier;

    public ModuleLoadGate(IConfiguration configuration, ILogger<ModuleLoadGate> logger)
    {
        _logger = logger;

        var sigOptions = new ModuleSignatureOptions();
        configuration.GetSection("Tsak:Modules:Signature").Bind(sigOptions);
        SignatureRequired = sigOptions.Required;
        var pem = sigOptions.ResolvePem();
        _verifier = pem is not null ? new ModuleSignatureVerifier(pem) : null;

        if (SignatureRequired && _verifier is null)
            _logger.LogError(
                "Tsak:Modules:Signature:Required=true but no public key is configured — ALL .tpkg packages "
                + "will be refused at load. Set Tsak:Modules:Signature:PublicKeyPath.");
        else if (SignatureRequired)
            _logger.LogInformation("Module signature enforcement ON — unsigned/tampered packages and bare DLLs will be refused");
    }

    /// <summary>
    /// Reads a .tpkg and, when enforcement is on, verifies its detached <c>.sig</c>.
    /// Returns the package bytes to load from, or <c>null</c> when the package must be refused
    /// (missing file, missing/invalid signature, no key configured).
    /// </summary>
    public byte[]? ReadVerifiedTpkg(string tpkgPath)
    {
        byte[] pkg;
        try
        {
            pkg = File.ReadAllBytes(tpkgPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read package {Pkg}", Path.GetFileName(tpkgPath));
            return null;
        }

        if (!SignatureRequired)
            return pkg;

        if (_verifier is null)
        {
            _logger.LogError("Refusing {Pkg}: signature required but no public key configured", Path.GetFileName(tpkgPath));
            return null;
        }

        var sigPath = tpkgPath + ".sig";
        if (!File.Exists(sigPath))
        {
            _logger.LogError("Refusing {Pkg}: no signature file ({Sig}) alongside the package",
                Path.GetFileName(tpkgPath), Path.GetFileName(sigPath));
            return null;
        }

        try
        {
            var sig = File.ReadAllBytes(sigPath);
            if (_verifier.VerifyWithEncodedSignature(pkg, sig))
                return pkg;

            _logger.LogError("Refusing {Pkg}: signature verification FAILED — unsigned by the trusted key or modified",
                Path.GetFileName(tpkgPath));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refusing {Pkg}: signature check errored", Path.GetFileName(tpkgPath));
            return null;
        }
    }

    /// <summary>
    /// A bare .dll carries no manifest and no detached signature, so with enforcement on it is
    /// refused outright — otherwise dropping Evil.dll into a watched directory executes arbitrary
    /// code the .tpkg signature feature claims to prevent. Package deployments (.tpkg + .sig) are
    /// the supported shape under enforcement.
    /// </summary>
    public bool AllowBareDll(string dllPath)
    {
        if (!SignatureRequired)
            return true;

        // The scan revisits the same files every interval — log the refusal once per path,
        // not once per scan.
        if (_refusalLogged.TryAdd(dllPath, 0))
            _logger.LogCritical(
                "Refusing bare assembly {Dll}: Tsak:Modules:Signature:Required=true admits only signed .tpkg packages "
                + "— a bare DLL cannot carry a signature. Package it as a signed .tpkg.",
                Path.GetFileName(dllPath));
        return false;
    }

    private readonly ConcurrentDictionary<string, byte> _refusalLogged = new(StringComparer.OrdinalIgnoreCase);
}
