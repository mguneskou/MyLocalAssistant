using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using MyLocalAssistant.Server.Auth;
using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class ModelEndpoints
{
    public static IEndpointRouteBuilder MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/models")
            .WithTags("Admin/Models")
            .RequireAuthorization("Admin");

        group.MapGet("/", (ClaimsPrincipal user, ModelManager mgr) =>
            Results.Ok(mgr.List(isGlobalAdmin: user.HasClaim(JwtIssuer.ClaimIsGlobalAdmin, "1"))));

        group.MapGet("/status", (ModelManager mgr) => Results.Ok(mgr.GetStatus()));

        group.MapPost("/{id}/download", (string id, ModelManager mgr) =>
        {
            try { return Results.Accepted($"/api/admin/models/{id}/download", mgr.StartDownload(id)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        group.MapGet("/{id}/download", (string id, ModelManager mgr) =>
        {
            var list = mgr.List();
            var m = list.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (m is null) return Results.NotFound();
            return Results.Ok(m.Download);
        });

        group.MapDelete("/{id}/download", (string id, ModelManager mgr) =>
            mgr.CancelDownload(id) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/{id}/activate", (string id, ModelManager mgr) =>
        {
            try { return Results.Accepted($"/api/admin/models/status", mgr.Activate(id)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "validation_failed");
            }
        });

        group.MapGet("/embedding/status", (EmbeddingService es) => Results.Ok(es.GetStatus()));

        group.MapPost("/embedding/{id}/activate", (string id, EmbeddingService es) =>
        {
            try { return Results.Accepted("/api/admin/models/embedding/status", es.Activate(id)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "validation_failed");
            }
        });

        group.MapDelete("/{id}", (string id, ModelManager mgr) =>
        {
            try { return mgr.DeleteModel(id) ? Results.NoContent() : Results.NotFound(); }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict, title: "conflict");
            }
        });

        return app;
    }
}
