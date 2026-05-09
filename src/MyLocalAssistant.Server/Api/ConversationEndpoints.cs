using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class ConversationEndpoints
{
    private const int ListLimit = 100;
    private const int SearchLimit = 20;

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chat/conversations").WithTags("Chat").RequireAuthorization();
        g.MapGet("/", ListAsync);
        g.MapGet("/search", SearchAsync);
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

    private static async Task<IResult> SearchAsync(
        HttpContext http, AppDbContext db, EmbeddingService embedding,
        string q, bool semantic = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest("q is required.");
        var userId = CurrentUserId(http);
        if (userId == Guid.Empty) return Results.Unauthorized();

        // SQL LIKE search across message bodies for this user's conversations.
        var term = "%" + q.Replace("%", "\\%").Replace("_", "\\_") + "%";
        var matches = await db.Messages.AsNoTracking()
            .Where(m => m.Conversation.UserId == userId
                        && m.Body != null
                        && EF.Functions.Like(m.Body, term))
            .Select(m => new { m.ConversationId, m.Conversation.Title, m.Conversation.AgentId,
                                m.Conversation.UpdatedAt, m.Conversation.CreatedAt,
                                MessageCount = m.Conversation.Messages.Count })
            .Distinct()
            .OrderByDescending(m => m.UpdatedAt)
            .Take(SearchLimit * 3)
            .ToListAsync(ct);

        var conversations = matches
            .GroupBy(m => m.ConversationId)
            .Select(g => g.First())
            .Take(SearchLimit)
            .Select(c => new ConversationSummaryDto(c.ConversationId, c.AgentId, c.Title, c.CreatedAt, c.UpdatedAt, c.MessageCount))
            .ToList();

        // Optional semantic re-ranking using the embedding service.
        if (semantic && embedding.IsLoaded && conversations.Count > 1)
        {
            try
            {
                var queryVec = await embedding.EmbedAsync(q, ct);
                var conversationIds = conversations.Select(c => c.Id).ToList();
                // Retrieve a snippet per conversation to score against the query vector.
                var snippets = await db.Messages.AsNoTracking()
                    .Where(m => conversationIds.Contains(m.ConversationId)
                                && m.Body != null
                                && EF.Functions.Like(m.Body, term))
                    .Select(m => new { m.ConversationId, m.Body })
                    .ToListAsync(ct);

                var scored = new Dictionary<Guid, float>();
                foreach (var s in snippets)
                {
                    if (s.Body is null) continue;
                    var vec = await embedding.EmbedAsync(s.Body[..Math.Min(512, s.Body.Length)], ct);
                    var dot = CosineSimilarity(queryVec, vec);
                    if (!scored.TryGetValue(s.ConversationId, out var prev) || dot > prev)
                        scored[s.ConversationId] = dot;
                }
                conversations = conversations
                    .OrderByDescending(c => scored.GetValueOrDefault(c.Id, 0f))
                    .ToList();
            }
            catch { /* embedding failure — keep SQL-ranked results */ }
        }

        return Results.Ok(conversations);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        float dot = 0, na = 0, nb = 0;
        for (var i = 0; i < len; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-8));
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
