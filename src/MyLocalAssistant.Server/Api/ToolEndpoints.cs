using MyLocalAssistant.Server.Tools;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class ToolEndpoints
{
    public static IEndpointRouteBuilder MapToolEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/tools")
            .WithTags("Admin/Tools")
            .RequireAuthorization("Admin");

        // Listing skills is fine for any admin (it shows what's installed); editing is owner-only.
        admin.MapGet("/", (ToolRegistry registry) => Results.Ok(registry.List()));

        admin.MapGet("/{id}", (string id, ToolRegistry registry) =>
        {
            var dto = registry.List().FirstOrDefault(s =>
                string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        admin.MapPatch("/{id}", async (string id, ToolUpdateRequest req, ToolRegistry registry, CancellationToken ct) =>
        {
            try
            {
                var dto = await registry.UpdateAsync(id, req, ct);
                return Results.Ok(dto);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.Problem(title: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(title: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).RequireAuthorization("GlobalAdmin");

        // Lightweight in-memory tool-call counters (since server start or last reset).
        admin.MapGet("/stats", (ToolCallStats stats) => Results.Ok(stats.Snapshot()));
        admin.MapPost("/stats/reset", (ToolCallStats stats) =>
        {
            stats.Reset();
            return Results.NoContent();
        }).RequireAuthorization("GlobalAdmin");

        return app;
    }
}
