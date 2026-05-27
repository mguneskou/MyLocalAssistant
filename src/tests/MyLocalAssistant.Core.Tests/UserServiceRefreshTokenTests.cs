using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyLocalAssistant.Server.Auth;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Core.Tests;

public class UserServiceRefreshTokenTests
{
    [Fact]
    public async Task Refresh_token_can_be_used_once_and_reuse_fails()
    {
        using var db = NewDb();
        var settings = NewSettings();
        var jwt = new JwtIssuer(settings);
        var svc = NewUserService(db, jwt, settings);

        var user = new User
        {
            Username = "alice",
            DisplayName = "Alice",
            PasswordHash = Pbkdf2Hasher.Hash("Password123!"),
            AuthSource = UserService.AuthSourceLocal,
        };
        db.Users.Add(user);

        var (plain, hash, exp) = jwt.IssueRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = hash,
            ExpiresAt = exp,
        });
        await db.SaveChangesAsync();

        var (first, firstCode) = await svc.RefreshAsync(plain, CancellationToken.None);
        Assert.Null(firstCode);
        Assert.NotNull(first);
        Assert.NotEqual(plain, first!.RefreshToken);
        Assert.False(string.IsNullOrWhiteSpace(first.AccessToken));

        var oldToken = await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
        Assert.NotNull(oldToken.RevokedAt);

        var (reuse, reuseCode) = await svc.RefreshAsync(plain, CancellationToken.None);
        Assert.Null(reuse);
        Assert.Equal(ProblemCodes.TokenExpired, reuseCode);

        var rotatedHash = JwtIssuer.HashRefreshToken(first.RefreshToken);
        var activeRotated = await db.RefreshTokens.AnyAsync(t => t.TokenHash == rotatedHash && t.RevokedAt == null);
        Assert.True(activeRotated);
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("refresh-tests-" + Guid.NewGuid())
            .Options);

    private static ServerSettings NewSettings() => new()
    {
        JwtSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
        JwtIssuer = "tests",
        JwtAudience = "tests",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 14,
    };

    private static UserService NewUserService(AppDbContext db, JwtIssuer jwt, ServerSettings settings)
    {
        var ldap = new LdapIdentityProvider(settings, NullLogger<LdapIdentityProvider>.Instance);
        return new UserService(db, jwt, settings, ldap, NullLogger<UserService>.Instance);
    }
}
