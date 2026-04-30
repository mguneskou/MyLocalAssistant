using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MyLocalAssistant.Server.Skills.Plugin;

/// <summary>
/// Loads ed25519 public keys (32 raw bytes, base64-encoded) from
/// <c>&lt;install&gt;/config/trusted-keys/&lt;keyId&gt;.pub</c> and verifies a detached signature
/// over arbitrary content. <see cref="VerifyManifestSignature"/> is the entry point used by the
/// plug-in scanner; <see cref="VerifyFileHash"/> covers the per-file SHA-256 manifest entries.
/// </summary>
public sealed class PluginSignatureVerifier
{
    private readonly Dictionary<string, byte[]> _trusted; // keyId -> 32-byte ed25519 pub
    private readonly ILogger<PluginSignatureVerifier> _log;

    public PluginSignatureVerifier(ILogger<PluginSignatureVerifier> log)
    {
        _log = log;
        _trusted = new(StringComparer.OrdinalIgnoreCase);
        var dir = ServerPaths.TrustedKeysDirectory;
        if (!Directory.Exists(dir))
        {
            _log.LogInformation("No trusted-keys directory at {Path}; plug-in loader will reject all plug-ins.", dir);
            return;
        }
        foreach (var path in Directory.GetFiles(dir, "*.pub"))
        {
            try
            {
                var keyId = Path.GetFileNameWithoutExtension(path);
                var raw = File.ReadAllText(path).Trim();
                var bytes = Convert.FromBase64String(raw);
                if (bytes.Length != 32)
                {
                    _log.LogWarning("Ignoring trusted key {KeyId}: expected 32 bytes, got {Len}.", keyId, bytes.Length);
                    continue;
                }
                _trusted[keyId] = bytes;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load trusted key {Path}.", path);
            }
        }
        _log.LogInformation("Loaded {Count} trusted plug-in key(s) from {Path}.", _trusted.Count, dir);
    }

    public int TrustedKeyCount => _trusted.Count;

    /// <summary>
    /// Verify a detached ed25519 signature over <paramref name="content"/> using the public
    /// key registered under <paramref name="keyId"/>. Returns <c>false</c> if the key is
    /// unknown, the signature is malformed, or verification fails.
    /// </summary>
    public bool Verify(string keyId, ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature)
    {
        if (!_trusted.TryGetValue(keyId, out var pub)) return false;
        if (signature.Length != 64) return false;
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(pub, 0));
            verifier.BlockUpdate(content.ToArray(), 0, content.Length);
            return verifier.VerifySignature(signature.ToArray());
        }
        catch
        {
            return false;
        }
    }

    public bool VerifyManifestSignature(string keyId, byte[] manifestBytes, byte[] signatureBytes)
        => Verify(keyId, manifestBytes, signatureBytes);

    public static string ComputeSha256Hex(string filePath)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(filePath);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
