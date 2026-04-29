using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Auth;

public sealed class UserService(AppDbContext db, JwtIssuer jwt, ILogger<UserService> log)
{
    public const string DefaultAdminUsername = "admin";
    public const string DefaultAdminPassword = "admin";

    /// <summary>
    /// Seeds an initial admin / admin user on an empty Users table.
    /// MustChangePassword = true forces a change on first login.
    /// </summary>
    public async Task EnsureAdminBootstrapAsync(CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(ct)) return;

        var admin = new User
        {
            Username = DefaultAdminUsername,
            DisplayName = "Administrator",
            PasswordHash = Pbkdf2Hasher.Hash(DefaultAdminPassword),
            IsAdmin = true,
            MustChangePassword = true,
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);
        log.LogWarning("Bootstrapped default admin account '{User}' / '{Pwd}' — change on first login.",
            DefaultAdminUsername, DefaultAdminPassword);
    }

    public async Task<(LoginResponse? Response, string? ProblemCode)> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        var user = await db.Users
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .FirstOrDefaultAsync(u => u.Username == req.Username, ct);
        if (user is null || user.IsDisabled || !Pbkdf2Hasher.Verify(req.Password, user.PasswordHash))
        {
            return (null, ProblemCodes.InvalidCredentials);
        }

        var (access, accessExp) = jwt.IssueAccessToken(user);
        var (refreshPlain, refreshHash, refreshExp) = jwt.IssueRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            ExpiresAt = refreshExp,
        });
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return (new LoginResponse(access, refreshPlain, accessExp, ToDto(user)), null);
    }

    public async Task<(LoginResponse? Response, string? ProblemCode)> RefreshAsync(string refreshTokenPlain, CancellationToken ct)
    {
        var hash = JwtIssuer.HashRefreshToken(refreshTokenPlain);
        var token = await db.RefreshTokens
            .Include(t => t.User).ThenInclude(u => u.Roles).ThenInclude(r => r.Role)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.RevokedAt is not null || token.ExpiresAt < DateTimeOffset.UtcNow || token.User.IsDisabled)
        {
            return (null, ProblemCodes.TokenExpired);
        }

        // Rotate: revoke the old, issue a new pair.
        token.RevokedAt = DateTimeOffset.UtcNow;
        var (access, accessExp) = jwt.IssueAccessToken(token.User);
        var (refreshPlain, refreshHash, refreshExp) = jwt.IssueRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = token.UserId,
            TokenHash = refreshHash,
            ExpiresAt = refreshExp,
        });
        await db.SaveChangesAsync(ct);

        return (new LoginResponse(access, refreshPlain, accessExp, ToDto(token.User)), null);
    }

    public async Task<string?> ChangePasswordAsync(Guid userId, ChangePasswordRequest req, CancellationToken ct)
    {
        var user = await db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null) return ProblemCodes.NotFound;
        if (!Pbkdf2Hasher.Verify(req.CurrentPassword, user.PasswordHash))
            return ProblemCodes.InvalidCredentials;
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
            return ProblemCodes.ValidationFailed;

        user.PasswordHash = Pbkdf2Hasher.Hash(req.NewPassword);
        user.MustChangePassword = false;
        await db.SaveChangesAsync(ct);
        return null;
    }

    public static UserDto ToDto(User user) => new(
        user.Id,
        user.Username,
        user.DisplayName,
        user.Department,
        user.Roles.Select(r => r.Role?.Name ?? "").Where(n => n.Length > 0).ToList(),
        user.MustChangePassword,
        user.IsAdmin);
}
