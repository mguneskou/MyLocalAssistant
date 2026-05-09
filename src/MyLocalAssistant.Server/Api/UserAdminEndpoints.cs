using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using MyLocalAssistant.Server.Auth;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class UserAdminEndpoints
{
    public static IEndpointRouteBuilder MapUserAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users")
            .WithTags("Admin/Users")
            .RequireAuthorization("Admin");

        group.MapGet("/", async (UserService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListUsersAsync(ct)));

        group.MapPost("/", async (HttpContext http, [FromBody] CreateUserRequest req, UserService svc, AuditWriter audit, CancellationToken ct) =>
        {
            var (actorId, actorName) = GetActor(http);
            var (user, code) = await svc.CreateUserAsync(req, ct);
            if (user is not null)
            {
                await audit.WriteAsync("admin.user.create", actorId, actorName, success: true,
                    detail: $"created user '{req.Username}'", ipAddress: http.Connection.RemoteIpAddress?.ToString(),
                    isAdminAction: true, ct: CancellationToken.None);
                return Results.Created($"/api/admin/users/{user.Id}", user);
            }
            await audit.WriteAsync("admin.user.create", actorId, actorName, success: false,
                detail: $"failed to create '{req.Username}': {code}", ipAddress: http.Connection.RemoteIpAddress?.ToString(),
                isAdminAction: true, ct: CancellationToken.None);
            return code switch
            {
                ProblemCodes.Conflict => Problem(code, "A user with that username already exists.", StatusCodes.Status409Conflict),
                _ => Problem(code!, "Validation failed.", StatusCodes.Status400BadRequest),
            };
        });

        group.MapPatch("/{id:guid}", async (HttpContext http, Guid id, [FromBody] UpdateUserRequest req, UserService svc, AuditWriter audit, CancellationToken ct) =>
        {
            var (actorId, actorName) = GetActor(http);
            var (user, code) = await svc.UpdateUserAsync(id, req, ct);
            if (user is not null)
            {
                await audit.WriteAsync("admin.user.update", actorId, actorName, success: true,
                    detail: $"updated user id={id}", ipAddress: http.Connection.RemoteIpAddress?.ToString(),
                    isAdminAction: true, ct: CancellationToken.None);
                return Results.Ok(user);
            }
            return code == ProblemCodes.NotFound
                ? Problem(code, "User not found.", StatusCodes.Status404NotFound)
                : Problem(code!, "Validation failed.", StatusCodes.Status400BadRequest);
        });

        group.MapPost("/{id:guid}/reset-password", async (
            HttpContext http, Guid id, [FromBody] ResetPasswordRequest req, UserService svc, AuditWriter audit, CancellationToken ct) =>
        {
            var (actorId, actorName) = GetActor(http);
            var code = await svc.ResetPasswordAsync(id, req.NewPassword, ct);
            var ok = code is null;
            await audit.WriteAsync("admin.user.reset-password", actorId, actorName, success: ok,
                detail: ok ? $"reset password for user id={id}" : $"failed: {code}",
                ipAddress: http.Connection.RemoteIpAddress?.ToString(),
                isAdminAction: true, ct: CancellationToken.None);
            return ok
                ? Results.NoContent()
                : code == ProblemCodes.NotFound
                    ? Problem(code, "User not found.", StatusCodes.Status404NotFound)
                    : Problem(code, "New password must be at least 8 characters.", StatusCodes.Status400BadRequest);
        });

        group.MapDelete("/{id:guid}", async (
            HttpContext http, Guid id, ClaimsPrincipal principal, UserService svc, AuditWriter audit, CancellationToken ct) =>
        {
            var (actorId, actorName) = GetActor(http);
            // Prevent admins from deleting themselves out of the system.
            var sub = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                      ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(sub, out var selfId) && selfId == id)
                return Problem(ProblemCodes.ValidationFailed, "You cannot delete your own account.", StatusCodes.Status400BadRequest);

            var code = await svc.DeleteUserAsync(id, ct);
            var ok = code is null;
            await audit.WriteAsync("admin.user.delete", actorId, actorName, success: ok,
                detail: ok ? $"deleted user id={id}" : $"failed: {code}",
                ipAddress: http.Connection.RemoteIpAddress?.ToString(),
                isAdminAction: true, ct: CancellationToken.None);
            return ok
                ? Results.NoContent()
                : Problem(code, "User not found.", StatusCodes.Status404NotFound);
        });

        return app;
    }

    private static (Guid? id, string? name) GetActor(HttpContext http)
    {
        var sub = http.User.FindFirstValue("sub");
        Guid.TryParse(sub, out var id);
        var name = http.User.FindFirstValue("name");
        return (id == Guid.Empty ? null : id, name);
    }

    private static IResult Problem(string code, string detail, int status) =>
        Results.Problem(detail: detail, statusCode: status, title: code, type: code);
}
