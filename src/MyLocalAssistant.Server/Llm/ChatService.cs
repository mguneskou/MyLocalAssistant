using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Core.Inference;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Server.Rag;

namespace MyLocalAssistant.Server.Llm;

public sealed class ChatService(
    AppDbContext db,
    InferenceQueue queue,
    LLamaSharpProvider provider,
    ModelManager models,
    RagService rag,
    ILogger<ChatService> log)
{
    public sealed record VisibilityCheck(bool Allowed, string? Reason, Agent? Agent);

    public async Task<VisibilityCheck> CheckVisibilityAsync(string agentId, Guid userId, bool isAdmin, CancellationToken ct)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null) return new VisibilityCheck(false, "Unknown agent.", null);
        if (!agent.Enabled) return new VisibilityCheck(false, "Agent is disabled.", agent);
        if (isAdmin || agent.IsGeneric) return new VisibilityCheck(true, null, agent);

        var hasDept = await db.UserDepartments
            .AnyAsync(ud => ud.UserId == userId && ud.Department.Name == agent.Name, ct);
        return hasDept
            ? new VisibilityCheck(true, null, agent)
            : new VisibilityCheck(false, "Agent is not available to your department.", agent);
    }

    /// <summary>
    /// Streams generated tokens for a single chat turn. The caller is responsible for
    /// already having validated agent visibility. Throws if no model is loaded.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        Agent agent,
        UserPrincipals principal,
        string userMessage,
        int maxTokens,
        Action<RagRetrievalResult>? onRetrieval,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (models.Status != ModelStatus.Loaded)
            throw new InvalidOperationException($"No model is loaded (status={models.Status}). Activate one in the Admin UI.");

        var retrieval = await rag.RetrieveAsync(agent, principal, userMessage, k: 4, ct);
        onRetrieval?.Invoke(retrieval);
        var prompt = BuildPrompt(agent.SystemPrompt, userMessage, retrieval.Chunks);
        log.LogDebug("Chat: agent={AgentId}, user={User}, ragChunks={Chunks}, allowed={Allow}/{Total}, promptChars={Chars}",
            agent.Id, principal.Username ?? principal.UserId.ToString(),
            retrieval.Chunks.Count, retrieval.Allowed.Count, retrieval.Requested.Count, prompt.Length);

        using var lease = await queue.AcquireAsync(ct);
        await foreach (var token in provider.GenerateAsync(prompt, maxTokens, ct).ConfigureAwait(false))
        {
            yield return token;
        }
    }

    private static string BuildPrompt(string systemPrompt, string userMessage, IReadOnlyList<RagContextChunk> chunks)
    {
        var sb = new StringBuilder();
        sb.Append(systemPrompt.Trim());
        if (chunks.Count > 0)
        {
            sb.Append("\n\nUse the following context to answer the user. Cite sources inline as [source:page] when relevant. If the context does not contain the answer, say so.\n\nContext:\n");
            for (var i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                sb.Append($"[{i + 1}] ({c.Source}:p{c.Page})\n");
                sb.Append(c.Text.Trim());
                sb.Append("\n\n");
            }
        }
        sb.Append("User: ").Append(userMessage.Trim()).Append("\nAssistant:");
        return sb.ToString();
    }
}
