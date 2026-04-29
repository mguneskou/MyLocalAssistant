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

        group.MapPost("/", async ([FromBody] CreateUserRequest req, UserService svc, CancellationToken ct) =>
        {
            var (user, code) = await svc.CreateUserAsync(req, ct);
            if (user is not null) return Results.Created($"/api/admin/users/{user.Id}", user);
            return code switch
            {
                ProblemCodes.Conflict => Problem(code, "A user with that username already exists.", StatusCodes.Status409Conflict),
                _ => Problem(code!, "Validation failed.", StatusCodes.Status400BadRequest),
            };
        });

        group.MapPatch("/{id:guid}", async (Guid id, [FromBody] UpdateUserRequest req, UserService svc, CancellationToken ct) =>
        {
            var (user, code) = await svc.UpdateUserAsync(id, req, ct);
            if (user is not null) return Results.Ok(user);
            return code == ProblemCodes.NotFound
                ? Problem(code, "User not found.", StatusCodes.Status404NotFound)
                : Problem(code!, "Validation failed.", StatusCodes.Status400BadRequest);
        });

        group.MapPost("/{id:guid}/reset-password", async (
            Guid id, [FromBody] ResetPasswordRequest req, UserService svc, CancellationToken ct) =>
        {
            var code = await svc.ResetPasswordAsync(id, req.NewPassword, ct);
            return code is null
                ? Results.NoContent()
                : code == ProblemCodes.NotFound
                    ? Problem(code, "User not found.", StatusCodes.Status404NotFound)
                    : Problem(code, "New password must be at least 8 characters.", StatusCodes.Status400BadRequest);
        });

        group.MapDelete("/{id:guid}", async (
            Guid id, ClaimsPrincipal principal, UserService svc, CancellationToken ct) =>
        {
            // Prevent admins from deleting themselves out of the system.
            var sub = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                      ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(sub, out var selfId) && selfId == id)
                return Problem(ProblemCodes.ValidationFailed, "You cannot delete your own account.", StatusCodes.Status400BadRequest);

            var code = await svc.DeleteUserAsync(id, ct);
            return code is null
                ? Results.NoContent()
                : Problem(code, "User not found.", StatusCodes.Status404NotFound);
        });

        return app;
    }

    private static IResult Problem(string code, string detail, int status) =>
        Results.Problem(detail: detail, statusCode: status, title: code, type: code);
}
