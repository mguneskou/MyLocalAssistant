using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Server.Rag;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Skills.BuiltIn;

/// <summary>
/// Lets the LLM explicitly query a RAG collection it (and the calling user) is
/// allowed to read. Distinct from the agent's implicit auto-RAG: this fires only
/// when the model decides it needs more context, scoped to a specific collection
/// the model picks by name. Authorization runs through the same
/// <see cref="RagAuthorizationService"/> pipeline as auto-RAG — a tool call cannot
/// bypass per-collection grants.
/// </summary>
internal sealed class RagSearchSkill(IServiceScopeFactory scopeFactory) : ISkill
{
    public string Id => "rag.search_collection";
    public string Name => "RAG search";
    public string Description => "Searches a named RAG collection for the most relevant chunks. Honours per-user access control.";
    public string Category => "Built-in";
    public string Source => SkillSources.BuiltIn;
    public string? Version => null;
    public string? Publisher => "MyLocalAssistant";
    public string? KeyId => null;

    public IReadOnlyList<SkillToolDto> Tools { get; } = new[]
    {
        new SkillToolDto(
            Name: "rag.search_collection",
            Description: "Search a RAG collection by name and return the top matching chunks. " +
                         "Use when the user asks something that probably needs documents from a known collection.",
            ArgumentsSchemaJson: """
            {
              "type": "object",
              "required": ["collection", "query"],
              "properties": {
                "collection": { "type": "string", "description": "Collection name (case-insensitive)." },
                "query":      { "type": "string", "description": "Natural-language query." },
                "topK":       { "type": "integer", "minimum": 1, "maximum": 20, "default": 4 }
              },
              "additionalProperties": false
            }
            """),
    };

    public SkillRequirementsDto Requirements { get; } = new(ToolCallProtocols.Tags, MinContextK: 4);

    public void Configure(string? configJson) { /* no per-instance config */ }

    public async Task<SkillResult> InvokeAsync(SkillInvocation call, SkillContext ctx)
    {
        if (!string.Equals(call.ToolName, "rag.search_collection", StringComparison.Ordinal))
            return SkillResult.Error($"Unknown tool '{call.ToolName}'.");

        string collectionName;
        string query;
        int topK = 4;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            collectionName = doc.RootElement.GetProperty("collection").GetString() ?? "";
            query          = doc.RootElement.GetProperty("query").GetString() ?? "";
            if (doc.RootElement.TryGetProperty("topK", out var k) && k.ValueKind == JsonValueKind.Number)
                topK = Math.Clamp(k.GetInt32(), 1, 20);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return SkillResult.Error("Arguments must include 'collection' (string) and 'query' (string).");
        }

        if (string.IsNullOrWhiteSpace(collectionName)) return SkillResult.Error("Collection name is required.");
        if (string.IsNullOrWhiteSpace(query))           return SkillResult.Error("Query is required.");

        // Skills are singletons; RagService + AppDbContext are scoped. Open a per-call scope.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db        = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rag       = scope.ServiceProvider.GetRequiredService<RagService>();
        var authz     = scope.ServiceProvider.GetRequiredService<RagAuthorizationService>();

        var collection = await db.RagCollections
            .FirstOrDefaultAsync(c => c.Name.ToLower() == collectionName.ToLower(), ctx.CancellationToken);
        if (collection is null)
            return SkillResult.Error($"Collection '{collectionName}' does not exist.");

        // Resolve principals fresh from DB so revocations apply immediately, then run the
        // exact same auth + retrieval path used by auto-RAG via a synthetic per-call agent.
        var principal = await authz.ResolveAsync(ctx.UserId, ctx.Username, ctx.IsAdmin, ctx.CancellationToken);
        var syntheticAgent = new Agent
        {
            Id = $"skill:{ctx.AgentId}",
            RagEnabled = true,
            RagCollectionIds = collection.Id.ToString(),
        };
        var result = await rag.RetrieveAsync(syntheticAgent, principal, query, topK, ctx.CancellationToken);

        if (result.Allowed.Count == 0)
            return SkillResult.Error($"You do not have access to collection '{collectionName}'.");
        if (result.Chunks.Count == 0)
            return SkillResult.Ok($"No matching chunks found in '{collectionName}'.");

        var sb = new StringBuilder();
        sb.AppendLine($"Top {result.Chunks.Count} chunk(s) from collection '{collectionName}':");
        for (int i = 0; i < result.Chunks.Count; i++)
        {
            var c = result.Chunks[i];
            sb.AppendLine();
            sb.AppendLine($"[{i + 1}] {c.Source} (page {c.Page}, distance {c.Distance:F3})");
            sb.AppendLine(c.Text);
        }
        return SkillResult.Ok(sb.ToString());
    }
}
