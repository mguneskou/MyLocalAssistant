using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class ConversationEndpoints
{
    private const int ListLimit = 100;

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chat/conversations").WithTags("Chat").RequireAuthorization();
        g.MapGet("/", ListAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        return app;
    }

    private static Guid CurrentUserId(HttpContext http)
    {
        var sub = http.User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
    }

    private static async Task<IResult> ListAsync(HttpContext http, AppDbContext db, string? agentId, CancellationToken ct)
    {
        var userId = CurrentUserId(http);
        if (userId == Guid.Empty) return Results.Unauthorized();

        var query = db.Conversations.Where(c => c.UserId == userId);
        if (!string.IsNullOrWhiteSpace(agentId))
            query = query.Where(c => c.AgentId == agentId);

        var rows = await query
            .OrderByDescending(c => c.UpdatedAt)
            .Take(ListLimit)
            .Select(c => new ConversationSummaryDto(
                c.Id, c.AgentId, c.Title, c.CreatedAt, c.UpdatedAt,
                c.Messages.Count))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetAsync(HttpContext http, AppDbContext db, Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId(http);
        if (userId == Guid.Empty) return Results.Unauthorized();

        var c = await db.Conversations
            .Where(x => x.Id == id && x.UserId == userId)
            .Select(x => new
            {
                x.Id, x.AgentId, x.Title, x.CreatedAt, x.UpdatedAt,
                Messages = x.Messages
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => new ConversationMessageDto(m.Id, m.Role.ToString(), m.Body, m.CreatedAt))
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (c is null) return Results.NotFound();
        return Results.Ok(new ConversationDetailDto(c.Id, c.AgentId, c.Title, c.CreatedAt, c.UpdatedAt, c.Messages));
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, AppDbContext db, Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId(http);
        if (userId == Guid.Empty) return Results.Unauthorized();

        var c = await db.Conversations.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (c is null) return Results.NotFound();
        db.Conversations.Remove(c);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
