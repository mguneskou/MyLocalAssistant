using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class RoleEndpoints
{
    public static IEndpointRouteBuilder MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/roles")
            .WithTags("Admin/Roles")
            .RequireAuthorization("Admin");

        // Roles are seeded; expose them so admins can grant RAG access by role.
        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Roles.OrderBy(r => r.Name).ToListAsync(ct);
            return Results.Ok(rows.Select(r => new RoleDto(r.Id, r.Name)));
        });

        return app;
    }
}
