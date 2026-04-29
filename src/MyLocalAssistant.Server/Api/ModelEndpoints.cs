using Microsoft.AspNetCore.Mvc;
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

        group.MapGet("/", (ModelManager mgr) => Results.Ok(mgr.List()));

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
