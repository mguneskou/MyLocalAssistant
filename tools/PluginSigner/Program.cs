using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

// ┌────────────────────────────────────────────────────────────────────────────┐
// │  mla-plugin-signer                                                         │
// │                                                                            │
// │  Commands:                                                                 │
// │    generate-key <keyId>                                                    │
// │        Generates a fresh ed25519 keypair and writes:                       │
// │          <keyId>.key  – base64 private key (32-byte seed)                 │
// │          <keyId>.pub  – base64 public key  (32 bytes)                     │
// │                                                                            │
// │    sign <pluginFolder> <keyFile>                                           │
// │        1. Computes SHA-256 of every non-sig file listed in manifest.json  │
// │           "files" array PLUS the entry executable.                        │
// │        2. Updates manifest.json with the current file hashes.             │
// │        3. Signs the UTF-8 bytes of manifest.json with the private key     │
// │           (ed25519) and writes <pluginFolder>/manifest.json.sig            │
// │                                                                            │
// │    verify <pluginFolder> <pubFile>                                         │
// │        Reads manifest.json + manifest.json.sig and verifies the           │
// │        signature and all file hashes. Useful for CI checks.               │
// └────────────────────────────────────────────────────────────────────────────┘

if (args.Length < 2)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();
return command switch
{
    "generate-key" => GenerateKey(args),
    "sign"         => Sign(args),
    "verify"       => Verify(args),
    _              => PrintUsage(),
};

// ── generate-key ──────────────────────────────────────────────────────────────

static int GenerateKey(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: mla-plugin-signer generate-key <keyId>");
        return 1;
    }
    var keyId   = args[1];
    var keyFile = keyId + ".key";
    var pubFile = keyId + ".pub";

    if (File.Exists(keyFile) || File.Exists(pubFile))
    {
        Console.Error.WriteLine($"Key files '{keyFile}' or '{pubFile}' already exist. Delete them first.");
        return 1;
    }

    var generator = new Ed25519KeyPairGenerator();
    generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
    AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();

    var priv = (Ed25519PrivateKeyParameters)pair.Private;
    var pub  = (Ed25519PublicKeyParameters)pair.Public;

    // Private key: 32-byte seed
    var seedBytes = priv.GetEncoded();  // 32-byte seed
    var pubBytes  = pub.GetEncoded();   // 32-byte public

    File.WriteAllText(keyFile, Convert.ToBase64String(seedBytes));
    File.WriteAllText(pubFile,  Convert.ToBase64String(pubBytes));

    Console.WriteLine($"Generated ed25519 key pair:");
    Console.WriteLine($"  Private (seed): {keyFile}");
    Console.WriteLine($"  Public:         {pubFile}");
    Console.WriteLine();
    Console.WriteLine("Copy the .pub file to the server's trusted-keys directory.");
    Console.WriteLine("Keep the .key file SECRET — never commit it to source control.");
    return 0;
}

// ── sign ──────────────────────────────────────────────────────────────────────

static int Sign(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: mla-plugin-signer sign <pluginFolder> <keyFile>");
        return 1;
    }
    var folder  = args[1];
    var keyFile = args[2];

    if (!Directory.Exists(folder)) { Console.Error.WriteLine($"Plugin folder not found: {folder}"); return 1; }
    if (!File.Exists(keyFile))     { Console.Error.WriteLine($"Key file not found: {keyFile}");     return 1; }

    var manifestPath = Path.Combine(folder, "manifest.json");
    if (!File.Exists(manifestPath)) { Console.Error.WriteLine($"manifest.json not found in {folder}"); return 1; }

    // Load manifest as mutable JSON.
    var manifestJson = File.ReadAllText(manifestPath);
    var manifest     = JsonNode.Parse(manifestJson) as JsonObject
        ?? throw new InvalidDataException("manifest.json is not a JSON object");

    // Collect files to hash: the entry exe + anything already in "files" list.
    var entryExe = manifest["entry"]?["command"]?.GetValue<string>()
        ?? throw new InvalidDataException("manifest.json missing entry.command");

    var entryPath = Path.Combine(folder, entryExe);
    if (!File.Exists(entryPath))
    {
        Console.Error.WriteLine($"Entry executable not found: {entryPath}");
        Console.Error.WriteLine("Publish the plugin first, then sign.");
        return 1;
    }

    // Discover all non-manifest, non-sig files in the folder (recursive).
    var allFiles = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .Where(f =>
        {
            var name = Path.GetFileName(f);
            return !name.Equals("manifest.json",     StringComparison.OrdinalIgnoreCase) &&
                   !name.Equals("manifest.json.sig", StringComparison.OrdinalIgnoreCase);
        })
        .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToList();

    // Build files array with sha256 hashes.
    var filesArray = new JsonArray();
    foreach (var rel in allFiles)
    {
        var abs  = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
        var hash = ComputeSha256Hex(abs);
        filesArray.Add(new JsonObject
        {
            ["path"]   = rel,
            ["sha256"] = hash,
        });
        Console.WriteLine($"  {hash[..8]}…  {rel}");
    }
    manifest["files"] = filesArray;

    // Serialize with deterministic (sorted) output.
    var updatedJson  = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    var manifestBytes = System.Text.Encoding.UTF8.GetBytes(updatedJson);
    File.WriteAllText(manifestPath, updatedJson);

    // Load private key seed and sign.
    var seedB64  = File.ReadAllText(keyFile).Trim();
    var seed     = Convert.FromBase64String(seedB64);
    if (seed.Length != 32) { Console.Error.WriteLine("Key file must contain a 32-byte (base64) ed25519 seed."); return 1; }

    var privKey  = new Ed25519PrivateKeyParameters(seed, 0);
    var signer   = new Ed25519Signer();
    signer.Init(true, privKey);
    signer.BlockUpdate(manifestBytes, 0, manifestBytes.Length);
    var sigBytes = signer.GenerateSignature();

    var sigPath = Path.Combine(folder, "manifest.json.sig");
    File.WriteAllText(sigPath, Convert.ToBase64String(sigBytes));

    Console.WriteLine();
    Console.WriteLine($"Signed successfully.");
    Console.WriteLine($"  manifest.json     updated with {allFiles.Count} file hash(es)");
    Console.WriteLine($"  manifest.json.sig written ({sigBytes.Length} bytes)");
    return 0;
}

// ── verify ────────────────────────────────────────────────────────────────────

static int Verify(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: mla-plugin-signer verify <pluginFolder> <pubFile>");
        return 1;
    }
    var folder  = args[1];
    var pubFile = args[2];

    var manifestPath = Path.Combine(folder, "manifest.json");
    var sigPath      = Path.Combine(folder, "manifest.json.sig");

    if (!File.Exists(manifestPath)) { Console.Error.WriteLine("manifest.json not found"); return 1; }
    if (!File.Exists(sigPath))      { Console.Error.WriteLine("manifest.json.sig not found"); return 1; }
    if (!File.Exists(pubFile))      { Console.Error.WriteLine($"Public key file not found: {pubFile}"); return 1; }

    var manifestBytes = File.ReadAllBytes(manifestPath);
    var sigBytes      = Convert.FromBase64String(File.ReadAllText(sigPath).Trim());
    var pubBytes      = Convert.FromBase64String(File.ReadAllText(pubFile).Trim());

    if (pubBytes.Length != 32)  { Console.Error.WriteLine("Public key must be 32 bytes."); return 1; }
    if (sigBytes.Length != 64)  { Console.Error.WriteLine($"Signature must be 64 bytes (got {sigBytes.Length})."); return 1; }

    // Verify signature.
    var verifier = new Ed25519Signer();
    verifier.Init(false, new Ed25519PublicKeyParameters(pubBytes, 0));
    verifier.BlockUpdate(manifestBytes, 0, manifestBytes.Length);
    if (!verifier.VerifySignature(sigBytes))
    {
        Console.Error.WriteLine("FAIL: signature verification failed.");
        return 1;
    }
    Console.WriteLine("OK: signature valid.");

    // Verify file hashes.
    var manifest = JsonNode.Parse(manifestBytes) as JsonObject;
    var files    = manifest?["files"] as JsonArray;
    if (files is null)
    {
        Console.WriteLine("No files array in manifest — nothing to hash-check.");
        return 0;
    }

    var ok = true;
    foreach (var entry in files)
    {
        var rel      = entry!["path"]!.GetValue<string>();
        var expected = entry["sha256"]!.GetValue<string>();
        var abs      = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(abs))
        {
            Console.Error.WriteLine($"FAIL: missing file {rel}");
            ok = false;
            continue;
        }
        var actual = ComputeSha256Hex(abs);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"FAIL: hash mismatch for {rel}");
            Console.Error.WriteLine($"  expected: {expected}");
            Console.Error.WriteLine($"  actual:   {actual}");
            ok = false;
        }
        else
        {
            Console.WriteLine($"OK: {rel}");
        }
    }
    return ok ? 0 : 1;
}

// ── helpers ───────────────────────────────────────────────────────────────────

static string ComputeSha256Hex(string path)
{
    using var sha    = SHA256.Create();
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
}

static int PrintUsage()
{
    Console.WriteLine("""
        mla-plugin-signer — Plugin signing utility for MyLocalAssistant

        Commands:
          generate-key <keyId>
              Generate a new ed25519 keypair. Creates <keyId>.key (private seed,
              keep secret) and <keyId>.pub (public key, copy to trusted-keys/).

          sign <pluginFolder> <keyFile>
              Hashes all plugin files, updates manifest.json with hashes, and
              writes manifest.json.sig. Run after publishing the plugin binary.

          verify <pluginFolder> <pubFile>
              Verifies manifest signature and all file hashes. Exits 0 on success.

        Example:
          dotnet run --project tools/PluginSigner -- generate-key bundled-plugins
          dotnet run --project tools/PluginSigner -- sign out/plugins/web.search bundled-plugins.key
          dotnet run --project tools/PluginSigner -- verify out/plugins/web.search bundled-plugins.pub
        """);
    return 1;
}

