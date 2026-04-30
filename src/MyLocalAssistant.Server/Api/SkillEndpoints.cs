using MyLocalAssistant.Server.Skills;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class SkillEndpoints
{
    public static IEndpointRouteBuilder MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/skills")
            .WithTags("Admin/Skills")
            .RequireAuthorization("Admin");

        // Listing skills is fine for any admin (it shows what's installed); editing is owner-only.
        admin.MapGet("/", (SkillRegistry registry) => Results.Ok(registry.List()));

        admin.MapGet("/{id}", (string id, SkillRegistry registry) =>
        {
            var dto = registry.List().FirstOrDefault(s =>
                string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        admin.MapPatch("/{id}", async (string id, SkillUpdateRequest req, SkillRegistry registry, CancellationToken ct) =>
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

        return app;
    }
}
