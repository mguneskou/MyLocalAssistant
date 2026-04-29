using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Server.Persistence;

namespace MyLocalAssistant.Server.Rag;

public sealed record RagContextChunk(
    string Source,
    int Page,
    string Text,
    float Distance,
    Guid CollectionId);

/// <summary>Result of an authorized retrieval. Carries audit-relevant collection sets.</summary>
public sealed record RagRetrievalResult(
    IReadOnlyList<RagContextChunk> Chunks,
    IReadOnlyList<Guid> Requested,
    IReadOnlyList<Guid> Allowed,
    IReadOnlyList<Guid> Denied)
{
    public static readonly RagRetrievalResult Empty = new(
        Array.Empty<RagContextChunk>(), Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>());
}

/// <summary>
/// Retrieves top-K context chunks for an agent's chat turn by fanning out across
/// every collection attached to the agent and merging by ascending distance.
/// Strictly enforces per-collection authorization: a caller never receives chunks
/// from a collection they cannot read, regardless of agent attachment or system prompt.
/// </summary>
public sealed class RagService(
    IVectorStore store,
    EmbeddingService embedding,
    RagAuthorizationService authz,
    ILogger<RagService> log)
{
    /// <summary>Per-agent retrieval. Returns empty list when RAG is disabled, no collections, embedding model not loaded, or all collections are denied.</summary>
    public async Task<RagRetrievalResult> RetrieveAsync(
        Agent agent,
        UserPrincipals principal,
        string query,
        int k,
        CancellationToken ct)
    {
        if (!agent.RagEnabled) return RagRetrievalResult.Empty;
        var requested = ParseCollectionIds(agent.RagCollectionIds);
        if (requested.Count == 0) return RagRetrievalResult.Empty;

        // Authorization gate FIRST. If a Restricted collection has no grant for this principal,
        // it never reaches the vector store. This is the single chokepoint.
        var decision = await authz.AuthorizeReadAsync(principal, requested, ct);
        if (decision.Allowed.Count == 0)
        {
            return new RagRetrievalResult(Array.Empty<RagContextChunk>(), requested, decision.Allowed, decision.Denied);
        }

        if (!embedding.IsLoaded)
        {
            log.LogWarning("RAG requested for agent {Agent} but embedding model not loaded; skipping.", agent.Id);
            return new RagRetrievalResult(Array.Empty<RagContextChunk>(), requested, decision.Allowed, decision.Denied);
        }
        if (string.IsNullOrWhiteSpace(query))
            return new RagRetrievalResult(Array.Empty<RagContextChunk>(), requested, decision.Allowed, decision.Denied);
        if (k <= 0) k = 4;

        var queryVec = await embedding.EmbedAsync(query, ct);
        var merged = new List<RagContextChunk>();
        foreach (var cid in decision.Allowed)
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
        var top = merged.OrderBy(c => c.Distance).Take(k).ToList();
        return new RagRetrievalResult(top, requested, decision.Allowed, decision.Denied);
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
