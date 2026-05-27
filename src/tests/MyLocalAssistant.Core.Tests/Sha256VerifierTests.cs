using MyLocalAssistant.Core.Download;

namespace MyLocalAssistant.Core.Tests;

public class Sha256VerifierTests
{
    [Fact]
    public async Task ComputeAsync_KnownVector_MatchesExpected()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "abc");
            var hash = await Sha256Verifier.ComputeAsync(path);
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifyAsync_EmptyExpected_ReturnsTrue()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "abc");
            Assert.True(await Sha256Verifier.VerifyAsync(path, ""));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifyAsync_Mismatch_ReturnsFalse()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "abc");
            Assert.False(await Sha256Verifier.VerifyAsync(path, new string('0', 64)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
