using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Inference;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Server.Rag;
using MyLocalAssistant.Server.Tools;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Llm;

public sealed class ChatService(
    AppDbContext db,
    InferenceQueue queue,
    ChatProviderRouter router,
    ModelCatalogService catalog,
    ModelManager models,
    RagService rag,
    ToolRegistry skills,
    MyLocalAssistant.Server.Skills.SkillRegistry skillRegistry,
    MyLocalAssistant.Server.Tools.ToolsetRegistry toolsetRegistry,
    ModelCapabilityRegistry capabilities,
    ToolCallStats toolStats,
    ServerSettings settings,
    ILogger<ChatService> log)
{
    public sealed record VisibilityCheck(bool Allowed, string? Reason, Agent? Agent);

    /// <summary>Server-wide default cap on tool invocations per chat turn. Agents may lower or raise this (max 20).</summary>
    private const int DefaultMaxToolCallsPerTurn = 10;
    private const string CeoStrategicSupervisorAgentId = "ceo-strategic-supervisor";

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
        /// <summary>Fired with the result (or error message) returned by the tool.</summary>
        public Action<string, string, bool>? OnToolResult { get; init; }
        /// <summary>Fired when the request must wait for the inference queue. Argument is the 1-based position.</summary>
        public Action<int>? OnQueued { get; init; }
        /// <summary>Conversation id for the current turn (passed into <see cref="ToolContext"/>).</summary>
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
        {
            var detail = string.IsNullOrWhiteSpace(models.LastError) ? "" : $" — {models.LastError}";
            var hint = models.Status == ModelStatus.Failed
                ? " Check the Models tab in Admin (the activation succeeded but the model failed to load — typically a missing GPU backend, insufficient VRAM, or a CPU without AVX/AVX2)."
                : models.Status == ModelStatus.Loading
                    ? " The model is still loading — try again in a few seconds."
                    : " Activate one in the Admin UI.";
            throw new InvalidOperationException($"No model is loaded (status={models.Status}).{detail}{hint}");
        }

        var retrieval = await rag.RetrieveAsync(agent, principal, userMessage, k: 4, ct);
        callbacks.OnRetrieval?.Invoke(retrieval);

        var activeModelId = models.ActiveModelId;
        var activeEntry = activeModelId is null ? null : catalog.FindById(activeModelId);
        if (activeEntry is null)
            throw new InvalidOperationException("Active model is not in the catalog.");
        var chatProvider = router.Get(activeEntry);
        var capability = capabilities.Get(activeModelId);
        var resolvedSkills = ResolveSkills(agent, capability, callbacks.OnToolUnavailable);

        // Hermes-style skill injection: resolve active skills for this agent and
        // collect their required tools + system prompt contributions.
        var activeSkills    = ResolveActiveSkills(agent);
        var skillPromptPart = BuildSkillSystemPrompt(activeSkills);

        // Merge skill-required tools into the resolved tool list (if not already present).
        foreach (var skill in activeSkills)
        {
            foreach (var toolId in skill.RequiredToolIds)
            {
                if (skills.TryGet(toolId, out var extraTool)
                    && skills.IsEnabled(toolId)
                    && !resolvedSkills.Any(s => s.Id == extraTool.Id))
                {
                    resolvedSkills.Add(extraTool);
                }
            }
        }

        var toolMode = resolvedSkills.Count > 0;
        var workDir = await ResolveWorkDirectoryAsync(principal.UserId, callbacks.ConversationId, ct);
        Directory.CreateDirectory(workDir);

        var maxToolCalls = Math.Clamp(agent.MaxToolCalls ?? DefaultMaxToolCallsPerTurn, 1, 20);

        // Native tool-calling providers (currently Anthropic) never see the text-tag prompt
        // below — they get tool definitions and history as structured API fields instead, so
        // there is no custom grammar for the model to imitate (and potentially drift from).
        if (capability.Tools == ToolCallProtocols.Native && chatProvider is INativeToolChatProvider nativeProvider)
        {
            var waitingNative = queue.Waiting;
            if (waitingNative > 0)
                callbacks.OnQueued?.Invoke(waitingNative + 1);
            using var nativeLease = await queue.AcquireAsync(ct);

            var nativeCtx = new ToolContext(
                principal.UserId,
                principal.Username ?? string.Empty,
                principal.IsAdmin,
                principal.IsGlobalAdmin,
                agent.Id,
                callbacks.ConversationId,
                workDir,
                ct);

            await foreach (var chunk in StreamNativeAsync(
                agent, userMessage, maxTokens, history, callbacks, nativeProvider, activeEntry,
                retrieval.Chunks, resolvedSkills, skillPromptPart, workDir, maxToolCalls, nativeCtx, ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
            yield break;
        }

        var basePrompt = BuildPrompt(
            settings.GlobalSystemPrompt,
            agent.SystemPrompt,
            agent.ScenarioNotes,
            workDir,
            userMessage,
            retrieval.Chunks,
            history,
            resolvedSkills,
            maxToolCalls,
            skillPromptPart);

        log.LogDebug("Chat: agent={AgentId}, user={User}, ragChunks={Chunks}, history={Hist}, promptChars={Chars}, model={Model}, tools={Tools}/{Bound}",
            agent.Id, principal.Username ?? principal.UserId.ToString(),
            retrieval.Chunks.Count, history.Count, basePrompt.Length, activeModelId,
            resolvedSkills.Count, ToolRegistry.ParseToolIds(agent.ToolIds).Count);

        // If other requests are queued for the local model, notify the client so the UI can show a position indicator.
        // Cloud providers never contend on the queue, but the notification is harmless for them.
        var waiting = queue.Waiting;
        if (waiting > 0)
            callbacks.OnQueued?.Invoke(waiting + 1);   // +1 = this request's position once it joins

        using var lease = await queue.AcquireAsync(ct);

        var assistantSoFar = new StringBuilder();
        var visibleAssistant = new StringBuilder();
        var stops = toolMode ? new[] { ToolCallClose } : Array.Empty<string>();
        var skillCtx = new ToolContext(
            principal.UserId,
            principal.Username ?? string.Empty,
            principal.IsAdmin,
            principal.IsGlobalAdmin,
            agent.Id,
            callbacks.ConversationId,
            workDir,
            ct);

        for (var iteration = 0; iteration <= maxToolCalls; iteration++)
        {
            var prompt = basePrompt + assistantSoFar.ToString();
            var hideMode = false;
            var holdBack = new StringBuilder();
            var toolBuffer = new StringBuilder();
            string? completedToolJson = null;

            await foreach (var token in chatProvider.GenerateAsync(activeEntry, prompt, maxTokens, stops, ct).ConfigureAwait(false))
            {
                assistantSoFar.Append(token);
                if (!hideMode)
                {
                    holdBack.Append(token);
                    var openIdx = IndexOf(holdBack, ToolCallOpen);
                    if (openIdx >= 0)
                    {
                        if (openIdx > 0)
                        {
                            var chunk = holdBack.ToString(0, openIdx);
                            visibleAssistant.Append(chunk);
                            yield return chunk;
                        }
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
                            var chunk = holdBack.ToString(0, safe);
                            visibleAssistant.Append(chunk);
                            yield return chunk;
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
                if (!hideMode && holdBack.Length > 0)
                {
                    var tail = holdBack.ToString();
                    visibleAssistant.Append(tail);
                    yield return tail;
                }
                if (hideMode)
                {
                    var candidate = toolBuffer.ToString().Trim();
                    // Cloud providers (OpenAI-compatible APIs) suppress the stop token from
                    // the stream — </tool_call> is never emitted.  LLamaSharp anti-prompts
                    // DO include the matched text, so local models work without this path.
                    // If the buffer is a complete JSON object, treat the stop sequence as an
                    // implicit close and fall through to normal tool dispatch.
                    if (candidate.Length > 0 && IsCompleteJsonObject(candidate))
                    {
                        completedToolJson = candidate;
                    }
                    else
                    {
                        callbacks.OnToolUnavailable?.Invoke("(parser)",
                            $"Model emitted an unterminated <tool_call> ({candidate.Length} chars). Ignored.");
                        yield break;
                    }
                }
                else
                {
                    var supplement = BuildCeoModeAComplianceSupplementIfNeeded(agent.Id, visibleAssistant.ToString());
                    if (supplement.Length > 0) yield return supplement;
                    yield break;
                }
            }

            if (iteration == maxToolCalls)
            {
                callbacks.OnToolUnavailable?.Invoke("(loop)",
                    $"Tool-call limit reached ({maxToolCalls}). Aborting further calls.");
                var supplement = BuildCeoModeAComplianceSupplementIfNeeded(agent.Id, visibleAssistant.ToString());
                if (supplement.Length > 0) yield return supplement;
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
                        var inv = new ToolInvocation(toolName!, argsJson ?? "{}");
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
    /// Drives a chat turn for a provider whose model capability is
    /// <see cref="ToolCallProtocols.Native"/> — tool definitions and tool results are sent as
    /// structured API fields via <see cref="INativeToolChatProvider"/> instead of a text grammar,
    /// so the model can never drift to a different tag and silently bypass tool execution.
    /// Text deltas are yielded to the caller as soon as they arrive; there is no tag-buffering
    /// step because there is no tag to hide.
    /// </summary>
    private async IAsyncEnumerable<string> StreamNativeAsync(
        Agent agent,
        string userMessage,
        int maxTokens,
        IReadOnlyList<HistoryTurn> history,
        ChatStreamCallbacks callbacks,
        INativeToolChatProvider nativeProvider,
        MyLocalAssistant.Core.Models.CatalogEntry activeEntry,
        IReadOnlyList<RagContextChunk> ragChunks,
        IReadOnlyList<ITool> resolvedSkills,
        string? skillPromptPart,
        string workDir,
        int maxToolCalls,
        ToolContext toolCtx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var systemPrompt = BuildSystemPromptCore(
            settings.GlobalSystemPrompt,
            agent.SystemPrompt,
            agent.ScenarioNotes,
            workDir,
            ragChunks,
            resolvedSkills,
            skillPromptPart);

        var messages = new List<NativeChatMessage>();
        foreach (var turn in history)
        {
            if (string.IsNullOrWhiteSpace(turn.Body)) continue;
            var role = turn.Role == MessageRole.Assistant ? "assistant" : "user";
            messages.Add(new NativeChatMessage(role, new List<NativeContentBlock> { new NativeTextBlock(turn.Body.Trim()) }));
        }
        messages.Add(new NativeChatMessage("user", new List<NativeContentBlock> { new NativeTextBlock(userMessage.Trim()) }));

        var tools = new List<ToolFunctionDto>();
        foreach (var skill in resolvedSkills)
            tools.AddRange(skill.Tools);

        var visibleAssistant = new StringBuilder();

        for (var iteration = 0; iteration <= maxToolCalls; iteration++)
        {
            NativeChatMessage? finalMessage = null;
            var stopReason = "end_turn";

            await foreach (var ev in nativeProvider.GenerateWithToolsAsync(activeEntry, systemPrompt, messages, tools, maxTokens, ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case NativeTextDeltaEvent textDelta:
                        visibleAssistant.Append(textDelta.Text);
                        yield return textDelta.Text;
                        break;
                    case NativeMessageCompleteEvent complete:
                        finalMessage = complete.Message;
                        stopReason = complete.StopReason;
                        break;
                }
            }

            if (finalMessage is null)
            {
                // Stream ended (connection dropped, provider error swallowed upstream) without
                // ever completing the turn. Nothing more to say.
                yield break;
            }

            var toolUseBlocks = finalMessage.Content.OfType<NativeToolUseBlock>().ToList();

            if (stopReason != "tool_use" || toolUseBlocks.Count == 0)
            {
                var supplement = BuildCeoModeAComplianceSupplementIfNeeded(agent.Id, visibleAssistant.ToString());
                if (supplement.Length > 0) yield return supplement;
                yield break;
            }

            if (iteration == maxToolCalls)
            {
                callbacks.OnToolUnavailable?.Invoke("(loop)",
                    $"Tool-call limit reached ({maxToolCalls}). Aborting further calls.");
                var supplement = BuildCeoModeAComplianceSupplementIfNeeded(agent.Id, visibleAssistant.ToString());
                if (supplement.Length > 0) yield return supplement;
                yield break;
            }

            var toolResults = new List<NativeContentBlock>();
            foreach (var toolUse in toolUseBlocks)
            {
                callbacks.OnToolCall?.Invoke(toolUse.Name, toolUse.ArgumentsJson);
                var (lookupOk, lookupError, skill) = LookupAllowedSkill(toolUse.Name, resolvedSkills);
                string resultJson;
                bool isError;
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
                        var inv = new ToolInvocation(toolUse.Name, toolUse.ArgumentsJson);
                        var result = await skill!.InvokeAsync(inv, toolCtx).ConfigureAwait(false);
                        sw.Stop();
                        resultJson = result.StructuredJson ?? JsonSerializer.Serialize(new { content = result.Content });
                        isError = result.IsError;
                        if (isError) toolStats.RecordError(skill.Id, toolUse.Name, sw.Elapsed.TotalMilliseconds);
                        else toolStats.RecordSuccess(skill.Id, toolUse.Name, sw.Elapsed.TotalMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        log.LogWarning(ex, "Skill {Tool} threw during invocation.", toolUse.Name);
                        resultJson = JsonSerializer.Serialize(new { error = ex.Message });
                        isError = true;
                        toolStats.RecordError(skill?.Id ?? toolUse.Name, toolUse.Name, sw.Elapsed.TotalMilliseconds);
                    }
                }
                callbacks.OnToolResult?.Invoke(toolUse.Name, resultJson, isError);
                toolResults.Add(new NativeToolResultBlock(toolUse.Id, resultJson, isError));
            }

            // Replay the model's own turn (text + tool_use blocks) back verbatim, then hand it
            // every tool result in a single follow-up message — required when the model asked
            // for more than one tool in the same turn (parallel tool calls).
            messages.Add(finalMessage);
            messages.Add(new NativeChatMessage("user", toolResults));
        }
    }

    /// <summary>
    /// Builds the system-prompt text shared by both tool-calling paths: global/agent prompts,
    /// active skill instructions, the execution-loop reminder, filesystem context, RAG context,
    /// office/tool-chaining behavior rules, and scenario notes. Unlike <see cref="BuildPrompt"/>,
    /// this never mentions the text-tag calling grammar — native-mode tools are declared to the
    /// provider as structured fields, not described in prose.
    /// </summary>
    private static string BuildSystemPromptCore(
        string? globalSystemPrompt,
        string systemPrompt,
        string? scenarioNotes,
        string? workDirectory,
        IReadOnlyList<RagContextChunk> chunks,
        IReadOnlyList<ITool> tools,
        string? skillSystemPrompt)
    {
        var sb = new StringBuilder();
        sb.Append($"Today's date is {DateTime.Now:dddd, MMMM d, yyyy}.\n\n");
        if (!string.IsNullOrWhiteSpace(globalSystemPrompt))
        {
            sb.Append(globalSystemPrompt!.Trim());
            sb.Append("\n\n");
        }
        sb.Append(systemPrompt.Trim());

        if (!string.IsNullOrWhiteSpace(skillSystemPrompt))
        {
            sb.Append("\n\n");
            sb.Append(skillSystemPrompt!.Trim());
        }
        sb.Append("\n\nAgent execution loop (always follow):\n");
        sb.Append("• Thought: decide the next best step based on the user goal and current evidence.\n");
        sb.Append("• Act: when a tool materially helps, call the most relevant tool with precise arguments.\n");
        sb.Append("• Observe: inspect the tool result, update your plan, and either continue the loop or produce the final answer.\n");
        sb.Append("• You should think step by step in order to fulfill the objective with reasoning divided into Thought/Action/Observation steps that can be repeated multiple times when needed.\n");
        sb.Append("• Keep this loop internal; provide concise user-facing outputs.\n");
        sb.Append("• Never invent tool outputs, files, or facts. If evidence is missing after available actions, state the gap clearly.\n");
        sb.Append("• Never claim a tool call succeeded, describe a tool result, or state that a file/action was completed unless you actually called the tool and are reporting its real returned result.\n");
        sb.Append("• If a request requires a tool that is not enabled, unavailable, or failed at runtime, explicitly state the exact tool name and the reason.\n");
        if (!string.IsNullOrWhiteSpace(workDirectory))
        {
            var resolvedWorkDir = workDirectory.Replace("\r", "").Replace("\n", " ");
            sb.Append("\n\nFilesystem context:\n");
            sb.Append("• You have a writable per-conversation work directory on the server.\n");
            sb.Append("• For this conversation, the exact directory is: ").Append(resolvedWorkDir).Append('\n');
            sb.Append("• If the user configured a Work folder, this conversation directory lives under that root as a subfolder created automatically for this conversation.\n");
            sb.Append("• Word, Excel, PDF, report, image, and code tools read from or save to this directory automatically, usually by filename only.\n");
            sb.Append("• This work directory is explicitly authorized by the signed-in user and is in scope for this conversation.\n");
            sb.Append("• You may read, create, update, move, rename, summarize, and delete files in this directory when the user's request requires it.\n");
            sb.Append("• If client.fs tools are available, files exposed through the user's shared folder or copied into the work directory are also explicitly user-authorized for this conversation.\n");
            sb.Append("• Do not refuse or warn about privacy, consent, or permission concerns for files that are already in this authorized work directory or exposed through the available file tools.\n");
            sb.Append("• Only refuse filesystem actions that are outside the available tools, outside the authorized work directory/shared folder scope, or clearly unrelated to the user's request.\n");
        }
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
            sb.Append("\n\nTools are available for this conversation. Call a tool only when it materially helps.\n");
            sb.Append("• If the user asks for a concrete file deliverable (for example Excel, Word, PowerPoint, or PDF) and the matching tool is available, you must call that tool before your final answer.\n");
            sb.Append("• Do not claim a file was created, modified, or analyzed unless the corresponding tool call succeeded.\n");
            var officeRules = BuildOfficeWorkflowRules(tools);
            if (officeRules.Length > 0) sb.Append(officeRules);
            var chainingHints = BuildToolChainingHints(tools);
            if (chainingHints.Length > 0) sb.Append(chainingHints);
        }
        if (!string.IsNullOrWhiteSpace(scenarioNotes))
        {
            sb.Append("\n\nScenario notes for this agent:\n");
            sb.Append(scenarioNotes.Trim());
            sb.Append('\n');
        }
        return sb.ToString();
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
    /// Resolve the agent's bound skill ids into actual <see cref="ITool"/>s, reporting every
    /// id that was filtered (disabled, missing, or model can't tool-call) via the callback.
    /// </summary>
    private List<ITool> ResolveSkills(Agent agent, ModelCapability capability, Action<string, string>? onUnavailable)
    {
        var bound = ToolRegistry.ParseToolIds(agent.ToolIds);
        if (bound.Count == 0) return new List<ITool>(0);

        if (capability.Tools == ToolCallProtocols.None)
        {
            foreach (var id in bound)
                onUnavailable?.Invoke(id, "Active model does not support tool calling.");
            return new List<ITool>(0);
        }
        if (capability.Tools != ToolCallProtocols.Tags && capability.Tools != ToolCallProtocols.Native)
        {
            foreach (var id in bound)
                onUnavailable?.Invoke(id, $"Tool protocol '{capability.Tools}' is not yet implemented.");
            return new List<ITool>(0);
        }

        var resolved = new List<ITool>(bound.Count);
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

    /// <summary>
    /// Resolve active skills for an agent.
    /// Skills are matched by id from the agent's SkillIds field.
    /// If SkillIds is not set, skills are matched by category against toolsets.
    /// Mirrors Hermes's skill injection mechanism.
    /// </summary>
    private List<Skills.ISkill> ResolveActiveSkills(Agent agent)
    {
        var result = new List<Skills.ISkill>();
        if (skillRegistry is null) return result;

        // If the agent declares explicit skill ids, use those.
        // Otherwise, auto-include all registered skills whose RequiredToolIds
        // are a subset of what the agent already has — same as Hermes's posture toolsets.
        var bound = ToolRegistry.ParseToolIds(agent.ToolIds);
        var boundSet = new HashSet<string>(bound, StringComparer.OrdinalIgnoreCase);

        foreach (var skill in skillRegistry.All())
        {
            // Include skill if all its required tools are bound to the agent
            if (skill.RequiredToolIds.All(id => boundSet.Contains(id)))
                result.Add(skill);
        }
        return result;
    }

    /// <summary>Build the combined system prompt contribution from active skills.</summary>
    private static string BuildSkillSystemPrompt(IReadOnlyList<Skills.ISkill> activeSkills)
    {
        if (activeSkills.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.Append("## Active workflow skills\n");
        sb.Append("The following specialized workflows are available for this agent:\n\n");
        foreach (var skill in activeSkills)
        {
            sb.Append($"### {skill.Name} (`{skill.Id}`)\n");
            sb.Append(skill.SystemPrompt.Trim());
            sb.Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static (bool ok, string? error, ITool? skill) LookupAllowedSkill(string toolName, IReadOnlyList<ITool> allowed)    {
        foreach (var s in allowed)
        {
            if (string.Equals(s.Id, toolName, StringComparison.OrdinalIgnoreCase)) return (true, null, s);
            foreach (var t in s.Tools)
                if (string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase)) return (true, null, s);
        }
        return (false, $"Tool '{toolName}' is not bound to this agent or not enabled.", null);
    }

    private static bool IsCompleteJsonObject(string s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
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

    private static string BuildCeoModeAComplianceSupplementIfNeeded(string agentId, string assistantText)
    {
        if (!string.Equals(agentId, CeoStrategicSupervisorAgentId, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var missingSections = new List<string>(6);
        if (!assistantText.Contains("Executive recommendation", StringComparison.OrdinalIgnoreCase)) missingSections.Add("Executive recommendation");
        if (!assistantText.Contains("Top risks", StringComparison.OrdinalIgnoreCase)) missingSections.Add("Top risks");
        if (!assistantText.Contains("Top opportunities", StringComparison.OrdinalIgnoreCase)) missingSections.Add("Top opportunities");
        if (!assistantText.Contains("Evidence map", StringComparison.OrdinalIgnoreCase)) missingSections.Add("Evidence map");
        if (!assistantText.Contains("Evidence gaps and assumptions", StringComparison.OrdinalIgnoreCase)) missingSections.Add("Evidence gaps and assumptions");
        if (!assistantText.Contains("Remediation actions", StringComparison.OrdinalIgnoreCase)) missingSections.Add("Remediation actions");

        var missingFields = new List<string>(2);
        if (!assistantText.Contains("Decision:", StringComparison.OrdinalIgnoreCase)) missingFields.Add("Decision");
        if (!assistantText.Contains("Confidence:", StringComparison.OrdinalIgnoreCase)) missingFields.Add("Confidence");

        if (missingSections.Count == 0 && missingFields.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("[Mode A schema compliance notice]");
        sb.AppendLine("The advisory response is missing required CEO Mode A structure. Please complete the schema below before actioning the recommendation.");
        if (missingSections.Count > 0)
            sb.AppendLine("Missing sections: " + string.Join(", ", missingSections) + ".");
        if (missingFields.Count > 0)
            sb.AppendLine("Missing required fields: " + string.Join(", ", missingFields) + ".");
        sb.AppendLine();
        sb.AppendLine("1) Executive recommendation");
        sb.AppendLine("- Decision: Approve | Conditional Approve | Reject");
        sb.AppendLine("- Confidence: High | Medium | Low");
        sb.AppendLine("- One-sentence rationale");
        sb.AppendLine("2) Top risks (maximum 3)");
        sb.AppendLine("3) Top opportunities (maximum 3)");
        sb.AppendLine("4) Evidence map");
        sb.AppendLine("5) Evidence gaps and assumptions");
        sb.AppendLine("6) Remediation actions");
        return sb.ToString();
    }

    private static string BuildPrompt(
        string? globalSystemPrompt,
        string systemPrompt,
        string? scenarioNotes,
        string? workDirectory,
        string userMessage,
        IReadOnlyList<RagContextChunk> chunks,
        IReadOnlyList<HistoryTurn> history,
        IReadOnlyList<ITool> tools,
        int maxToolCalls = DefaultMaxToolCallsPerTurn,
        string? skillSystemPrompt = null)
    {
        var sb = new StringBuilder();
        // Always inject current date so the model knows "today" without needing a tool call.
        sb.Append($"Today's date is {DateTime.Now:dddd, MMMM d, yyyy}.\n\n");
        if (!string.IsNullOrWhiteSpace(globalSystemPrompt))
        {
            sb.Append(globalSystemPrompt!.Trim());
            sb.Append("\n\n");
        }
        sb.Append(systemPrompt.Trim());

        // Inject skill system prompts (Hermes-style: active skills contribute instructions)
        if (!string.IsNullOrWhiteSpace(skillSystemPrompt))
        {
            sb.Append("\n\n");
            sb.Append(skillSystemPrompt!.Trim());
        }
        sb.Append("\n\nAgent execution loop (always follow):\n");
        sb.Append("• Thought: decide the next best step based on the user goal and current evidence.\n");
        sb.Append("• Act: when a tool materially helps, call the most relevant tool with precise arguments.\n");
        sb.Append("• Observe: inspect the tool result, update your plan, and either continue the loop or produce the final answer.\n");
        sb.Append("• You should think step by step in order to fulfill the objective with reasoning divided into Thought/Action/Observation steps that can be repeated multiple times when needed.\n");
        sb.Append("• Keep this loop internal; provide concise user-facing outputs.\n");
        sb.Append("• Never invent tool outputs, files, or facts. If evidence is missing after available actions, state the gap clearly.\n");
        sb.Append("• Never claim a tool call succeeded, write out a fabricated <tool_result>, or state that a file/action was completed unless you actually emitted the tool call and the system gave you back a real result to report.\n");
        sb.Append("• If a request requires a tool that is not enabled, unavailable, or failed at runtime, explicitly state the exact tool name and the reason.\n");
        if (!string.IsNullOrWhiteSpace(workDirectory))
        {
            var resolvedWorkDir = workDirectory.Replace("\r", "").Replace("\n", " ");
            sb.Append("\n\nFilesystem context:\n");
            sb.Append("• You have a writable per-conversation work directory on the server.\n");
            sb.Append("• For this conversation, the exact directory is: ").Append(resolvedWorkDir).Append('\n');
            sb.Append("• If the user configured a Work folder, this conversation directory lives under that root as a subfolder created automatically for this conversation.\n");
            sb.Append("• Word, Excel, PDF, report, image, and code tools read from or save to this directory automatically, usually by filename only.\n");
            sb.Append("• This work directory is explicitly authorized by the signed-in user and is in scope for this conversation.\n");
            sb.Append("• You may read, create, update, move, rename, summarize, and delete files in this directory when the user's request requires it.\n");
            sb.Append("• If client.fs tools are available, files exposed through the user's shared folder or copied into the work directory are also explicitly user-authorized for this conversation.\n");
            sb.Append("• Do not refuse or warn about privacy, consent, or permission concerns for files that are already in this authorized work directory or exposed through the available file tools.\n");
            sb.Append("• Only refuse filesystem actions that are outside the available tools, outside the authorized work directory/shared folder scope, or clearly unrelated to the user's request.\n");
        }
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
            sb.Append(maxToolCalls).Append(" tool calls per turn.\n\nAvailable tools:\n");
            sb.Append("• If the user asks for a concrete file deliverable (for example Excel, Word, PowerPoint, or PDF) and the matching tool is available, you must call that tool before your final answer.\n");
            sb.Append("• Do not claim a file was created, modified, or analyzed unless the corresponding tool call succeeded.\n");
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
            var officeRules = BuildOfficeWorkflowRules(tools);
            if (officeRules.Length > 0) sb.Append(officeRules);
            var chainingHints = BuildToolChainingHints(tools);
            if (chainingHints.Length > 0) sb.Append(chainingHints);
        }
        if (!string.IsNullOrWhiteSpace(scenarioNotes))
        {
            sb.Append("\n\nScenario notes for this agent:\n");
            sb.Append(scenarioNotes.Trim());
            sb.Append('\n');
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

    /// <summary>
    /// Returns a repeatable office-workflow block when office-document, template, or SQL data
    /// tools are available. This keeps recurring tasks on the same template family, makes
    /// tool usage mandatory for deliverable-oriented requests, and keeps SQL-backed workflows
    /// read-only and repeatable.
    /// </summary>
    private static string BuildOfficeWorkflowRules(IReadOnlyList<ITool> tools)
    {
        bool hasWord = false, hasExcel = false, hasPowerPoint = false, hasPdf = false, hasReport = false;
        bool hasWorkDir = false, hasSqlServer = false;
        foreach (var skill in tools)
        {
            foreach (var fn in skill.Tools)
            {
                if (fn.Name.StartsWith("workdir.", StringComparison.Ordinal)) hasWorkDir = true;
                if (fn.Name.StartsWith("sqlserver.", StringComparison.Ordinal)) hasSqlServer = true;
                if (fn.Name.StartsWith("word.", StringComparison.Ordinal)) hasWord = true;
                if (fn.Name.StartsWith("excel.", StringComparison.Ordinal)) hasExcel = true;
                if (fn.Name.StartsWith("powerpoint.", StringComparison.Ordinal)) hasPowerPoint = true;
                if (fn.Name.StartsWith("pdf.", StringComparison.Ordinal)) hasPdf = true;
                if (fn.Name.StartsWith("report.", StringComparison.Ordinal)) hasReport = true;
            }
        }

        if (!hasWord && !hasExcel && !hasPowerPoint && !hasPdf && !hasReport && !hasWorkDir && !hasSqlServer)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("\n\nOffice execution rules:\n");
        sb.Append("• For office-work requests, create or update the actual deliverable with the office tools. Do not stop at advice, outlines, or sample text unless the user explicitly asks for discussion only.\n");
        sb.Append("• Repeated task families must follow the same workflow and template family each time unless the user provides a different template or explicitly requests a different format.\n");
        sb.Append("• If a template file or prior example is available, read it first and preserve the section order, worksheet structure, slide flow, naming pattern, and recurring boilerplate.\n");
        sb.Append("• If no explicit template is available, use the built-in standard workflow below rather than inventing a new structure each time.\n");
        sb.Append("• Ask clarifying questions only when a missing input blocks a professional deliverable; otherwise proceed with the standard workflow and mark assumptions clearly.\n");
        sb.Append("• User-facing replies for office tasks must briefly confirm what file was created or updated, what sections/sheets/slides were added, and what assumptions still require review.\n");
        if (hasWorkDir)
        {
            sb.Append("• Start by checking the work directory for user-supplied templates, prior examples, SQL scripts, or connection profiles. Use workdir.list_files to discover them, workdir.read_text for text assets, and workdir.copy_file to duplicate a template into a new output file before editing it. Never overwrite the original template.\n");
        }
        if (hasSqlServer)
        {
            sb.Append("• When the deliverable depends on SQL Server data, inspect the schema first if it is not already known, then fetch only the dataset needed and move it into the requested document, workbook, or presentation. Do not dump raw database output back to the user unless they explicitly ask for it.\n");
        }
        sb.Append("Built-in standard workflows:\n");
        if (hasWord)
        {
            sb.Append("• Job advert / role profile (Word): Title, Role summary, Department / Reporting line, Key responsibilities, Essential requirements, Preferred requirements, Working pattern / location, What we offer, Application / next steps.\n");
            sb.Append("• Policy / procedure / formal document (Word): Purpose, Scope, Definitions, Responsibilities, Procedure, Exceptions, Review cycle, Document control.\n");
            sb.Append("• When a copied Word template contains placeholders, prefer word.replace_tokens over rebuilding the document, especially when headers, footers, and repeated boilerplate are already designed.\n");
            sb.Append("• Use word.insert_image for logos, signatures, exhibits, or screenshots, word.set_header_footer for polished running headers/footers, and word.set_section_layout when sections require landscape pages, custom margins, or multi-column layouts.\n");
        }
        if (hasExcel)
        {
            sb.Append("• KPI / operational / financial workbook (Excel): RawData sheet, Calculations sheet, Summary sheet, Charts or Dashboard sheet, Assumptions sheet; use explicit formulas, totals, variances, and clearly labelled units.\n");
            sb.Append("• Comparison / tracker workbook (Excel): input table, scoring or status columns, totals/subtotals, conditional formatting, filters, and print setup.\n");
            sb.Append("• When an Excel template exposes named ranges, prefer excel.read_named_range and excel.write_named_range instead of hard-coded cell addresses so the same template remains reusable after layout changes.\n");
            sb.Append("• Use excel.set_calculation_mode, excel.recalculate, and excel.evaluate_formula when workbook outputs depend on formula refresh, manual calculation templates, or a quick computed answer before writing back a final sheet.\n");
            sb.Append("• Use excel.create_pivot_table for native refreshable PivotTables when the workbook should stay interactive for the user, and use excel.create_pivot_report when you need a fixed grouped summary sheet that is easy to restyle or chart.\n");
            sb.Append("• Use excel.add_chart for native workbook visuals, including stacked column/bar, line, pie, doughnut, area, scatter, and combo charts with legend, data-label, axis-title, and per-series color controls; use excel.add_image, excel.add_text_box, excel.add_shape, excel.add_comment, and excel.add_hyperlink to finish the dashboard surface instead of leaving plain cells only.\n");
        }
        if (hasPowerPoint)
        {
            sb.Append("• Presentation deck (PowerPoint): Title, Executive summary, Key facts or metrics, Detailed analysis, Risks/issues, Decisions required, Next steps.\n");
            sb.Append("• When a PowerPoint template already has styled slides, duplicate the template slide and use powerpoint.replace_text or powerpoint.write_slide on the copy instead of recreating the layout manually; placeholder-aware updates preserve title/body targets even when template shape order varies.\n");
            sb.Append("• Prefer powerpoint.add_slide_from_template when the deck already contains branded layouts; use powerpoint.add_image for logos/screenshots and powerpoint.add_chart for presentation-ready metric visuals on the duplicated slide, including line trends and pie-style composition views.\n");
            sb.Append("• Use powerpoint.apply_branding when the deck needs a repeatable footer band, slide background, or text-color treatment across multiple slides, and use stacked chart variants when category totals must be shown by component instead of as separate grouped bars.\n");
        }
        if (hasPdf || hasReport)
            sb.Append("• Distribution-ready report: create the working document first, then generate PDF when the user needs a shareable final version.\n");
        if (hasSqlServer)
        {
            sb.Append("SQL Server execution rules:\n");
            sb.Append("• Prefer connection files and .sql files already stored in the work directory when available so the same data workflow is reused across runs.\n");
            sb.Append("• Use sqlserver.list_tables or sqlserver.describe_table before sqlserver.query when the schema is unfamiliar or when the user names a business concept rather than a table.\n");
            sb.Append("• SQL Server access is read-only. Do not attempt write, DDL, admin, or multi-statement SQL.\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns a "Tool chaining rules" block when the active tool set contains both
    /// <c>client.fs.*</c> bridge tools AND any of <c>excel.*</c>, <c>word.*</c>, <c>pdf.*</c>, or <c>powerpoint.*</c>.
    /// The injected text prevents the agent from calling server-side file tools directly
    /// on client-side paths without first copying the file into the server work directory.
    /// </summary>
    private static string BuildToolChainingHints(IReadOnlyList<ITool> tools)
    {
        bool hasClientFs = false, hasFileWork = false;
        foreach (var skill in tools)
        {
            foreach (var fn in skill.Tools)
            {
                if (fn.Name.StartsWith("client.fs.", StringComparison.Ordinal)) hasClientFs = true;
                if (fn.Name.StartsWith("excel.", StringComparison.Ordinal) ||
                    fn.Name.StartsWith("word.", StringComparison.Ordinal) ||
                    fn.Name.StartsWith("pdf.", StringComparison.Ordinal) ||
                    fn.Name.StartsWith("powerpoint.", StringComparison.Ordinal)) hasFileWork = true;
            }
            if (hasClientFs && hasFileWork) break;
        }
        if (!hasClientFs || !hasFileWork) return string.Empty;
        return "\n\nTool chaining rules:\n" +
               "• Files on the client PC (listed via client.fs.list): ALWAYS call client.fs.copyToWorkDir first to copy the file to the server work directory, then use excel.*, word.*, pdf.*, or powerpoint.* tools on the returned filename. Never pass a client-side path directly to those tools.\n" +
               "• To send a result file back to the client after creating or editing it: call client.fs.copyFromWorkDir with the server filename and the desired client destination path.\n";
    }
}
