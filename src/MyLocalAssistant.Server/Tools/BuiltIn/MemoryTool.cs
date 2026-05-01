using System.Text.Json;
using System.Text.Json.Serialization;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// Persistent per-user key-value memory store backed by a JSON file.
/// Storage: {StateDirectory}/_memory/{userId}/memories.json
/// Config JSON: {"dataRoot":"C:/path/to/override"}
/// </summary>
internal sealed class MemoryTool : ITool
{
    // ── ITool metadata ────────────────────────────────────────────────────────

    public string  Id          => "memory.tool";
    public string  Name        => "Memory Tool";
    public string  Description => "Persistent per-user key-value memory. Models can save, recall, list, delete, and search memories that survive across conversations.";
    public string  Category    => "Productivity";
    public string  Source      => ToolSources.BuiltIn;
    public string? Version     => null;
    public string? Publisher   => "MyLocalAssistant";
    public string? KeyId       => null;

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "memory.save",
            Description: "Save or update a memory entry for the current user. Use descriptive keys (e.g. 'user.name', 'project.deadline').",
            ArgumentsSchemaJson: """{"type":"object","properties":{"key":{"type":"string","description":"Unique memory key"},"value":{"type":"string","description":"Value to store"}},"required":["key","value"]}"""),
        new ToolFunctionDto(
            Name: "memory.recall",
            Description: "Retrieve a specific memory entry by key.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"key":{"type":"string","description":"Memory key to retrieve"}},"required":["key"]}"""),
        new ToolFunctionDto(
            Name: "memory.list",
            Description: "List all stored memory keys and a preview of their values for the current user.",
            ArgumentsSchemaJson: """{"type":"object","properties":{}}"""),
        new ToolFunctionDto(
            Name: "memory.delete",
            Description: "Delete a memory entry by key.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"key":{"type":"string","description":"Memory key to delete"}},"required":["key"]}"""),
        new ToolFunctionDto(
            Name: "memory.search",
            Description: "Search memories by keyword across both keys and values.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"query":{"type":"string","description":"Search keyword"},"max_results":{"type":"integer","description":"Maximum results to return (default 10)"}},"required":["query"]}"""),
    };

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Json, MinContextK: 4);

    // ── Config ────────────────────────────────────────────────────────────────

    private string _dataRoot = ServerPaths.StateDirectory;
    private readonly object _fileLock = new();

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        if (!string.IsNullOrWhiteSpace(cfg?.DataRoot))
            _dataRoot = cfg.DataRoot;
    }

    // ── ITool.InvokeAsync ─────────────────────────────────────────────────────

    public Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var args   = doc.RootElement.Clone(); // clone before doc is disposed
        var userId = ctx.UserId.ToString();

        var result = call.ToolName switch
        {
            "memory.save"   => Save(args, userId),
            "memory.recall" => Recall(args, userId),
            "memory.list"   => List(userId),
            "memory.delete" => Delete(args, userId),
            "memory.search" => Search(args, userId),
            _               => ToolResult.Error($"Unknown tool '{call.ToolName}'"),
        };
        return Task.FromResult(result);
    }

    // ── Operations ────────────────────────────────────────────────────────────

    private ToolResult Save(JsonElement args, string userId)
    {
        var key   = args.TryGetProperty("key",   out var k) ? k.GetString() ?? "" : "";
        var value = args.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(key)) return ToolResult.Error("key is required");

        var memories = LoadMemories(userId);
        memories[key] = new MemoryEntry { Value = value, UpdatedAt = DateTimeOffset.UtcNow };
        SaveMemories(userId, memories);
        return ToolResult.Ok($"Memory '{key}' saved.");
    }

    private ToolResult Recall(JsonElement args, string userId)
    {
        var key = args.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(key)) return ToolResult.Error("key is required");

        var memories = LoadMemories(userId);
        return memories.TryGetValue(key, out var entry)
            ? ToolResult.Ok(entry.Value)
            : ToolResult.Error($"No memory found for key '{key}'");
    }

    private ToolResult List(string userId)
    {
        var memories = LoadMemories(userId);
        if (memories.Count == 0) return ToolResult.Ok("No memories stored.");

        var lines = memories
            .OrderBy(kv => kv.Key)
            .Select(kv => $"• {kv.Key}: {Truncate(kv.Value.Value, 80)}");
        return ToolResult.Ok(string.Join("\n", lines));
    }

    private ToolResult Delete(JsonElement args, string userId)
    {
        var key = args.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(key)) return ToolResult.Error("key is required");

        var memories = LoadMemories(userId);
        if (!memories.Remove(key)) return ToolResult.Error($"No memory found for key '{key}'");
        SaveMemories(userId, memories);
        return ToolResult.Ok($"Memory '{key}' deleted.");
    }

    private ToolResult Search(JsonElement args, string userId)
    {
        var query      = args.TryGetProperty("query",       out var q)  ? q.GetString()?.ToLowerInvariant() ?? "" : "";
        var maxResults = args.TryGetProperty("max_results", out var mr) && mr.TryGetInt32(out var n) ? n : 10;

        var memories = LoadMemories(userId);
        var matches  = memories
            .Where(kv => kv.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                      || kv.Value.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key)
            .Take(maxResults)
            .Select(kv => $"• {kv.Key}: {Truncate(kv.Value.Value, 120)}");

        var result = string.Join("\n", matches);
        return result.Length == 0
            ? ToolResult.Ok($"No memories matching '{query}'.")
            : ToolResult.Ok(result);
    }

    // ── Storage ───────────────────────────────────────────────────────────────

    private string MemoryFilePath(string userId)
    {
        var dir = Path.Combine(_dataRoot, "_memory", userId);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "memories.json");
    }

    private Dictionary<string, MemoryEntry> LoadMemories(string userId)
    {
        var path = MemoryFilePath(userId);
        lock (_fileLock)
        {
            if (!File.Exists(path)) return new();
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, MemoryEntry>>(json, s_json) ?? new();
            }
            catch { return new(); }
        }
    }

    private void SaveMemories(string userId, Dictionary<string, MemoryEntry> memories)
    {
        var path = MemoryFilePath(userId);
        lock (_fileLock)
        {
            var json = JsonSerializer.Serialize(memories, s_json);
            File.WriteAllText(path, json);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private sealed class Config
    {
        [JsonPropertyName("dataRoot")] public string? DataRoot { get; set; }
    }

    private sealed class MemoryEntry
    {
        [JsonPropertyName("value")]     public string         Value     { get; set; } = "";
        [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
    }
}
