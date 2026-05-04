using System.Text.Json;
using System.Text.Json.Serialization;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// Capability snapshot for a single model. <see cref="Tools"/> matches the
/// constants in <see cref="ToolCallProtocols"/> (currently <c>"none"</c>,
/// <c>"tags"</c>, or <c>"json"</c>); only <c>"tags"</c> is wired in v2.1.
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
        // Local model families
        new("qwen2.5",      new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("qwen3",        new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("llama-3.1",    new ModelCapability(ToolCallProtocols.Tags, 16)),
        new("llama-3.2",    new ModelCapability(ToolCallProtocols.Tags, 16)),
        new("llama-3.3",    new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("mistral-nemo", new ModelCapability(ToolCallProtocols.Tags, 32)),
        new("hermes-3",     new ModelCapability(ToolCallProtocols.Tags, 16)),
        new("command-r",    new ModelCapability(ToolCallProtocols.Tags, 32)),
        // Cloud providers — all use the Tags protocol (our SSE streaming tool-call wrapper)
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
                        if (tools is not (ToolCallProtocols.None or ToolCallProtocols.Tags or ToolCallProtocols.Json))
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
