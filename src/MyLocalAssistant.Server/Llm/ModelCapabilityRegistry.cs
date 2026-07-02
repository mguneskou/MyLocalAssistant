using System.Text.Json;
using System.Text.Json.Serialization;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// Capability snapshot for a single model. <see cref="Tools"/> matches the
/// constants in <see cref="ToolCallProtocols"/>. <c>"tags"</c> (text-grammar) and
/// <c>"native"</c> (provider structured tool-calling, currently Anthropic only) are wired;
/// <c>"json"</c> is reserved for a future OpenAI-native function-calling path.
/// <see cref="ContextK"/> is the practical context size in thousands of tokens
/// the model can be trusted with for the entire prompt.
/// </summary>
public sealed record ModelCapability(string Tools, int ContextK)
{
    public static readonly ModelCapability Default = new(ToolCallProtocols.None, 4);
}

/// <summary>
/// Read-only catalog answering "is this model capable of tool calling, and how
/// much context does it have?". Loads <c>config/model-capabilities.json</c> if
/// present (relative to the server install dir), falling back to a small built-in
/// allow-list for known tool-capable instruct families. Substring match against
/// the lower-cased model id, longest-match wins.
/// </summary>
public sealed class ModelCapabilityRegistry
{
    private readonly IReadOnlyList<KeyValuePair<string, ModelCapability>> _entries;
    private readonly ILogger<ModelCapabilityRegistry> _log;

    // Conservative defaults: only families with proven tag-style tool use end up in "tags".
    private static readonly KeyValuePair<string, ModelCapability>[] s_builtInDefaults =
    {
        // Local model families. NOTE: patterns are matched as a substring of the
        // lower-cased catalog id, so they must line up with the ids actually shipped in
        // Resources/model-catalog.json (e.g. "llama32-3b-q4km", "gemma3-12b-q4km").
        new("qwen2.5",      new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("qwen2",        new ModelCapability(ToolCallProtocols.Tags, 32)),  // catalog ids "qwen25-*" (no dot) + Qwen2/2.5 family
        new("qwen3",        new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("llama-3.1",    new ModelCapability(ToolCallProtocols.Tags, 16)),
        new("llama-3.2",    new ModelCapability(ToolCallProtocols.Tags, 16)),
        new("llama-3.3",    new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("llama32",      new ModelCapability(ToolCallProtocols.Tags, 16)),  // catalog id "llama32-3b" (no dot/hyphen)
        new("gemma",        new ModelCapability(ToolCallProtocols.Tags, 8)),   // gemma3-* local + gemma-*-it
        new("phi4",         new ModelCapability(ToolCallProtocols.Tags, 16)),  // phi4-mini, phi4-14b
        new("granite",      new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("mixtral",      new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("gpt-oss",      new ModelCapability(ToolCallProtocols.Tags, 128)), // gpt-oss-120b
        new("deepseek-r1",  new ModelCapability(ToolCallProtocols.Tags, 32)),  // emits <think> before <tool_call>; parser tolerates it
        new("mistral-nemo", new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("hermes-3",     new ModelCapability(ToolCallProtocols.Tags, 16)),
        new("command-r",    new ModelCapability(ToolCallProtocols.Tags, 32)),
        // Cloud providers — Tags protocol (our SSE streaming tool-call wrapper). The prompt
        // (with the <tool_call> grammar) is sent verbatim as a single user message, so tool
        // calling works identically to the local tag path.
        new("openai-",      new ModelCapability(ToolCallProtocols.Tags, 128)), // openai-gpt-4o-mini, openai-gpt-4.1*
        // Anthropic uses Native: Claude models are trained on their own tool-call conventions
        // and can silently drift away from the app's custom <tool_call> tag (observed with
        // Claude Haiku 4.5 fabricating an entire fake <function_calls>/<tool_result> exchange
        // and reporting success while no tool ever ran). AnthropicChatProvider implements
        // INativeToolChatProvider so ChatService drives Claude's real tools/tool_use API instead.
        new("anthropic-",   new ModelCapability(ToolCallProtocols.Native, 200)), // anthropic-claude-*
        new("groq-",        new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("gemini-",      new ModelCapability(ToolCallProtocols.Tags, 128)),
        new("mistral-",     new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("cerebras-",    new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("together-",    new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("cohere-",      new ModelCapability(ToolCallProtocols.Tags, 32)),
    };

    public ModelCapabilityRegistry(ILogger<ModelCapabilityRegistry> log, IHostEnvironment env)
    {
        _log = log;
        var entries = new List<KeyValuePair<string, ModelCapability>>(s_builtInDefaults);
        var path = Path.Combine(env.ContentRootPath, "config", "model-capabilities.json");
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, ModelCapabilityFile>>(json,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip });
                if (parsed is not null)
                {
                    foreach (var (key, val) in parsed)
                    {
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        var tools = string.IsNullOrWhiteSpace(val.Tools) ? ToolCallProtocols.None : val.Tools.Trim().ToLowerInvariant();
                        if (tools is not (ToolCallProtocols.None or ToolCallProtocols.Tags or ToolCallProtocols.Json or ToolCallProtocols.Native))
                            tools = ToolCallProtocols.None;
                        entries.Add(new(key.Trim().ToLowerInvariant(), new ModelCapability(tools, Math.Max(1, val.ContextK))));
                    }
                    _log.LogInformation("Loaded {Count} model-capability override(s) from {Path}.", parsed.Count, path);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load {Path}; using built-in defaults only.", path);
            }
        }
        // Longest key wins on substring match.
        _entries = entries.OrderByDescending(e => e.Key.Length).ToArray();
    }

    public ModelCapability Get(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return ModelCapability.Default;
        var key = modelId.ToLowerInvariant();
        foreach (var (pattern, cap) in _entries)
            if (key.Contains(pattern, StringComparison.Ordinal))
                return cap;
        return ModelCapability.Default;
    }

    private sealed class ModelCapabilityFile
    {
        [JsonPropertyName("tools")] public string Tools { get; set; } = "none";
        [JsonPropertyName("contextK")] public int ContextK { get; set; } = 4;
    }
}
