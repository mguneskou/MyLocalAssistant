using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Server.Persistence;

namespace MyLocalAssistant.Server.Rag;

public sealed record RagContextChunk(
    string Source,
    int Page,
    string Text,
    float Distance,
    Guid CollectionId);

/// <summary>
/// Retrieves top-K context chunks for an agent's chat turn by fanning out across
/// every collection attached to the agent and merging by ascending distance.
/// </summary>
public sealed class RagService(
    IVectorStore store,
    EmbeddingService embedding,
    ILogger<RagService> log)
{
    /// <summary>Per-agent retrieval. Returns empty list when RAG is disabled, no collections, or embedding model not loaded.</summary>
    public async Task<IReadOnlyList<RagContextChunk>> RetrieveAsync(
        Agent agent,
        string query,
        int k,
        CancellationToken ct)
    {
        if (!agent.RagEnabled) return Array.Empty<RagContextChunk>();
        var ids = ParseCollectionIds(agent.RagCollectionIds);
        if (ids.Count == 0) return Array.Empty<RagContextChunk>();
        if (!embedding.IsLoaded)
        {
            log.LogWarning("RAG requested for agent {Agent} but embedding model not loaded; skipping.", agent.Id);
            return Array.Empty<RagContextChunk>();
        }
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<RagContextChunk>();
        if (k <= 0) k = 4;

        var queryVec = await embedding.EmbedAsync(query, ct);
        // Pull top-k from each collection, merge by distance ascending, take top-k overall.
        var merged = new List<RagContextChunk>();
        foreach (var cid in ids)
        {
            try
            {
                await store.EnsureCollectionAsync(cid.ToString("N"), embedding.EmbeddingDimension, ct);
                var hits = await store.SearchAsync(cid.ToString("N"), queryVec, k, ct);
                foreach (var h in hits)
                    merged.Add(new RagContextChunk(h.Source, h.Page, h.Text, h.Distance, cid));
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "RAG search failed for agent {Agent} collection {Coll}", agent.Id, cid);
            }
        }
        return merged.OrderBy(c => c.Distance).Take(k).ToList();
    }

    public static IReadOnlyList<Guid> ParseCollectionIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<Guid>();
        var list = new List<Guid>();
        foreach (var part in csv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Guid.TryParse(part, out var g)) list.Add(g);
        return list;
    }

    public static string? FormatCollectionIds(IEnumerable<Guid>? ids)
    {
        if (ids is null) return null;
        var arr = ids.Where(g => g != Guid.Empty).Distinct().ToArray();
        return arr.Length == 0 ? null : string.Join(';', arr.Select(g => g.ToString()));
    }
}
