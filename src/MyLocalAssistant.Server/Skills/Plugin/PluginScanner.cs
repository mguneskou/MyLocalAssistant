using System.Text.Json;
using MyLocalAssistant.Shared.Plugins;

namespace MyLocalAssistant.Server.Skills.Plugin;

/// <summary>
/// Walks <c>&lt;install&gt;/plugins/&lt;id&gt;/</c> at startup. Each subfolder must contain:
///   manifest.json     — signed payload (see <see cref="SkillManifest"/>);
///   manifest.json.sig — base64-encoded ed25519 signature over manifest.json bytes;
///   the executable + every file listed in <see cref="SkillManifest.Files"/>.
/// Verification order: signature -> per-file SHA-256 -> manifest sanity. Any failure
/// rejects the plug-in with a logged warning; the rest still load.
/// </summary>
public sealed class PluginScanner
{
    private readonly PluginSignatureVerifier _verifier;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginScanner> _log;
    private readonly string _pluginsRoot;
    private readonly string _outputRoot;

    public PluginScanner(PluginSignatureVerifier verifier, ILoggerFactory lf, ILogger<PluginScanner> log)
    {
        _verifier = verifier;
        _loggerFactory = lf;
        _log = log;
        _pluginsRoot = ServerPaths.PluginsDirectory;
        _outputRoot = ServerPaths.OutputDirectory;
    }

    /// <summary>Scan <c>./plugins/</c> and return every plug-in that passed verification.</summary>
    public IReadOnlyList<PluginSkill> ScanAndLoad()
    {
        var loaded = new List<PluginSkill>();
        if (!Directory.Exists(_pluginsRoot))
        {
            _log.LogInformation("Plug-ins directory {Path} does not exist; skipping plug-in load.", _pluginsRoot);
            return loaded;
        }
        if (_verifier.TrustedKeyCount == 0)
        {
            _log.LogWarning("No trusted keys configured; refusing to load any plug-ins. Drop *.pub files into config/trusted-keys/.");
            return loaded;
        }
        foreach (var dir in Directory.GetDirectories(_pluginsRoot))
        {
            try
            {
                var skill = TryLoad(dir);
                if (skill is not null) loaded.Add(skill);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Plug-in folder {Dir} failed to load.", dir);
            }
        }
        _log.LogInformation("Plug-in scanner: {Count} plug-in(s) verified and loaded from {Root}.", loaded.Count, _pluginsRoot);
        return loaded;
    }

    private PluginSkill? TryLoad(string folder)
    {
        var manifestPath = Path.Combine(folder, "manifest.json");
        var sigPath = manifestPath + ".sig";
        if (!File.Exists(manifestPath)) { _log.LogWarning("Plug-in {Folder} missing manifest.json.", folder); return null; }
        if (!File.Exists(sigPath)) { _log.LogWarning("Plug-in {Folder} missing manifest.json.sig.", folder); return null; }

        var manifestBytes = File.ReadAllBytes(manifestPath);
        var sigBytes = SafeBase64(File.ReadAllText(sigPath).Trim());
        if (sigBytes is null) { _log.LogWarning("Plug-in {Folder} signature is not valid base64.", folder); return null; }

        SkillManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<SkillManifest>(manifestBytes, JsonRpcFraming.Json); }
        catch (Exception ex) { _log.LogWarning(ex, "Plug-in {Folder} manifest is not valid JSON.", folder); return null; }
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.KeyId))
        {
            _log.LogWarning("Plug-in {Folder} manifest is missing required fields (id, keyId).", folder);
            return null;
        }

        if (!_verifier.VerifyManifestSignature(manifest.KeyId, manifestBytes, sigBytes))
        {
            _log.LogWarning("Plug-in {Id} REJECTED: signature invalid for keyId='{Key}'.", manifest.Id, manifest.KeyId);
            return null;
        }

        // Per-file SHA-256 verification of every payload file referenced by the manifest.
        foreach (var f in manifest.Files)
        {
            var path = Path.Combine(folder, f.Path);
            if (!File.Exists(path))
            {
                _log.LogWarning("Plug-in {Id} REJECTED: missing file '{File}'.", manifest.Id, f.Path);
                return null;
            }
            var actual = PluginSignatureVerifier.ComputeSha256Hex(path);
            if (!string.Equals(actual, f.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("Plug-in {Id} REJECTED: hash mismatch for '{File}' (expected={Expected}, actual={Actual}).",
                    manifest.Id, f.Path, f.Sha256, actual);
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(manifest.Entry.Command))
        {
            _log.LogWarning("Plug-in {Id} REJECTED: manifest.entry.command is empty.", manifest.Id);
            return null;
        }
        var entryPath = Path.Combine(folder, manifest.Entry.Command);
        if (!File.Exists(entryPath))
        {
            _log.LogWarning("Plug-in {Id} REJECTED: entry executable '{Exe}' not found.", manifest.Id, manifest.Entry.Command);
            return null;
        }

        var skillLog = _loggerFactory.CreateLogger($"PluginSkill[{manifest.Id}]");
        return new PluginSkill(manifest, folder, _outputRoot, skillLog);
    }

    private static byte[]? SafeBase64(string s)
    {
        try { return Convert.FromBase64String(s); } catch { return null; }
    }
}
