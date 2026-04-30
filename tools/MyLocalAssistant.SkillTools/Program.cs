using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyLocalAssistant.Shared.Plugins;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

// SkillTools — developer CLI for the MyLocalAssistant plug-in pipeline.
// Usage:
//   SkillTools keygen --pub <out.pub> --priv <out.key>
//   SkillTools hash <plugin-folder>                                # rewrites manifest.json file hashes
//   SkillTools sign <manifest.json> --key <priv.key>               # writes manifest.json.sig
//   SkillTools verify <manifest.json> --key <pub.pub>              # exit 0 if good
//
// Keys are stored as bare base64 (32 raw bytes for ed25519); compatible with the
// trust store loader at <install>/config/trusted-keys/<keyId>.pub.

if (args.Length == 0) { PrintUsage(); return 1; }

try
{
    return args[0] switch
    {
        "keygen" => Keygen(args.AsSpan(1)),
        "hash"   => HashFiles(args.AsSpan(1)),
        "sign"   => Sign(args.AsSpan(1)),
        "verify" => Verify(args.AsSpan(1)),
        "pack"   => Pack(args.AsSpan(1)),
        "install" => Install(args.AsSpan(1)),
        _ => UnknownCommand(args[0]),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

static int Keygen(ReadOnlySpan<string> a)
{
    var pubPath = TakeOpt(a, "--pub") ?? throw new ArgumentException("--pub <path> required");
    var privPath = TakeOpt(a, "--priv") ?? throw new ArgumentException("--priv <path> required");
    var random = new SecureRandom();
    var gen = new Ed25519KeyPairGenerator();
    gen.Init(new Ed25519KeyGenerationParameters(random));
    var kp = gen.GenerateKeyPair();
    var pub = ((Ed25519PublicKeyParameters)kp.Public).GetEncoded();
    var priv = ((Ed25519PrivateKeyParameters)kp.Private).GetEncoded();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pubPath))!);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privPath))!);
    File.WriteAllText(pubPath, Convert.ToBase64String(pub));
    File.WriteAllText(privPath, Convert.ToBase64String(priv));
    Console.WriteLine($"Wrote public key  -> {pubPath}");
    Console.WriteLine($"Wrote private key -> {privPath}");
    Console.WriteLine("KeyId is the public key's filename without extension; copy the .pub file into <install>/config/trusted-keys/.");
    return 0;
}

static int HashFiles(ReadOnlySpan<string> a)
{
    if (a.Length < 1) throw new ArgumentException("usage: hash <plugin-folder>");
    var folder = Path.GetFullPath(a[0]);
    var manifestPath = Path.Combine(folder, "manifest.json");
    if (!File.Exists(manifestPath)) throw new FileNotFoundException("manifest.json not found", manifestPath);
    var bytes = File.ReadAllBytes(manifestPath);
    var manifest = JsonSerializer.Deserialize<SkillManifest>(bytes, JsonRpcFraming.Json)
        ?? throw new InvalidDataException("manifest.json is not a valid SkillManifest.");
    foreach (var f in manifest.Files)
    {
        var path = Path.Combine(folder, f.Path);
        if (!File.Exists(path)) throw new FileNotFoundException($"manifest references missing file '{f.Path}'.");
        f.Sha256 = Sha256Hex(path);
        Console.WriteLine($"{f.Path,-40} {f.Sha256}");
    }
    var json = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonRpcFraming.Json) { WriteIndented = true });
    File.WriteAllBytes(manifestPath, json);
    Console.WriteLine($"Updated {manifestPath}.");
    return 0;
}

static int Sign(ReadOnlySpan<string> a)
{
    if (a.Length < 1) throw new ArgumentException("usage: sign <manifest.json> --key <priv.key>");
    var manifestPath = Path.GetFullPath(a[0]);
    var keyPath = TakeOpt(a[1..], "--key") ?? throw new ArgumentException("--key <path> required");
    var manifestBytes = File.ReadAllBytes(manifestPath);
    var priv = LoadPrivateKey(keyPath);
    var signer = new Ed25519Signer();
    signer.Init(true, priv);
    signer.BlockUpdate(manifestBytes, 0, manifestBytes.Length);
    var sig = signer.GenerateSignature();
    var sigPath = manifestPath + ".sig";
    File.WriteAllText(sigPath, Convert.ToBase64String(sig));
    Console.WriteLine($"Wrote signature ({sig.Length} bytes) -> {sigPath}");
    return 0;
}

static int Verify(ReadOnlySpan<string> a)
{
    if (a.Length < 1) throw new ArgumentException("usage: verify <manifest.json> --key <pub.pub>");
    var manifestPath = Path.GetFullPath(a[0]);
    var keyPath = TakeOpt(a[1..], "--key") ?? throw new ArgumentException("--key <path> required");
    var manifestBytes = File.ReadAllBytes(manifestPath);
    var sigBytes = Convert.FromBase64String(File.ReadAllText(manifestPath + ".sig").Trim());
    var pub = new Ed25519PublicKeyParameters(Convert.FromBase64String(File.ReadAllText(keyPath).Trim()), 0);
    var verifier = new Ed25519Signer();
    verifier.Init(false, pub);
    verifier.BlockUpdate(manifestBytes, 0, manifestBytes.Length);
    if (verifier.VerifySignature(sigBytes)) { Console.WriteLine("OK: signature valid."); return 0; }
    Console.Error.WriteLine("BAD: signature does not match."); return 2;
}

static Ed25519PrivateKeyParameters LoadPrivateKey(string path)
{
    var raw = Convert.FromBase64String(File.ReadAllText(path).Trim());
    if (raw.Length != 32) throw new InvalidDataException($"Private key must be 32 bytes (got {raw.Length}).");
    return new Ed25519PrivateKeyParameters(raw, 0);
}

static string Sha256Hex(string path)
{
    using var sha = SHA256.Create();
    using var fs = File.OpenRead(path);
    return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
}

static string? TakeOpt(ReadOnlySpan<string> a, string name)
{
    for (var i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
    return null;
}

static int UnknownCommand(string cmd) { Console.Error.WriteLine($"Unknown command: {cmd}"); PrintUsage(); return 1; }

static void PrintUsage()
{
    Console.WriteLine("SkillTools — MyLocalAssistant plug-in developer CLI");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  keygen  --pub <out.pub> --priv <out.key>");
    Console.WriteLine("  hash    <plugin-folder>");
    Console.WriteLine("  sign    <manifest.json> --key <priv.key>");
    Console.WriteLine("  verify  <manifest.json> --key <pub.pub>");
    Console.WriteLine("  pack    <plugin-folder> --out <pkg.mlaplugin>");
    Console.WriteLine("  install <pkg.mlaplugin> --to <install-dir>");
}

static int Pack(ReadOnlySpan<string> a)
{
    if (a.Length < 1) throw new ArgumentException("usage: pack <plugin-folder> --out <pkg>");
    var folder = Path.GetFullPath(a[0]);
    var outPath = TakeOpt(a[1..], "--out") ?? throw new ArgumentException("--out <pkg> required");
    var manifestPath = Path.Combine(folder, "manifest.json");
    var sigPath = manifestPath + ".sig";
    if (!File.Exists(manifestPath)) throw new FileNotFoundException("manifest.json not found", manifestPath);
    if (!File.Exists(sigPath)) throw new FileNotFoundException("manifest.json.sig not found — run `sign` first.", sigPath);

    var bytes = File.ReadAllBytes(manifestPath);
    var manifest = JsonSerializer.Deserialize<SkillManifest>(bytes, JsonRpcFraming.Json)
        ?? throw new InvalidDataException("manifest.json is not a valid SkillManifest.");

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    if (File.Exists(outPath)) File.Delete(outPath);

    using var zip = ZipFile.Open(outPath, ZipArchiveMode.Create);
    AddEntry(zip, folder, "manifest.json");
    AddEntry(zip, folder, "manifest.json.sig");
    foreach (var f in manifest.Files)
    {
        var src = Path.Combine(folder, f.Path);
        if (!File.Exists(src)) throw new FileNotFoundException($"manifest references missing file '{f.Path}'.");
        zip.CreateEntryFromFile(src, NormalizePath(f.Path), CompressionLevel.Optimal);
    }
    Console.WriteLine($"Packed plug-in '{manifest.Id}' v{manifest.Version} → {outPath}");
    return 0;
}

static void AddEntry(ZipArchive zip, string folder, string name)
{
    var src = Path.Combine(folder, name);
    if (File.Exists(src)) zip.CreateEntryFromFile(src, NormalizePath(name), CompressionLevel.Optimal);
}

static string NormalizePath(string p) => p.Replace('\\', '/');

static int Install(ReadOnlySpan<string> a)
{
    if (a.Length < 1) throw new ArgumentException("usage: install <pkg.mlaplugin> --to <install-dir>");
    var pkg = Path.GetFullPath(a[0]);
    var installDir = TakeOpt(a[1..], "--to") ?? throw new ArgumentException("--to <install-dir> required");
    if (!File.Exists(pkg)) throw new FileNotFoundException("package not found", pkg);

    using var zip = ZipFile.OpenRead(pkg);
    var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("package missing manifest.json");
    SkillManifest manifest;
    using (var s = manifestEntry.Open())
    using (var ms = new MemoryStream())
    {
        s.CopyTo(ms);
        manifest = JsonSerializer.Deserialize<SkillManifest>(ms.ToArray(), JsonRpcFraming.Json)
            ?? throw new InvalidDataException("manifest.json invalid.");
    }
    if (string.IsNullOrWhiteSpace(manifest.Id)) throw new InvalidDataException("manifest.id is empty.");

    var pluginsRoot = Path.Combine(installDir, "plugins");
    var target = Path.Combine(pluginsRoot, manifest.Id);
    Directory.CreateDirectory(target);
    // Replace contents (simple upgrade): wipe target before extract.
    foreach (var existing in Directory.EnumerateFileSystemEntries(target))
    {
        if (Directory.Exists(existing)) Directory.Delete(existing, recursive: true);
        else File.Delete(existing);
    }
    foreach (var e in zip.Entries)
    {
        if (string.IsNullOrEmpty(e.Name)) continue; // directory entry
        var dest = Path.GetFullPath(Path.Combine(target, e.FullName));
        if (!dest.StartsWith(target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Refusing zip-slip path '{e.FullName}'.");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        e.ExtractToFile(dest, overwrite: true);
    }
    Console.WriteLine($"Installed plug-in '{manifest.Id}' v{manifest.Version} → {target}");
    Console.WriteLine("Restart the server (or use Admin → Skills → Reload) to load it.");
    return 0;
}
