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
            .Include(u => u.Departments).ThenInclude(d => d.Department)
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
            .Include(t => t.User).ThenInclude(u => u.Departments).ThenInclude(d => d.Department)
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
        user.Departments.Select(d => d.Department?.Name ?? "").Where(n => n.Length > 0).ToList(),
        user.Roles.Select(r => r.Role?.Name ?? "").Where(n => n.Length > 0).ToList(),
        user.MustChangePassword,
        user.IsAdmin);

    // ---------- Admin operations ----------

    public async Task<List<UserAdminDto>> ListUsersAsync(CancellationToken ct)
    {
        var users = await db.Users
            .Include(u => u.Departments).ThenInclude(d => d.Department)
            .OrderBy(u => u.Username)
            .ToListAsync(ct);
        return users.Select(ToAdminDto).ToList();
    }

    public async Task<(UserAdminDto? User, string? Code)> CreateUserAsync(CreateUserRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.DisplayName))
            return (null, ProblemCodes.ValidationFailed);
        if (string.IsNullOrEmpty(req.Password) || req.Password.Length < 8)
            return (null, ProblemCodes.ValidationFailed);

        var username = req.Username.Trim();
        if (await db.Users.AnyAsync(u => u.Username == username, ct))
            return (null, ProblemCodes.Conflict);

        var user = new User
        {
            Username = username,
            DisplayName = req.DisplayName.Trim(),
            PasswordHash = Pbkdf2Hasher.Hash(req.Password),
            IsAdmin = req.IsAdmin,
            MustChangePassword = true,
        };
        db.Users.Add(user);

        // Admins implicitly have access to everything; ignore any provided list.
        if (!req.IsAdmin && req.Departments is { Count: > 0 })
        {
            await SyncDepartmentsAsync(user, req.Departments, ct);
        }

        await db.SaveChangesAsync(ct);
        await db.Entry(user).Collection(u => u.Departments).LoadAsync(ct);
        foreach (var ud in user.Departments) await db.Entry(ud).Reference(x => x.Department).LoadAsync(ct);
        log.LogInformation("Created user {Username} (admin={IsAdmin})", user.Username, user.IsAdmin);
        return (ToAdminDto(user), null);
    }

    public async Task<(UserAdminDto? User, string? Code)> UpdateUserAsync(Guid userId, UpdateUserRequest req, CancellationToken ct)
    {
        var user = await db.Users
            .Include(u => u.Departments).ThenInclude(d => d.Department)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return (null, ProblemCodes.NotFound);

        if (req.DisplayName is not null)
        {
            if (string.IsNullOrWhiteSpace(req.DisplayName)) return (null, ProblemCodes.ValidationFailed);
            user.DisplayName = req.DisplayName.Trim();
        }
        if (req.IsAdmin is not null) user.IsAdmin = req.IsAdmin.Value;
        if (req.IsDisabled is not null) user.IsDisabled = req.IsDisabled.Value;

        // Clear departments for admins (implicit all-access). Otherwise apply list if provided.
        if (user.IsAdmin)
        {
            user.Departments.Clear();
        }
        else if (req.Departments is not null)
        {
            await SyncDepartmentsAsync(user, req.Departments, ct);
        }

        await db.SaveChangesAsync(ct);
        // Reload after sync so projection is fresh.
        await db.Entry(user).Collection(u => u.Departments).Query()
            .Include(d => d.Department).LoadAsync(ct);
        return (ToAdminDto(user), null);
    }

    public async Task<string?> ResetPasswordAsync(Guid userId, string newPassword, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 8)
            return ProblemCodes.ValidationFailed;
        var user = await db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null) return ProblemCodes.NotFound;
        user.PasswordHash = Pbkdf2Hasher.Hash(newPassword);
        user.MustChangePassword = true;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Admin reset password for user {Username}", user.Username);
        return null;
    }

    public async Task<string?> DeleteUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null) return ProblemCodes.NotFound;
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        log.LogWarning("Deleted user {Username}", user.Username);
        return null;
    }

    /// <summary>
    /// Replaces the user's department membership to match <paramref name="departmentNames"/>.
    /// Unknown names are ignored.
    /// </summary>
    private async Task SyncDepartmentsAsync(User user, IReadOnlyList<string> departmentNames, CancellationToken ct)
    {
        var wanted = departmentNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var depts = await db.Departments
            .Where(d => wanted.Contains(d.Name))
            .ToListAsync(ct);

        var wantedIds = depts.Select(d => d.Id).ToHashSet();
        // Remove memberships not in wanted set
        user.Departments.RemoveAll(ud => !wantedIds.Contains(ud.DepartmentId));
        // Add missing
        var existingIds = user.Departments.Select(ud => ud.DepartmentId).ToHashSet();
        foreach (var d in depts)
        {
            if (!existingIds.Contains(d.Id))
                user.Departments.Add(new UserDepartment { UserId = user.Id, DepartmentId = d.Id });
        }
    }

    private static UserAdminDto ToAdminDto(User u) => new(
        u.Id, u.Username, u.DisplayName,
        u.Departments.Select(d => d.Department?.Name ?? "").Where(n => n.Length > 0).OrderBy(n => n).ToList(),
        u.IsAdmin, u.IsDisabled, u.MustChangePassword,
        u.CreatedAt, u.LastLoginAt);
}
