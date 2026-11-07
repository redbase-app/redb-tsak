using System.Security.Cryptography;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Verifies a detached signature over a <c>.tpkg</c> package against a configured public key.
/// <para>
/// This is the trust anchor for module deployment: the signature is checked at the <b>load
/// boundary</b> (before any code from the package runs), so the public key — not "who wrote the
/// file" — decides what may execute. It covers both API uploads and files dropped into the module
/// directory, and uses only the BCL (<see cref="RSA"/> / <see cref="ECDsa"/>) — no external tool,
/// no cosign dependency.
/// </para>
/// <para>
/// Signing (in CI) is a SHA-256 signature over the raw <c>.tpkg</c> bytes with the matching private
/// key; the signature travels next to the package as <c>{name}.tpkg.sig</c> (base64 or raw bytes).
/// Both RSA (PKCS#1 v1.5) and ECDSA are accepted — the key type is inferred from the PEM.
/// </para>
/// </summary>
public sealed class ModuleSignatureVerifier
{
    private readonly string _publicKeyPem;

    public ModuleSignatureVerifier(string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
            throw new ArgumentException("Public key PEM is required.", nameof(publicKeyPem));
        _publicKeyPem = publicKeyPem;
    }

    /// <summary>
    /// Loads a verifier from a PEM public key file, or returns <c>null</c> when the path is unset —
    /// signaling "no key configured".
    /// </summary>
    public static ModuleSignatureVerifier? FromKeyFile(string? publicKeyPath)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPath)) return null;
        if (!File.Exists(publicKeyPath))
            throw new FileNotFoundException($"Module signing public key not found: {publicKeyPath}");
        return new ModuleSignatureVerifier(File.ReadAllText(publicKeyPath));
    }

    /// <summary>
    /// Verifies <paramref name="signature"/> over <paramref name="packageBytes"/>. Returns
    /// <c>false</c> on any mismatch or malformed input — never throws for a bad signature, so a
    /// forged upload is a clean rejection, not a crash.
    /// </summary>
    public bool Verify(byte[] packageBytes, byte[] signature)
    {
        if (packageBytes is null || packageBytes.Length == 0) return false;
        if (signature is null || signature.Length == 0) return false;

        // Try RSA first, then ECDSA — the PEM importer accepts either; we don't know a priori.
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_publicKeyPem);
            return rsa.VerifyData(packageBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException) { /* not an RSA key — fall through */ }
        catch (ArgumentException) { /* malformed signature for RSA — fall through */ }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(_publicKeyPem);
            return ecdsa.VerifyData(packageBytes, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Reads a signature that may be stored as base64 text or as raw signature bytes and verifies it.
    /// </summary>
    public bool VerifyWithEncodedSignature(byte[] packageBytes, byte[] signatureFileBytes)
    {
        if (signatureFileBytes is null || signatureFileBytes.Length == 0) return false;

        // Prefer base64 (how CI/openssl usually emit .sig); fall back to raw bytes.
        try
        {
            var text = System.Text.Encoding.ASCII.GetString(signatureFileBytes).Trim();
            var decoded = Convert.FromBase64String(text);
            if (Verify(packageBytes, decoded)) return true;
        }
        catch (FormatException) { /* not base64 — try raw */ }

        return Verify(packageBytes, signatureFileBytes);
    }
}
