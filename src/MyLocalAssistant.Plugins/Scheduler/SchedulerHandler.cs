using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;
using MyLocalAssistant.Plugin.Shared;

namespace MyLocalAssistant.Plugins.Scheduler;

/// <summary>
/// Manages scheduled agent tasks. The server-side SchedulerHostedService periodically reads
/// the schedules file and executes due entries.
///
/// Config JSON: {"dataRoot":"C:/path/to/server/state"}
/// Storage: {dataRoot}/_scheduler/schedules.json
///
/// 'when' field accepts:
///   - ISO 8601 datetime string  → one-shot execution at that time
///   - Cron expression (5 or 6 fields) → recurring; uses Cronos library
/// </summary>
internal sealed class SchedulerHandler : IPluginTool
{
    private string _dataRoot = "";
    private readonly object _fileLock = new();

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        if (!string.IsNullOrWhiteSpace(cfg?.DataRoot)) _dataRoot = cfg.DataRoot;
    }

    public Task<PluginToolResult> InvokeAsync(
        string toolName, JsonElement arguments, PluginContext context, CancellationToken ct)
    {
        return toolName switch
        {
            "schedule.create"  => Task.FromResult(Create(arguments, context)),
            "schedule.list"    => Task.FromResult(List(context)),
            "schedule.cancel"  => Task.FromResult(Cancel(arguments, context)),
            "schedule.run_now" => Task.FromResult(RunNow(arguments, context)),
            _                  => Task.FromResult(PluginToolResult.Error($"Unknown tool '{toolName}'")),
        };
    }

    // ── Operations ────────────────────────────────────────────────────────────

    private PluginToolResult Create(JsonElement args, PluginContext ctx)
    {
        var name       = args.TryGetProperty("name",         out var n)   ? n.GetString()   ?? "" : "";
        var when       = args.TryGetProperty("when",         out var w)   ? w.GetString()   ?? "" : "";
        var agentId    = args.TryGetProperty("agent_id",     out var ai)  ? ai.GetString()  ?? "" : "";
        var prompt     = args.TryGetProperty("prompt",       out var p)   ? p.GetString()   ?? "" : "";
        var saveResult = args.TryGetProperty("save_result",  out var sr)  && sr.ValueKind == JsonValueKind.True;
        var mailResult = args.TryGetProperty("email_result", out var mr)  && mr.ValueKind == JsonValueKind.True;
        var emailTo    = args.TryGetProperty("email_to",     out var et)  ? et.GetString()  : null;

        if (string.IsNullOrWhiteSpace(name))    return PluginToolResult.Error("name is required");
        if (string.IsNullOrWhiteSpace(when))    return PluginToolResult.Error("when is required");
        if (string.IsNullOrWhiteSpace(agentId)) return PluginToolResult.Error("agent_id is required");
        if (string.IsNullOrWhiteSpace(prompt))  return PluginToolResult.Error("prompt is required");

        // Parse 'when' → either cron or one-shot datetime.
        string?          cronExpr = null;
        DateTimeOffset   nextRun;

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
            return PluginToolResult.Error(
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
            CreatedByUserId = ctx.UserId,
            CreatedAt       = DateTimeOffset.UtcNow,
        };

        var schedules = Load();
        schedules.Add(entry);
        Save(schedules);

        return PluginToolResult.Ok(
            $"Scheduled '{name}' (id={entry.Id}). Next run: {entry.NextRun:u}");
    }

    private PluginToolResult List(PluginContext ctx)
    {
        var schedules = Load()
            .Where(s => s.Enabled && s.CreatedByUserId == ctx.UserId)
            .OrderBy(s => s.NextRun)
            .ToList();

        if (schedules.Count == 0)
            return PluginToolResult.Ok("No active schedules.");

        var lines = schedules.Select(s =>
            $"• [{s.Id[..8]}] {s.Name} | next: {s.NextRun:u} | agent: {s.AgentId}" +
            (s.CronExpression is not null ? $" | cron: {s.CronExpression}" : " | one-shot"));

        return PluginToolResult.Ok(string.Join("\n", lines));
    }

    private PluginToolResult Cancel(JsonElement args, PluginContext ctx)
    {
        var id = args.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id)) return PluginToolResult.Error("id is required");

        var schedules = Load();
        var entry     = schedules.FirstOrDefault(
            s => s.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase) &&
                 s.CreatedByUserId == ctx.UserId);

        if (entry is null)
            return PluginToolResult.Error($"Schedule '{id}' not found or not owned by you.");

        entry.Enabled = false;
        Save(schedules);
        return PluginToolResult.Ok($"Schedule '{entry.Name}' cancelled.");
    }

    private PluginToolResult RunNow(JsonElement args, PluginContext ctx)
    {
        var id = args.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id)) return PluginToolResult.Error("id is required");

        var schedules = Load();
        var entry     = schedules.FirstOrDefault(
            s => s.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase) &&
                 s.CreatedByUserId == ctx.UserId);

        if (entry is null)
            return PluginToolResult.Error($"Schedule '{id}' not found or not owned by you.");

        // Setting NextRun in the past causes SchedulerHostedService to pick it up on next poll.
        entry.NextRun = DateTimeOffset.UtcNow.AddSeconds(-1);
        entry.Enabled = true;
        Save(schedules);
        return PluginToolResult.Ok($"Schedule '{entry.Name}' queued for immediate execution.");
    }

    // ── Storage ───────────────────────────────────────────────────────────────

    private string ScheduleFilePath()
    {
        var root = string.IsNullOrWhiteSpace(_dataRoot) ? Path.GetTempPath() : _dataRoot;
        var dir  = Path.Combine(root, "_scheduler");
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
            // Try 6-field (with seconds) first, then 5-field.
            try   { cron = CronExpression.Parse(expr, CronFormat.IncludeSeconds); return true; }
            catch { cron = CronExpression.Parse(expr, CronFormat.Standard);       return true; }
        }
        catch { cron = null; return false; }
    }

    // ── JSON options ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions s_jsonIndented = new(s_json)
    {
        WriteIndented = true,
    };

    private sealed class Config
    {
        [JsonPropertyName("dataRoot")] public string? DataRoot { get; set; }
    }
}

/// <summary>
/// Serialized schedule record. The SchedulerHostedService (server) reads this file and
/// executes entries whose NextRun &lt;= UtcNow and Enabled == true.
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
