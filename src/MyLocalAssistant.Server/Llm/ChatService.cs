using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Core.Inference;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Server.Rag;
using MyLocalAssistant.Server.Skills;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Llm;

public sealed class ChatService(
    AppDbContext db,
    InferenceQueue queue,
    LLamaSharpProvider provider,
    ModelManager models,
    RagService rag,
    SkillRegistry skills,
    ModelCapabilityRegistry capabilities,
    ToolCallStats toolStats,
    ServerSettings settings,
    ILogger<ChatService> log)
{
    public sealed record VisibilityCheck(bool Allowed, string? Reason, Agent? Agent);

    /// <summary>Hard cap on tool invocations per chat turn. Prevents runaway loops on jailbreaks.</summary>
    private const int MaxToolCallsPerTurn = 3;

    private const string ToolCallOpen = "<tool_call>";
    private const string ToolCallClose = "</tool_call>";

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

    public sealed record HistoryTurn(MessageRole Role, string Body);

    public sealed class ChatStreamCallbacks
    {
        public Action<RagRetrievalResult>? OnRetrieval { get; init; }
        /// <summary>Fired for every agent-bound skill skipped, with a human-readable reason.</summary>
        public Action<string, string>? OnToolUnavailable { get; init; }
        /// <summary>Fired immediately after a tool call is parsed but before invocation.</summary>
        public Action<string, string>? OnToolCall { get; init; }
        /// <summary>Fired with the result (or error message) returned by the skill.</summary>
        public Action<string, string, bool>? OnToolResult { get; init; }
        /// <summary>Conversation id for the current turn (passed into <see cref="SkillContext"/>).</summary>
        public Guid ConversationId { get; init; }
    }

    /// <summary>
    /// Streams generated tokens for a single chat turn. Text inside <c>&lt;tool_call&gt;</c>
    /// markers is stripped from the user-visible token stream and surfaced via the callbacks.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        Agent agent,
        UserPrincipals principal,
        string userMessage,
        int maxTokens,
        IReadOnlyList<HistoryTurn> history,
        ChatStreamCallbacks callbacks,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (models.Status != ModelStatus.Loaded)
            throw new InvalidOperationException($"No model is loaded (status={models.Status}). Activate one in the Admin UI.");

        var retrieval = await rag.RetrieveAsync(agent, principal, userMessage, k: 4, ct);
        callbacks.OnRetrieval?.Invoke(retrieval);

        var activeModelId = models.ActiveModelId;
        var capability = capabilities.Get(activeModelId);
        var resolvedSkills = ResolveSkills(agent, capability, callbacks.OnToolUnavailable);
        var toolMode = resolvedSkills.Count > 0;

        var basePrompt = BuildPrompt(
            settings.GlobalSystemPrompt,
            agent.SystemPrompt,
            userMessage,
            retrieval.Chunks,
            history,
            resolvedSkills);

        log.LogDebug("Chat: agent={AgentId}, user={User}, ragChunks={Chunks}, history={Hist}, promptChars={Chars}, model={Model}, tools={Tools}/{Bound}",
            agent.Id, principal.Username ?? principal.UserId.ToString(),
            retrieval.Chunks.Count, history.Count, basePrompt.Length, activeModelId,
            resolvedSkills.Count, SkillRegistry.ParseSkillIds(agent.SkillIds).Count);

        using var lease = await queue.AcquireAsync(ct);

        var assistantSoFar = new StringBuilder();
        var stops = toolMode ? new[] { ToolCallClose } : Array.Empty<string>();
        var workDir = await ResolveWorkDirectoryAsync(principal.UserId, callbacks.ConversationId, ct);
        Directory.CreateDirectory(workDir);
        var skillCtx = new SkillContext(
            principal.UserId,
            principal.Username ?? string.Empty,
            principal.IsAdmin,
            principal.IsGlobalAdmin,
            agent.Id,
            callbacks.ConversationId,
            workDir,
            ct);

        for (var iteration = 0; iteration <= MaxToolCallsPerTurn; iteration++)
        {
            var prompt = basePrompt + assistantSoFar.ToString();
            var hideMode = false;
            var holdBack = new StringBuilder();
            var toolBuffer = new StringBuilder();
            string? completedToolJson = null;

            await foreach (var token in provider.GenerateAsync(prompt, maxTokens, stops, ct).ConfigureAwait(false))
            {
                assistantSoFar.Append(token);
                if (!hideMode)
                {
                    holdBack.Append(token);
                    var openIdx = IndexOf(holdBack, ToolCallOpen);
                    if (openIdx >= 0)
                    {
                        if (openIdx > 0) yield return holdBack.ToString(0, openIdx);
                        var afterOpen = openIdx + ToolCallOpen.Length;
                        toolBuffer.Append(holdBack.ToString(afterOpen, holdBack.Length - afterOpen));
                        holdBack.Clear();
                        hideMode = true;
                        var closeIdx2 = IndexOf(toolBuffer, ToolCallClose);
                        if (closeIdx2 >= 0)
                        {
                            completedToolJson = toolBuffer.ToString(0, closeIdx2);
                            break;
                        }
                    }
                    else
                    {
                        var safe = Math.Max(0, holdBack.Length - (ToolCallOpen.Length - 1));
                        if (safe > 0)
                        {
                            yield return holdBack.ToString(0, safe);
                            holdBack.Remove(0, safe);
                        }
                    }
                }
                else
                {
                    toolBuffer.Append(token);
                    var closeIdx = IndexOf(toolBuffer, ToolCallClose);
                    if (closeIdx >= 0)
                    {
                        completedToolJson = toolBuffer.ToString(0, closeIdx);
                        break;
                    }
                }
            }

            if (completedToolJson is null)
            {
                if (!hideMode && holdBack.Length > 0) yield return holdBack.ToString();
                if (hideMode)
                {
                    var unfinished = toolBuffer.ToString().Trim();
                    callbacks.OnToolUnavailable?.Invoke("(parser)",
                        $"Model emitted an unterminated <tool_call> ({unfinished.Length} chars). Ignored.");
                }
                yield break;
            }

            if (iteration == MaxToolCallsPerTurn)
            {
                callbacks.OnToolUnavailable?.Invoke("(loop)",
                    $"Tool-call limit reached ({MaxToolCallsPerTurn}). Aborting further calls.");
                yield break;
            }

            EnsureSuffix(assistantSoFar, ToolCallClose);

            var (toolName, argsJson, parseError) = ParseToolCall(completedToolJson);
            string resultJson;
            bool isError;
            if (parseError is not null)
            {
                resultJson = JsonSerializer.Serialize(new { error = parseError });
                isError = true;
                callbacks.OnToolCall?.Invoke(toolName ?? "(unparsed)", completedToolJson);
                callbacks.OnToolResult?.Invoke(toolName ?? "(unparsed)", resultJson, true);
            }
            else
            {
                callbacks.OnToolCall?.Invoke(toolName!, argsJson ?? "{}");
                var (lookupOk, lookupError, skill) = LookupAllowedSkill(toolName!, resolvedSkills);
                if (!lookupOk)
                {
                    resultJson = JsonSerializer.Serialize(new { error = lookupError });
                    isError = true;
                }
                else
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var inv = new SkillInvocation(toolName!, argsJson ?? "{}");
                        var result = await skill!.InvokeAsync(inv, skillCtx).ConfigureAwait(false);
                        sw.Stop();
                        resultJson = result.StructuredJson
                            ?? JsonSerializer.Serialize(new { content = result.Content });
                        isError = result.IsError;
                        if (isError) toolStats.RecordError(skill.Id, toolName!, sw.Elapsed.TotalMilliseconds);
                        else toolStats.RecordSuccess(skill.Id, toolName!, sw.Elapsed.TotalMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        log.LogWarning(ex, "Skill {Tool} threw during invocation.", toolName);
                        resultJson = JsonSerializer.Serialize(new { error = ex.Message });
                        isError = true;
                        toolStats.RecordError(skill?.Id ?? toolName!, toolName!, sw.Elapsed.TotalMilliseconds);
                    }
                }
                callbacks.OnToolResult?.Invoke(toolName!, resultJson, isError);
            }

            assistantSoFar.Append('\n').Append("<tool_result>").Append(resultJson).Append("</tool_result>\n");
        }
    }

    /// <summary>
    /// Resolve the per-conversation work directory passed to skills as <c>workDirectory</c>.
    /// Honors <see cref="User.WorkRoot"/> when set, falling back to <c>state\\output\\</c>.
    /// Best-effort: any I/O failure on the user's WorkRoot (path missing, permission denied,
    /// network share offline) is logged and silently falls back so the chat still proceeds.
    /// </summary>
    private async Task<string> ResolveWorkDirectoryAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var conv = conversationId.ToString("N");
        var fallback = Path.Combine(ServerPaths.OutputDirectory, conv);
        try
        {
            var workRoot = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.WorkRoot)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(workRoot)) return fallback;
            var dir = Path.Combine(workRoot, conv);
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to use user WorkRoot for {UserId}; falling back to {Fallback}.", userId, fallback);
            return fallback;
        }
    }

    /// <summary>
    /// Resolve the agent's bound skill ids into actual <see cref="ISkill"/>s, reporting every
    /// id that was filtered (disabled, missing, or model can't tool-call) via the callback.
    /// </summary>
    private List<ISkill> ResolveSkills(Agent agent, ModelCapability capability, Action<string, string>? onUnavailable)
    {
        var bound = SkillRegistry.ParseSkillIds(agent.SkillIds);
        if (bound.Count == 0) return new List<ISkill>(0);

        if (capability.Tools == ToolCallProtocols.None)
        {
            foreach (var id in bound)
                onUnavailable?.Invoke(id, "Active model does not support tool calling.");
            return new List<ISkill>(0);
        }
        if (capability.Tools != ToolCallProtocols.Tags)
        {
            foreach (var id in bound)
                onUnavailable?.Invoke(id, $"Tool protocol '{capability.Tools}' is not yet implemented.");
            return new List<ISkill>(0);
        }

        var resolved = new List<ISkill>(bound.Count);
        foreach (var id in bound)
        {
            if (!skills.TryGet(id, out var skill))
            {
                onUnavailable?.Invoke(id, "Skill is not registered on this server.");
                continue;
            }
            if (!skills.IsEnabled(id))
            {
                onUnavailable?.Invoke(id, "Skill is disabled in the global skill catalog.");
                continue;
            }
            if (capability.ContextK < skill.Requirements.MinContextK)
            {
                onUnavailable?.Invoke(id,
                    $"Active model context ({capability.ContextK}k) is below skill minimum ({skill.Requirements.MinContextK}k).");
                continue;
            }
            resolved.Add(skill);
        }
        return resolved;
    }

    private static (bool ok, string? error, ISkill? skill) LookupAllowedSkill(string toolName, IReadOnlyList<ISkill> allowed)
    {
        foreach (var s in allowed)
        {
            if (string.Equals(s.Id, toolName, StringComparison.OrdinalIgnoreCase)) return (true, null, s);
            foreach (var t in s.Tools)
                if (string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase)) return (true, null, s);
        }
        return (false, $"Tool '{toolName}' is not bound to this agent or not enabled.", null);
    }

    private static (string? toolName, string? argsJson, string? error) ParseToolCall(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return (null, null, "Empty tool call.");
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, "Tool call must be a JSON object.");
            string? name = null;
            if (root.TryGetProperty("tool", out var t1) && t1.ValueKind == JsonValueKind.String) name = t1.GetString();
            else if (root.TryGetProperty("name", out var t2) && t2.ValueKind == JsonValueKind.String) name = t2.GetString();
            if (string.IsNullOrWhiteSpace(name)) return (null, null, "Tool call missing 'tool' field.");
            string args = "{}";
            if (root.TryGetProperty("arguments", out var a1)) args = a1.GetRawText();
            else if (root.TryGetProperty("args", out var a2)) args = a2.GetRawText();
            return (name, args, null);
        }
        catch (JsonException jex)
        {
            return (null, null, "Tool call JSON parse error: " + jex.Message);
        }
    }

    private static int IndexOf(StringBuilder sb, string needle)
    {
        if (needle.Length == 0 || sb.Length < needle.Length) return -1;
        var max = sb.Length - needle.Length;
        for (var i = 0; i <= max; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (sb[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static void EnsureSuffix(StringBuilder sb, string suffix)
    {
        if (sb.Length >= suffix.Length)
        {
            var ok = true;
            for (var j = 0; j < suffix.Length; j++)
            {
                if (sb[sb.Length - suffix.Length + j] != suffix[j]) { ok = false; break; }
            }
            if (ok) return;
        }
        sb.Append(suffix);
    }

    private static string BuildPrompt(
        string? globalSystemPrompt,
        string systemPrompt,
        string userMessage,
        IReadOnlyList<RagContextChunk> chunks,
        IReadOnlyList<HistoryTurn> history,
        IReadOnlyList<ISkill> tools)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(globalSystemPrompt))
        {
            sb.Append(globalSystemPrompt!.Trim());
            sb.Append("\n\n");
        }
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
        if (tools.Count > 0)
        {
            sb.Append("\n\nYou have access to the following tools. To call one, emit EXACTLY:\n");
            sb.Append("<tool_call>{\"tool\":\"<tool-name>\",\"arguments\":{...}}</tool_call>\n");
            sb.Append("Stop immediately after the closing tag. The system will respond with:\n");
            sb.Append("<tool_result>{\"content\":\"...\"}</tool_result>\n");
            sb.Append("Then continue your answer to the user. Call a tool only when it materially helps. Maximum ");
            sb.Append(MaxToolCallsPerTurn).Append(" tool calls per turn.\n\nAvailable tools:\n");
            foreach (var skill in tools)
            {
                foreach (var t in skill.Tools)
                {
                    sb.Append("- ").Append(t.Name).Append(": ").Append(t.Description).Append('\n');
                    if (!string.IsNullOrWhiteSpace(t.ArgumentsSchemaJson))
                    {
                        sb.Append("  schema: ").Append(t.ArgumentsSchemaJson.Replace("\r", "").Replace('\n', ' ')).Append('\n');
                    }
                }
            }
        }
        sb.Append('\n');
        foreach (var turn in history)
        {
            if (string.IsNullOrWhiteSpace(turn.Body)) continue;
            var label = turn.Role == MessageRole.Assistant ? "Assistant" : "User";
            sb.Append(label).Append(": ").Append(turn.Body.Trim()).Append('\n');
        }
        sb.Append("User: ").Append(userMessage.Trim()).Append("\nAssistant:");
        return sb.ToString();
    }
}
