using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using MyLocalAssistant.Server.Auth;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", async ([FromBody] LoginRequest req, UserService svc, CancellationToken ct) =>
        {
            var (resp, code) = await svc.LoginAsync(req, ct);
            if (resp is not null)
            {
                if (resp.User.MustChangePassword)
                {
                    // Login still succeeds — client is expected to call /change-password before continuing.
                }
                return Results.Ok(resp);
            }
            return Problem(code!, "Invalid credentials.", StatusCodes.Status401Unauthorized);
        });

        group.MapPost("/refresh", async ([FromBody] RefreshRequest req, UserService svc, CancellationToken ct) =>
        {
            var (resp, code) = await svc.RefreshAsync(req.RefreshToken, ct);
            if (resp is not null) return Results.Ok(resp);
            return Problem(code!, "Refresh token invalid or expired.", StatusCodes.Status401Unauthorized);
        });

        group.MapPost("/change-password", async (
            [FromBody] ChangePasswordRequest req,
            ClaimsPrincipal principal,
            UserService svc,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            // Try both raw "sub" and the mapped NameIdentifier claim — depending on
            // JwtSecurityTokenHandler.DefaultInboundClaimTypeMap state either may be present.
            var sub = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                      ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(sub, out var userId))
            {
                var log = lf.CreateLogger("AuthEndpoints");
                log.LogWarning("change-password: could not resolve user id. IsAuthenticated={Auth}, Claims=[{Claims}]",
                    principal.Identity?.IsAuthenticated,
                    string.Join(", ", principal.Claims.Select(c => $"{c.Type}={c.Value}")));
                return Problem(ProblemCodes.Forbidden, "Not authenticated.", StatusCodes.Status401Unauthorized);
            }

            var code = await svc.ChangePasswordAsync(userId, req, ct);
            if (code is null) return Results.NoContent();
            return code switch
            {
                ProblemCodes.InvalidCredentials => Problem(code, "Current password is incorrect.", StatusCodes.Status400BadRequest),
                ProblemCodes.ValidationFailed => Problem(code, "New password must be at least 8 characters.", StatusCodes.Status400BadRequest),
                ProblemCodes.Forbidden => Problem(code, "This account is managed in your directory; change your password there.", StatusCodes.Status403Forbidden),
                _ => Problem(code, "Not found.", StatusCodes.Status404NotFound),
            };
        }).RequireAuthorization();

        group.MapGet("/me", async (ClaimsPrincipal principal, UserService svc, CancellationToken ct) =>
        {
            var sub = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                      ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(sub, out var userId)) return Results.Unauthorized();
            var dto = await svc.GetProfileAsync(userId, ct);
            if (dto is null) return Results.Unauthorized();
            return Results.Ok(dto);
        }).RequireAuthorization();

        group.MapPatch("/me", async (
            [FromBody] UpdateWorkRootRequest req,
            ClaimsPrincipal principal,
            UserService svc,
            CancellationToken ct) =>
        {
            var sub = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                      ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(sub, out var userId)) return Results.Unauthorized();
            var code = await svc.UpdateWorkRootAsync(userId, req.WorkRoot, ct);
            if (code is null) return Results.NoContent();
            return code switch
            {
                ProblemCodes.ValidationFailed => Problem(code, "Invalid path: must be an absolute path without wildcards or '..'.", StatusCodes.Status400BadRequest),
                _ => Problem(code, "Not found.", StatusCodes.Status404NotFound),
            };
        }).RequireAuthorization();

        return app;
    }

    private static IResult Problem(string code, string detail, int status) =>
        Results.Problem(detail: detail, statusCode: status, title: code, type: code);
}
