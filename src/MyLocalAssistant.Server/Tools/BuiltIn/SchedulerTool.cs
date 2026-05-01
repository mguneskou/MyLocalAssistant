using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// Manages scheduled agent tasks. The server-side SchedulerHostedService periodically reads
/// the schedules file and executes due entries.
/// Storage: {StateDirectory}/_scheduler/schedules.json
/// Config JSON: {"dataRoot":"C:/path/to/override"}
/// </summary>
internal sealed class SchedulerTool : ITool
{
    // ── ITool metadata ────────────────────────────────────────────────────────

    public string  Id          => "scheduler";
    public string  Name        => "Scheduler";
    public string  Description => "Manages scheduled agent tasks. Supports one-shot (ISO 8601 datetime) and recurring (cron expression) schedules.";
    public string  Category    => "Productivity";
    public string  Source      => ToolSources.BuiltIn;
    public string? Version     => null;
    public string? Publisher   => "MyLocalAssistant";
    public string? KeyId       => null;

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "schedule.create",
            Description: "Create a new scheduled task. 'when' accepts an ISO 8601 datetime (one-shot) or a cron expression (recurring, 5 or 6 fields).",
            ArgumentsSchemaJson: """{"type":"object","properties":{"name":{"type":"string","description":"Human-readable schedule name"},"when":{"type":"string","description":"ISO 8601 datetime or cron expression"},"agent_id":{"type":"string","description":"ID of the agent to run"},"prompt":{"type":"string","description":"Prompt to send to the agent"},"save_result":{"type":"boolean","description":"Save agent reply as a conversation"},"email_result":{"type":"boolean","description":"Email the result when done"},"email_to":{"type":"string","description":"Email address for the result (if email_result is true)"}},"required":["name","when","agent_id","prompt"]}"""),
        new ToolFunctionDto(
            Name: "schedule.list",
            Description: "List all active schedules created by the current user.",
            ArgumentsSchemaJson: """{"type":"object","properties":{}}"""),
        new ToolFunctionDto(
            Name: "schedule.cancel",
            Description: "Cancel a scheduled task by its ID (prefix match accepted).",
            ArgumentsSchemaJson: """{"type":"object","properties":{"id":{"type":"string","description":"Schedule ID or prefix"}},"required":["id"]}"""),
        new ToolFunctionDto(
            Name: "schedule.run_now",
            Description: "Trigger a scheduled task to run on the next poll cycle (within ~1 minute).",
            ArgumentsSchemaJson: """{"type":"object","properties":{"id":{"type":"string","description":"Schedule ID or prefix"}},"required":["id"]}"""),
    };

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Json, MinContextK: 4);

    // ── Config ────────────────────────────────────────────────────────────────

    private string _dataRoot = ServerPaths.StateDirectory;
    private readonly object _fileLock = new();

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions s_jsonIndented = new(s_json)
    {
        WriteIndented = true,
    };

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        if (!string.IsNullOrWhiteSpace(cfg?.DataRoot)) _dataRoot = cfg.DataRoot;
    }

    // ── ITool.InvokeAsync ─────────────────────────────────────────────────────

    public Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var args   = doc.RootElement.Clone();
        var userId = ctx.UserId.ToString();

        var result = call.ToolName switch
        {
            "schedule.create"  => Create(args, userId),
            "schedule.list"    => List(userId),
            "schedule.cancel"  => Cancel(args, userId),
            "schedule.run_now" => RunNow(args, userId),
            _                  => ToolResult.Error($"Unknown tool '{call.ToolName}'"),
        };
        return Task.FromResult(result);
    }

    // ── Operations ────────────────────────────────────────────────────────────

    private ToolResult Create(JsonElement args, string userId)
    {
        var name       = args.TryGetProperty("name",         out var n)  ? n.GetString()  ?? "" : "";
        var when       = args.TryGetProperty("when",         out var w)  ? w.GetString()  ?? "" : "";
        var agentId    = args.TryGetProperty("agent_id",     out var ai) ? ai.GetString() ?? "" : "";
        var prompt     = args.TryGetProperty("prompt",       out var p)  ? p.GetString()  ?? "" : "";
        var saveResult = args.TryGetProperty("save_result",  out var sr) && sr.ValueKind == JsonValueKind.True;
        var mailResult = args.TryGetProperty("email_result", out var mr) && mr.ValueKind == JsonValueKind.True;
        var emailTo    = args.TryGetProperty("email_to",     out var et) ? et.GetString() : null;

        if (string.IsNullOrWhiteSpace(name))    return ToolResult.Error("name is required");
        if (string.IsNullOrWhiteSpace(when))    return ToolResult.Error("when is required");
        if (string.IsNullOrWhiteSpace(agentId)) return ToolResult.Error("agent_id is required");
        if (string.IsNullOrWhiteSpace(prompt))  return ToolResult.Error("prompt is required");

        string?        cronExpr = null;
        DateTimeOffset nextRun;

        if (TryParseCron(when, out var cron))
        {
            cronExpr = when;
            nextRun  = cron!.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc) ?? DateTimeOffset.UtcNow;
        }
        else if (DateTimeOffset.TryParse(when, out var dt))
        {
            nextRun = dt.ToUniversalTime();
        }
        else
        {
            return ToolResult.Error(
                $"'when' must be an ISO 8601 datetime or a cron expression (5 or 6 fields). Got: '{when}'");
        }

        var entry = new ScheduleEntry
        {
            Id              = Guid.NewGuid().ToString("N"),
            Name            = name,
            AgentId         = agentId,
            Prompt          = prompt,
            SaveResult      = saveResult,
            EmailResult     = mailResult,
            EmailTo         = emailTo,
            CronExpression  = cronExpr,
            NextRun         = nextRun,
            Enabled         = true,
            CreatedByUserId = userId,
            CreatedAt       = DateTimeOffset.UtcNow,
        };

        var schedules = Load();
        schedules.Add(entry);
        Save(schedules);
        return ToolResult.Ok($"Scheduled '{name}' (id={entry.Id}). Next run: {entry.NextRun:u}");
    }

    private ToolResult List(string userId)
    {
        var schedules = Load()
            .Where(s => s.Enabled && s.CreatedByUserId == userId)
            .OrderBy(s => s.NextRun)
            .ToList();

        if (schedules.Count == 0)
            return ToolResult.Ok("No active schedules.");

        var lines = schedules.Select(s =>
            $"• [{s.Id[..8]}] {s.Name} | next: {s.NextRun:u} | agent: {s.AgentId}" +
            (s.CronExpression is not null ? $" | cron: {s.CronExpression}" : " | one-shot"));

        return ToolResult.Ok(string.Join("\n", lines));
    }

    private ToolResult Cancel(JsonElement args, string userId)
    {
        var id = args.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id)) return ToolResult.Error("id is required");

        var schedules = Load();
        var entry     = schedules.FirstOrDefault(
            s => s.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase) &&
                 s.CreatedByUserId == userId);

        if (entry is null)
            return ToolResult.Error($"Schedule '{id}' not found or not owned by you.");

        entry.Enabled = false;
        Save(schedules);
        return ToolResult.Ok($"Schedule '{entry.Name}' cancelled.");
    }

    private ToolResult RunNow(JsonElement args, string userId)
    {
        var id = args.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id)) return ToolResult.Error("id is required");

        var schedules = Load();
        var entry     = schedules.FirstOrDefault(
            s => s.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase) &&
                 s.CreatedByUserId == userId);

        if (entry is null)
            return ToolResult.Error($"Schedule '{id}' not found or not owned by you.");

        entry.NextRun = DateTimeOffset.UtcNow.AddSeconds(-1);
        entry.Enabled = true;
        Save(schedules);
        return ToolResult.Ok($"Schedule '{entry.Name}' queued for immediate execution.");
    }

    // ── Storage ───────────────────────────────────────────────────────────────

    private string ScheduleFilePath()
    {
        var dir = Path.Combine(_dataRoot, "_scheduler");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "schedules.json");
    }

    private List<ScheduleEntry> Load()
    {
        var path = ScheduleFilePath();
        lock (_fileLock)
        {
            if (!File.Exists(path)) return [];
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<ScheduleEntry>>(json, s_json) ?? [];
            }
            catch { return []; }
        }
    }

    private void Save(List<ScheduleEntry> schedules)
    {
        var path = ScheduleFilePath();
        lock (_fileLock)
        {
            var json = JsonSerializer.Serialize(schedules, s_jsonIndented);
            File.WriteAllText(path, json);
        }
    }

    // ── Cron helpers ──────────────────────────────────────────────────────────

    private static bool TryParseCron(string expr, out CronExpression? cron)
    {
        try
        {
            try   { cron = CronExpression.Parse(expr, CronFormat.IncludeSeconds); return true; }
            catch { cron = CronExpression.Parse(expr, CronFormat.Standard);       return true; }
        }
        catch { cron = null; return false; }
    }

    private sealed class Config
    {
        [JsonPropertyName("dataRoot")] public string? DataRoot { get; set; }
    }
}

/// <summary>
/// Serialized schedule record shared between SchedulerTool and SchedulerHostedService.
/// </summary>
internal sealed class ScheduleEntry
{
    [JsonPropertyName("id")]              public string         Id              { get; set; } = "";
    [JsonPropertyName("name")]            public string         Name            { get; set; } = "";
    [JsonPropertyName("agentId")]         public string         AgentId         { get; set; } = "";
    [JsonPropertyName("prompt")]          public string         Prompt          { get; set; } = "";
    [JsonPropertyName("saveResult")]      public bool           SaveResult      { get; set; }
    [JsonPropertyName("emailResult")]     public bool           EmailResult     { get; set; }
    [JsonPropertyName("emailTo")]         public string?        EmailTo         { get; set; }
    [JsonPropertyName("cronExpression")]  public string?        CronExpression  { get; set; }
    [JsonPropertyName("nextRun")]         public DateTimeOffset NextRun         { get; set; }
    [JsonPropertyName("enabled")]         public bool           Enabled         { get; set; }
    [JsonPropertyName("createdByUserId")] public string         CreatedByUserId { get; set; } = "";
    [JsonPropertyName("createdAt")]       public DateTimeOffset CreatedAt       { get; set; }
}
