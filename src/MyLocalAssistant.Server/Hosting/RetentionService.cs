using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Server.Persistence;

namespace MyLocalAssistant.Server.Hosting;

/// <summary>
/// Periodically purges old audit entries and old message bodies according to the
/// retention policy in <see cref="ServerSettings"/>. Metadata is preserved by
/// nulling Message.Body and stamping BodyPurgedAt.
/// </summary>
public sealed class RetentionService(
    IServiceScopeFactory scopes,
    ILogger<RetentionService> log) : BackgroundService
{
    private static readonly TimeSpan s_initialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(s_initialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { log.LogWarning(ex, "Retention pass failed."); }
            try { await Task.Delay(s_interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ServerSettings>();
        var now = DateTimeOffset.UtcNow;

        // 1. Purge audit rows entirely.
        var auditDays = Math.Max(1, settings.AuditRetentionDays);
        var auditCutoff = now.AddDays(-auditDays);
        var auditDeleted = await db.AuditEntries
            .Where(a => a.Timestamp < auditCutoff)
            .ExecuteDeleteAsync(ct);

        // 2. Purge message bodies (keep metadata).
        var bodyDays = Math.Max(1, settings.MessageBodyRetentionDays);
        var bodyCutoff = now.AddDays(-bodyDays);
        var bodyPurged = await db.Messages
            .Where(m => m.Body != null && m.CreatedAt < bodyCutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Body, _ => null)
                .SetProperty(m => m.BodyPurgedAt, _ => now), ct);

        if (auditDeleted > 0 || bodyPurged > 0)
            log.LogInformation("Retention pass: deleted {Audit} audit row(s), purged {Bodies} message body/ies.",
                auditDeleted, bodyPurged);
    }
}
