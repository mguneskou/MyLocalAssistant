using MyLocalAssistant.Server.Skills;
using MyLocalAssistant.Server.Tools;

namespace MyLocalAssistant.Server.Api;

/// <summary>
/// REST endpoints for the Hermes-style skill and toolset system.
/// Skills are Hermes-equivalent workflow definitions that inject system prompts + tools.
/// Toolsets are named groups of tools that can be assigned to agents.
/// </summary>
public static class SkillEndpoints
{
    public static IEndpointRouteBuilder MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Skills ────────────────────────────────────────────────────────────
        var skills = app.MapGroup("/api/admin/skills")
            .WithTags("Admin/Skills")
            .RequireAuthorization("Admin");

        skills.MapGet("/", (SkillRegistry registry) =>
            Results.Ok(registry.Manifests().Select(m => new
            {
                m.Id,
                m.Name,
                m.Description,
                m.Category,
                m.Version,
                m.Publisher,
                m.IsBuiltIn,
                m.RequiredToolIds,
                InputCount  = m.Inputs.Count,
                OutputCount = m.Outputs.Count,
            })));

        skills.MapGet("/{id}", (string id, SkillRegistry registry) =>
        {
            var skill = registry.Find(id);
            if (skill is null) return Results.NotFound();
            return Results.Ok(new
            {
                skill.Id,
                skill.Name,
                skill.Description,
                skill.Category,
                skill.SystemPrompt,
                skill.RequiredToolIds,
                Manifest = skill.Manifest,
            });
        });

        // ── Toolsets ──────────────────────────────────────────────────────────
        var toolsets = app.MapGroup("/api/admin/toolsets")
            .WithTags("Admin/Toolsets")
            .RequireAuthorization("Admin");

        toolsets.MapGet("/", (ToolsetRegistry registry) =>
            Results.Ok(registry.All().Select(ts => new
            {
                ts.Id,
                ts.Name,
                ts.Description,
                ts.IsBuiltIn,
                DirectToolCount = ts.ToolIds.Count,
                IncludedToolsets = ts.IncludesToolsetIds,
            })));

        toolsets.MapGet("/{id}", (string id, ToolsetRegistry registry) =>
        {
            var ts = registry.Find(id);
            if (ts is null) return Results.NotFound();
            var resolved = registry.Resolve(id);
            return Results.Ok(new
            {
                ts.Id,
                ts.Name,
                ts.Description,
                ts.IsBuiltIn,
                ts.ToolIds,
                ts.IncludesToolsetIds,
                ResolvedToolIds = resolved,
            });
        });

        toolsets.MapGet("/{id}/resolve", (string id, ToolsetRegistry registry) =>
        {
            var resolved = registry.Resolve(id);
            if (!resolved.Any() && registry.Find(id) is null) return Results.NotFound();
            return Results.Ok(new { ToolsetId = id, ResolvedToolIds = resolved });
        });

        return app;
    }
}
