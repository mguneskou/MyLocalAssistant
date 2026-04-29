using MyLocalAssistant.Server.Auth;

namespace MyLocalAssistant.Server.Api;

public static class DepartmentEndpoints
{
    public static IEndpointRouteBuilder MapDepartmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/departments")
            .WithTags("Admin/Departments")
            .RequireAuthorization("Admin");

        // Departments are seeded on server startup and are read-only via the API.
        group.MapGet("/", async (DepartmentService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(ct)));

        return app;
    }
}
