namespace MyLocalAssistant.Server.Scheduling;

/// <summary>Type of job trigger.</summary>
public enum JobTriggerType
{
    /// <summary>Runs once at a specific UTC time.</summary>
    OneShot,
    /// <summary>Repeats on a cron expression (Cronos 5/6-field).</summary>
    Recurring,
}

/// <summary>
/// An Operations Scheduler job definition.  Extends the low-level schedule entry
/// with skill awareness, structured input, and delivery channel routing.
/// </summary>
public sealed class OperationsJob
{
    /// <summary>Stable unique id (GUID).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name        { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    // ── Trigger ──────────────────────────────────────────────────────────────

    public JobTriggerType TriggerType { get; set; }

    /// <summary>ISO-8601 UTC datetime for OneShot jobs.</summary>
    public DateTimeOffset? RunAt { get; set; }

    /// <summary>Cronos cron expression for Recurring jobs.</summary>
    public string? CronExpression { get; set; }

    /// <summary>IANA time-zone id for cron evaluation (default: UTC).</summary>
    public string TimeZoneId { get; set; } = "UTC";

    // ── Owner / context ───────────────────────────────────────────────────────

    public string CreatedByUserId { get; set; } = string.Empty;
    public string AgentId         { get; set; } = string.Empty;

    // ── Execution payload ─────────────────────────────────────────────────────

    /// <summary>If set, the named skill is executed instead of a raw prompt.</summary>
    public string? SkillId { get; set; }

    /// <summary>Free-form prompt (used when SkillId is null).</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Optional JSON input forwarded to the skill.</summary>
    public string? SkillInputJson { get; set; }

    // ── Delivery ──────────────────────────────────────────────────────────────

    public bool EmailResult   { get; set; }
    public string? EmailTo    { get; set; }

    /// <summary>Messaging channel to deliver the result (e.g. "telegram", "teams"). Null = no channel delivery.</summary>
    public string? DeliveryChannel { get; set; }

    /// <summary>Channel-specific recipient identifier (chat id, webhook, etc.).</summary>
    public string? DeliveryRecipient { get; set; }

    // ── State ─────────────────────────────────────────────────────────────────

    public bool Enabled                    { get; set; } = true;
    public DateTimeOffset? NextRun         { get; set; }
    public DateTimeOffset? LastRun         { get; set; }
    public DateTimeOffset CreatedAt        { get; init; } = DateTimeOffset.UtcNow;
    public string? LastRunStatus           { get; set; }
}
