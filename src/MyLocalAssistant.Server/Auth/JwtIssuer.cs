using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Server.Persistence;

namespace MyLocalAssistant.Server.Auth;

public sealed class JwtIssuer(ServerSettings settings)
{
    public const string ClaimDepartment = "dept";
    public const string ClaimIsAdmin = "adm";

    private readonly SymmetricSecurityKey _key = new(Convert.FromBase64String(settings.JwtSigningKey));

    public (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(User user)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(settings.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, user.Username),
            new(ClaimTypes.Name, user.Username),
        };
        if (user.IsAdmin) claims.Add(new Claim(ClaimIsAdmin, "1"));
        // Emit one "dept" claim per department. Admins implicitly have access to all (not enumerated).
        foreach (var ud in user.Departments)
        {
            if (ud.Department is not null)
                claims.Add(new Claim(ClaimDepartment, ud.Department.Name));
        }
        foreach (var ur in user.Roles)
        {
            if (ur.Role is not null) claims.Add(new Claim(ClaimTypes.Role, ur.Role.Name));
        }

        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: settings.JwtIssuer,
            audience: settings.JwtAudience,
            claims: claims,
            expires: expires.UtcDateTime,
            signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }

    public (string PlainToken, string TokenHash, DateTimeOffset ExpiresAt) IssueRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        var plain = Convert.ToBase64String(bytes);
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(plain)));
        return (plain, hash, DateTimeOffset.UtcNow.AddDays(settings.RefreshTokenDays));
    }

    public static string HashRefreshToken(string plainToken)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(plainToken)));

    public TokenValidationParameters GetValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = settings.JwtIssuer,
        ValidateAudience = true,
        ValidAudience = settings.JwtAudience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        IssuerSigningKey = _key,
        ValidateIssuerSigningKey = true,
    };
}
