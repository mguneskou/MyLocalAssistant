using System.Security.Claims;
using MyLocalAssistant.Server.Auth;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        // End-user list (filtered by department + enabled).
        app.MapGet("/api/agents", async (ClaimsPrincipal user, AgentService svc, CancellationToken ct) =>
        {
            var sub = user.FindFirstValue("sub");
            if (sub is null || !Guid.TryParse(sub, out var userId))
                return Results.Unauthorized();
            var isAdmin = user.HasClaim(JwtIssuer.ClaimIsAdmin, "1");
            return Results.Ok(await svc.ListVisibleAsync(userId, isAdmin, ct));
        })
        .WithTags("Agents")
        .RequireAuthorization();

        var admin = app.MapGroup("/api/admin/agents")
            .WithTags("Admin/Agents")
            .RequireAuthorization("Admin");

        admin.MapGet("/", async (AgentService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAllAsync(ct)));

        // Editing agents (enabled flag, model, RAG bindings, system prompt) is reserved for the global admin.
        admin.MapPatch("/{id}", async (HttpContext http, string id, AgentUpdateRequest req, AgentService svc, AuditWriter audit, CancellationToken ct) =>
        {
            var sub = http.User.FindFirstValue("sub");
            Guid.TryParse(sub, out var actorId);
            var actorName = http.User.FindFirstValue("name");
            try
            {
                var dto = await svc.UpdateAsync(id, req, ct);
                await audit.WriteAsync("admin.agent.update", actorId == Guid.Empty ? null : actorId, actorName,
                    success: true, detail: $"updated agent '{id}'",
                    ipAddress: http.Connection.RemoteIpAddress?.ToString(),
                    isAdminAction: true, ct: CancellationToken.None);
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
