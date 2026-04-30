using System.Text;
using System.Text.Json;
using MyLocalAssistant.Server.Skills;
using MyLocalAssistant.Server.Skills.Plugin;
using MyLocalAssistant.Shared.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace MyLocalAssistant.Core.Tests;

public class JsonRpcFramingTests
{
    [Fact]
    public async Task RoundtripsRequestAndResponse()
    {
        await using var ms = new MemoryStream();
        var req = new RpcRequest { Id = 7, Method = "invoke" };
        await JsonRpcFraming.WriteFrameAsync(ms, req, CancellationToken.None);
        ms.Position = 0;

        var bytes = await JsonRpcFraming.ReadFrameAsync(ms, CancellationToken.None);
        Assert.NotNull(bytes);
        var parsed = JsonSerializer.Deserialize<RpcRequest>(bytes!, JsonRpcFraming.Json)!;
        Assert.Equal(7, parsed.Id);
        Assert.Equal("invoke", parsed.Method);
    }

    [Fact]
    public async Task ReturnsNullOnCleanEof()
    {
        await using var ms = new MemoryStream();
        var bytes = await JsonRpcFraming.ReadFrameAsync(ms, CancellationToken.None);
        Assert.Null(bytes);
    }

    [Fact]
    public async Task ThrowsOnTruncatedPayload()
    {
        var partial = Encoding.ASCII.GetBytes("Content-Length: 50\r\n\r\n{\"id\":1}");
        await using var ms = new MemoryStream(partial);
        await Assert.ThrowsAsync<EndOfStreamException>(() => JsonRpcFraming.ReadFrameAsync(ms, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsMissingContentLength()
    {
        var bad = Encoding.ASCII.GetBytes("X-Junk: 1\r\n\r\n{}");
        await using var ms = new MemoryStream(bad);
        await Assert.ThrowsAsync<InvalidDataException>(() => JsonRpcFraming.ReadFrameAsync(ms, CancellationToken.None));
    }
}

public class PluginSignatureVerifierTests : IDisposable
{
    private readonly string _trustDir;
    private readonly Ed25519PrivateKeyParameters _priv;
    private readonly Ed25519PublicKeyParameters _pub;
    private readonly string _origCwd;

    public PluginSignatureVerifierTests()
    {
        _trustDir = Path.Combine(Path.GetTempPath(), "mla-tests-" + Guid.NewGuid().ToString("N"));
        // ServerPaths.TrustedKeysDirectory is fixed at AppContext.BaseDirectory/config/trusted-keys.
        // Set up a per-test sandbox by ChDir'ing isn't reliable for static init; instead we exercise
        // the verifier's Verify method directly with a manually-loaded key dictionary.
        var random = new SecureRandom();
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(random));
        var kp = gen.GenerateKeyPair();
        _priv = (Ed25519PrivateKeyParameters)kp.Private;
        _pub = (Ed25519PublicKeyParameters)kp.Public;
        _origCwd = Environment.CurrentDirectory;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _origCwd;
        try { if (Directory.Exists(_trustDir)) Directory.Delete(_trustDir, recursive: true); } catch { }
    }

    [Fact]
    public void VerifierAcceptsGoodSignatureAndRejectsTampered()
    {
        // Stage a trust store at <ServerPaths.TrustedKeysDirectory> = AppContext.BaseDirectory/config/trusted-keys.
        var dir = Path.Combine(AppContext.BaseDirectory, "config", "trusted-keys");
        Directory.CreateDirectory(dir);
        var keyId = "test-" + Guid.NewGuid().ToString("N");
        var pubPath = Path.Combine(dir, keyId + ".pub");
        File.WriteAllText(pubPath, Convert.ToBase64String(_pub.GetEncoded()));
        try
        {
            var verifier = new PluginSignatureVerifier(NullLogger<PluginSignatureVerifier>.Instance);
            Assert.True(verifier.TrustedKeyCount >= 1);

            var msg = Encoding.UTF8.GetBytes("hello");
            var signer = new Ed25519Signer();
            signer.Init(true, _priv);
            signer.BlockUpdate(msg, 0, msg.Length);
            var sig = signer.GenerateSignature();

            Assert.True(verifier.Verify(keyId, msg, sig));
            // Tampered message
            var tampered = Encoding.UTF8.GetBytes("hellO");
            Assert.False(verifier.Verify(keyId, tampered, sig));
            // Unknown keyId
            Assert.False(verifier.Verify("nope-" + Guid.NewGuid().ToString("N"), msg, sig));
        }
        finally
        {
            File.Delete(pubPath);
        }
    }

    [Fact]
    public void ComputeSha256HexMatchesKnownValue()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, new byte[] { 0x61, 0x62, 0x63 }); // "abc"
        try
        {
            var hex = PluginSignatureVerifier.ComputeSha256Hex(path);
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hex);
        }
        finally { File.Delete(path); }
    }
}

public class ToolCallStatsTests
{
    [Fact]
    public void RecordsAndAggregates()
    {
        var s = new ToolCallStats();
        s.RecordSuccess("math.eval", "evaluate", 12);
        s.RecordSuccess("math.eval", "evaluate", 8);
        s.RecordError("math.eval", "evaluate", 100);

        var snap = s.Snapshot();
        var row = Assert.Single(snap.Rows);
        Assert.Equal("math.eval", row.SkillId);
        Assert.Equal("evaluate", row.ToolName);
        Assert.Equal(2, row.Successes);
        Assert.Equal(1, row.Errors);
        Assert.Equal(40d, row.AvgMs); // (12+8+100)/3
        Assert.Equal(100d, row.MaxMs);
    }

    [Fact]
    public void ResetClears()
    {
        var s = new ToolCallStats();
        s.RecordSuccess("a", "b", 1);
        s.Reset();
        Assert.Empty(s.Snapshot().Rows);
    }
}
