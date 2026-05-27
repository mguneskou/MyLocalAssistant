using System.Security.Cryptography;
using MyLocalAssistant.Server.Auth;
using Xunit;

namespace MyLocalAssistant.Core.Tests;

public class Pbkdf2HasherTests
{
    [Fact]
    public void Hash_then_Verify_succeeds_for_correct_password()
    {
        var stored = Pbkdf2Hasher.Hash("Correct Horse Battery Staple");
        Assert.True(Pbkdf2Hasher.Verify("Correct Horse Battery Staple", stored));
    }

    [Fact]
    public void Verify_fails_for_wrong_password()
    {
        var stored = Pbkdf2Hasher.Hash("hunter2");
        Assert.False(Pbkdf2Hasher.Verify("hunter3", stored));
    }

    [Fact]
    public void Verify_fails_for_empty_or_malformed_hash()
    {
        Assert.False(Pbkdf2Hasher.Verify("pw", ""));
        Assert.False(Pbkdf2Hasher.Verify("pw", "   "));
        Assert.False(Pbkdf2Hasher.Verify("pw", "not-a-hash"));
        Assert.False(Pbkdf2Hasher.Verify("pw", "bcrypt$sha256$1000$abc$def")); // wrong algo prefix
        Assert.False(Pbkdf2Hasher.Verify("pw", "pbkdf2$sha512$1000$abc$def")); // wrong hash algo
        Assert.False(Pbkdf2Hasher.Verify("pw", "pbkdf2$sha256$notanumber$abc$def"));
        Assert.False(Pbkdf2Hasher.Verify("pw", "pbkdf2$sha256$1000$!!!$def"));  // invalid base64
    }

    /// <summary>
    /// Iteration count is embedded in the stored hash, so credentials hashed by older
    /// builds (which used 210 000 iterations) must continue to verify after the default
    /// is bumped. This is the back-compat contract \u2014 if it ever breaks, every existing
    /// user is locked out on the upgrade.
    /// </summary>
    [Fact]
    public void Verify_accepts_legacy_iteration_count_hashes()
    {
        var legacy = BuildHashWithIterations("legacy-password", iterations: 210_000);
        Assert.True(Pbkdf2Hasher.Verify("legacy-password", legacy));
        Assert.False(Pbkdf2Hasher.Verify("legacy-passwordx", legacy));
    }

    [Fact]
    public void Verify_accepts_unusually_low_iteration_count()
    {
        // Future-proofing: a developer fixture might use a low iter count to keep tests fast.
        var fast = BuildHashWithIterations("fast", iterations: 1_000);
        Assert.True(Pbkdf2Hasher.Verify("fast", fast));
    }

    [Fact]
    public void Hash_produces_unique_output_per_call()
    {
        var a = Pbkdf2Hasher.Hash("same");
        var b = Pbkdf2Hasher.Hash("same");
        Assert.NotEqual(a, b); // distinct salts \u2192 distinct stored strings
        Assert.True(Pbkdf2Hasher.Verify("same", a));
        Assert.True(Pbkdf2Hasher.Verify("same", b));
    }

    [Fact]
    public void Hash_uses_current_default_iteration_count_for_new_hashes()
    {
        // Surface guard: if someone reverts the iteration bump by accident, this test
        // makes the regression obvious. Update the expected value when the default moves.
        const int expectedIterations = 600_000;
        var stored = Pbkdf2Hasher.Hash("anything");
        var parts = stored.Split('$');
        Assert.Equal(5, parts.Length);
        Assert.Equal("pbkdf2", parts[0]);
        Assert.Equal("sha256", parts[1]);
        Assert.Equal(expectedIterations, int.Parse(parts[2]));
    }

    private static string BuildHashWithIterations(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
}
