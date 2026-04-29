using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Core.Inference;
using MyLocalAssistant.Server.Persistence;

namespace MyLocalAssistant.Server.Llm;

public sealed class ChatService(
    AppDbContext db,
    InferenceQueue queue,
    LLamaSharpProvider provider,
    ModelManager models,
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
        string userMessage,
        int maxTokens,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (models.Status != ModelStatus.Loaded)
            throw new InvalidOperationException($"No model is loaded (status={models.Status}). Activate one in the Admin UI.");

        var prompt = BuildPrompt(agent.SystemPrompt, userMessage);
        log.LogDebug("Chat: agent={AgentId}, promptChars={Chars}", agent.Id, prompt.Length);

        using var lease = await queue.AcquireAsync(ct);
        await foreach (var token in provider.GenerateAsync(prompt, maxTokens, ct).ConfigureAwait(false))
        {
            yield return token;
        }
    }

    private static string BuildPrompt(string systemPrompt, string userMessage)
    {
        // Minimal, model-agnostic plain-text framing. Chat-template support comes later
        // (LLamaSharp can apply per-model templates once we wire it through ModelManager).
        return $"{systemPrompt.Trim()}\n\nUser: {userMessage.Trim()}\nAssistant:";
    }
}
