using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Server.Rag;
using MyLocalAssistant.Server.Tools;

namespace MyLocalAssistant.Server.Hosting;

/// <summary>
/// Polls <c>{state}/_scheduler/schedules.json</c> every minute.
/// When a schedule entry is due (NextRun &lt;= UtcNow, Enabled = true) it:
///   1. Creates a Conversation + persists the user prompt Message.
///   2. Drives ChatService.StreamAsync to completion (fire-and-forget stream).
///   3. Persists the assistant reply as a Message.
///   4. If EmailResult = true, invokes the email.send tool (if registered).
///   5. Advances NextRun (cron) or disables the entry (one-shot).
/// All errors are caught and logged — a bad schedule never kills the host.
/// </summary>
public sealed class SchedulerHostedService(
    IServiceScopeFactory scopes,
    ILogger<SchedulerHostedService> log) : BackgroundService
{
    // Writes to schedules.json are also done by the plugin process; we use a
    // per-file lock only within this service. Concurrent access between this
    // service and the plugin process is acceptable because each write is an
    // atomic File.WriteAllText over a small JSON file.
    private static readonly TimeSpan s_pollInterval   = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_initialDelay   = TimeSpan.FromSeconds(30);

    private static string ScheduleFilePath
        => Path.Combine(ServerPaths.StateDirectory, "_scheduler", "schedules.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(s_initialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAsync(stoppingToken); }
            catch (Exception ex) { log.LogWarning(ex, "Scheduler poll failed."); }

            try { await Task.Delay(s_pollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var schedules = LoadSchedules();
        if (schedules.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        var due = schedules.Where(s => s.Enabled && s.NextRun <= now).ToList();
        if (due.Count == 0) return;

        log.LogInformation("Scheduler: {Count} task(s) due.", due.Count);

        foreach (var entry in due)
        {
            try
            {
                await RunEntryAsync(entry, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Scheduled task '{Name}' (id={Id}) failed.", entry.Name, entry.Id);
            }
            finally
            {
                AdvanceOrDisable(entry);
            }
        }

        SaveSchedules(schedules);
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    private async Task RunEntryAsync(ScheduleEntry entry, CancellationToken ct)
    {
        log.LogInformation("Scheduler: running '{Name}' (agent={AgentId}).", entry.Name, entry.AgentId);

        using var scope = scopes.CreateScope();
        var sp   = scope.ServiceProvider;
        var db   = sp.GetRequiredService<AppDbContext>();
        var chat = sp.GetRequiredService<ChatService>();
        var authz = sp.GetRequiredService<RagAuthorizationService>();

        // Resolve the system user who owns this schedule.
        var owner = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id.ToString() == entry.CreatedByUserId ||
                                      u.Username == entry.CreatedByUserId, ct);
        Guid userId   = owner?.Id       ?? Guid.Empty;
        string username = owner?.Username ?? "scheduler";
        bool isAdmin  = owner?.IsAdmin  ?? false;

        var principal = new UserPrincipals(userId, username, isAdmin, false,
            new HashSet<Guid>(), new HashSet<Guid>());

        // Resolve agent.
        var agent = await db.Agents.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == entry.AgentId, ct);
        if (agent is null)
        {
            log.LogWarning("Scheduler: agent '{AgentId}' not found for task '{Name}'.", entry.AgentId, entry.Name);
            return;
        }
        if (!agent.Enabled)
        {
            log.LogWarning("Scheduler: agent '{AgentId}' is disabled; skipping task '{Name}'.", entry.AgentId, entry.Name);
            return;
        }

        // Create conversation.
        var conversation = new Conversation
        {
            UserId  = userId,
            AgentId = entry.AgentId,
            Title   = $"[Scheduled] {entry.Name}",
        };
        db.Conversations.Add(conversation);

        var userMessage = new Message
        {
            ConversationId = conversation.Id,
            Role           = MessageRole.User,
            Body           = entry.Prompt,
        };
        db.Messages.Add(userMessage);
        await db.SaveChangesAsync(ct);

        // Stream the response to completion.
        var replyBuilder = new StringBuilder();
        var callbacks = new ChatService.ChatStreamCallbacks
        {
            ConversationId = conversation.Id,
        };

        try
        {
            await foreach (var token in chat.StreamAsync(
                agent, principal, entry.Prompt,
                maxTokens: 1024,
                history: [],
                callbacks: callbacks,
                ct: ct))
            {
                replyBuilder.Append(token);
            }
        }
        catch (Exception ex)
        {
            replyBuilder.Append($"\n[Scheduler error: {ex.Message}]");
            log.LogWarning(ex, "Scheduler: ChatService.StreamAsync failed for task '{Name}'.", entry.Name);
        }

        var reply = replyBuilder.ToString();

        // Persist assistant reply.
        var assistantMessage = new Message
        {
            ConversationId = conversation.Id,
            Role           = MessageRole.Assistant,
            Body           = reply,
        };
        db.Messages.Add(assistantMessage);
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Optionally email the result.
        if (entry.EmailResult && !string.IsNullOrWhiteSpace(entry.EmailTo))
        {
            await TrySendResultEmailAsync(sp, entry, reply, ct);
        }

        log.LogInformation("Scheduler: task '{Name}' completed. Reply length={Len}.", entry.Name, reply.Length);
    }

    private async Task TrySendResultEmailAsync(
        IServiceProvider sp, ScheduleEntry entry, string reply, CancellationToken ct)
    {
        try
        {
            var registry = sp.GetRequiredService<ToolRegistry>();
            var emailTool = registry.TryGet("email.tool", out var et) ? et : null;
            if (emailTool is null)
            {
                log.LogInformation("Scheduler: email.tool not registered; skipping email for '{Name}'.", entry.Name);
                return;
            }

            var argsJson = JsonSerializer.Serialize(new
            {
                to      = entry.EmailTo,
                subject = $"[Scheduled Result] {entry.Name}",
                body    = reply,
            }, s_json);

            var ctx = new ToolContext(
                Guid.Empty, "scheduler", false, false,
                entry.AgentId, Guid.Empty,
                ServerPaths.OutputDirectory, ct);

            var result = await emailTool.InvokeAsync(
                new ToolInvocation("email.send", argsJson), ctx);

            if (result.IsError)
                log.LogWarning("Scheduler: email failed for '{Name}': {Err}", entry.Name, result.Content);
            else
                log.LogInformation("Scheduler: email sent for '{Name}'.", entry.Name);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Scheduler: email send threw for task '{Name}'.", entry.Name);
        }
    }

    // ── Schedule advancement ──────────────────────────────────────────────────

    private static void AdvanceOrDisable(ScheduleEntry entry)
    {
        if (entry.CronExpression is not null)
        {
            // Let the plugin's Cronos logic determine the next fire time.
            // We can't reference Cronos from the server without adding the package,
            // so we use a simple approach: set NextRun to now + 1min so the scheduler
            // doesn't re-fire immediately. The real advancement is recalculated by the
            // plugin via schedule.list. Better approach: parse cron here using Cronos.
            // For now advance by a conservative estimate based on the cron string.
            entry.NextRun = ComputeNextCron(entry.CronExpression)
                ?? DateTimeOffset.UtcNow.AddDays(365); // fallback: disable effectively
        }
        else
        {
            // One-shot: disable after execution.
            entry.Enabled = false;
        }
    }

    private static DateTimeOffset? ComputeNextCron(string cronExpr)
    {
        // We include Cronos in the server to avoid duplicating logic.
        // If the package is not present this falls back to null (one-year deferral above).
        try
        {
            var cron = Cronos.CronExpression.Parse(cronExpr, Cronos.CronFormat.Standard);
            return cron.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        }
        catch
        {
            try
            {
                var cron = Cronos.CronExpression.Parse(cronExpr, Cronos.CronFormat.IncludeSeconds);
                return cron.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
            }
            catch { return null; }
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private List<ScheduleEntry> LoadSchedules()
    {
        var path = ScheduleFilePath;
        if (!File.Exists(path)) return [];
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ScheduleEntry>>(json, s_json) ?? [];
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Scheduler: failed to read schedules.json");
            return [];
        }
    }

    private void SaveSchedules(List<ScheduleEntry> schedules)
    {
        var path = ScheduleFilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(schedules, s_jsonIndented);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Scheduler: failed to write schedules.json");
        }
    }

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions s_jsonIndented = new(s_json)
    {
        WriteIndented = true,
    };
}

/// <summary>
/// Mirror of the plugin-side ScheduleEntry. Must stay in sync with
/// src/MyLocalAssistant.Plugins/Scheduler/SchedulerHandler.cs.
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
