using System.Text.Json;
using System.Text.Json.Serialization;
using MyLocalAssistant.Plugin.Shared;

namespace MyLocalAssistant.Plugins.MemoryTool;

/// <summary>
/// Persistent per-user key-value memory store backed by a JSON file.
/// Storage: {dataRoot}/_memory/{userId}/memories.json
/// Config JSON: {"dataRoot":"C:/path/to/server/state"}
/// </summary>
internal sealed class MemoryToolHandler : IPluginTool
{
    private string _dataRoot = "";
    private readonly object _fileLock = new();

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        if (!string.IsNullOrWhiteSpace(cfg?.DataRoot))
            _dataRoot = cfg.DataRoot;
    }

    public Task<PluginToolResult> InvokeAsync(
        string toolName, JsonElement arguments, PluginContext context, CancellationToken ct)
    {
        return toolName switch
        {
            "memory.save"   => Task.FromResult(Save(arguments, context)),
            "memory.recall" => Task.FromResult(Recall(arguments, context)),
            "memory.list"   => Task.FromResult(List(context)),
            "memory.delete" => Task.FromResult(Delete(arguments, context)),
            "memory.search" => Task.FromResult(Search(arguments, context)),
            _               => Task.FromResult(PluginToolResult.Error($"Unknown tool '{toolName}'")),
        };
    }

    // ── Operations ────────────────────────────────────────────────────────────

    private PluginToolResult Save(JsonElement args, PluginContext ctx)
    {
        var key   = args.TryGetProperty("key",   out var k) ? k.GetString() ?? "" : "";
        var value = args.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(key)) return PluginToolResult.Error("key is required");

        var memories = LoadMemories(ctx.UserId);
        memories[key] = new MemoryEntry { Value = value, UpdatedAt = DateTimeOffset.UtcNow };
        SaveMemories(ctx.UserId, memories);
        return PluginToolResult.Ok($"Memory '{key}' saved.");
    }

    private PluginToolResult Recall(JsonElement args, PluginContext ctx)
    {
        var key = args.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(key)) return PluginToolResult.Error("key is required");

        var memories = LoadMemories(ctx.UserId);
        return memories.TryGetValue(key, out var entry)
            ? PluginToolResult.Ok(entry.Value)
            : PluginToolResult.Error($"No memory found for key '{key}'");
    }

    private PluginToolResult List(PluginContext ctx)
    {
        var memories = LoadMemories(ctx.UserId);
        if (memories.Count == 0) return PluginToolResult.Ok("No memories stored.");

        var lines = memories
            .OrderBy(kv => kv.Key)
            .Select(kv => $"• {kv.Key}: {Truncate(kv.Value.Value, 80)}");
        return PluginToolResult.Ok(string.Join("\n", lines));
    }

    private PluginToolResult Delete(JsonElement args, PluginContext ctx)
    {
        var key = args.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(key)) return PluginToolResult.Error("key is required");

        var memories = LoadMemories(ctx.UserId);
        if (!memories.Remove(key)) return PluginToolResult.Error($"No memory found for key '{key}'");
        SaveMemories(ctx.UserId, memories);
        return PluginToolResult.Ok($"Memory '{key}' deleted.");
    }

    private PluginToolResult Search(JsonElement args, PluginContext ctx)
    {
        var query      = args.TryGetProperty("query",       out var q)  ? q.GetString()?.ToLowerInvariant() ?? "" : "";
        var maxResults = args.TryGetProperty("max_results", out var mr) && mr.TryGetInt32(out var n) ? n : 10;

        var memories = LoadMemories(ctx.UserId);
        var matches  = memories
            .Where(kv => kv.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                      || kv.Value.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key)
            .Take(maxResults)
            .Select(kv => $"• {kv.Key}: {Truncate(kv.Value.Value, 120)}");

        var result = string.Join("\n", matches);
        return result.Length == 0
            ? PluginToolResult.Ok($"No memories matching '{query}'.")
            : PluginToolResult.Ok(result);
    }

    // ── Storage ───────────────────────────────────────────────────────────────

    private string MemoryFilePath(string userId)
    {
        var root = string.IsNullOrWhiteSpace(_dataRoot) ? Path.GetTempPath() : _dataRoot;
        var dir  = Path.Combine(root, "_memory", userId);
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
                return JsonSerializer.Deserialize<Dictionary<string, MemoryEntry>>(json, s_json)
                    ?? new();
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

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

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
