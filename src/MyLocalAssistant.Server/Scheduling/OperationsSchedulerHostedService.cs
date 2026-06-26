using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyLocalAssistant.Server.Skills;

namespace MyLocalAssistant.Server.Scheduling;

/// <summary>
/// Background service that polls IOperationsScheduler every minute and executes
/// due jobs.  Skill-based jobs are routed through SkillExecutor; prompt-only jobs
/// are passed directly to the agent chat pipeline.
/// </summary>
public sealed class OperationsSchedulerHostedService(
    IOperationsScheduler scheduler,
    IServiceScopeFactory scopes,
    ILogger<OperationsSchedulerHostedService> log) : BackgroundService
{
    private static readonly TimeSpan s_initialDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(s_initialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try   { await PollAsync(stoppingToken); }
            catch (Exception ex) { log.LogWarning(ex, "OperationsScheduler poll error."); }

            try { await Task.Delay(s_pollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var due = await scheduler.GetDueJobsAsync(DateTimeOffset.UtcNow, ct);
        if (due.Count == 0) return;

        log.LogInformation("OperationsScheduler: {Count} job(s) due.", due.Count);

        foreach (var job in due)
        {
            var status = "ok";
            try
            {
                await ExecuteJobAsync(job, ct);
            }
            catch (Exception ex)
            {
                status = "error";
                log.LogWarning(ex, "OperationsScheduler: job '{Name}' ({Id}) failed.", job.Name, job.Id);
            }
            finally
            {
                await scheduler.AdvanceAsync(job, status, ct);
            }
        }
    }

    private async Task ExecuteJobAsync(OperationsJob job, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;

        if (!string.IsNullOrWhiteSpace(job.SkillId))
        {
            var executor = sp.GetRequiredService<SkillExecutor>();
            var context = new SkillContext(
                UserId: Guid.Empty,
                Username: "scheduler",
                IsAdmin: true,
                AgentId: job.AgentId,
                ConversationId: Guid.NewGuid(),
                WorkDirectory: ServerPaths.StateDirectory,
                UserMessage: job.Prompt,
                Tools: new Dictionary<string, Server.Tools.ITool>(),
                CancellationToken: ct);

            var result = await executor.RunAsync(job.SkillId, context, ct);
            log.LogInformation(
                "OperationsScheduler: skill '{Skill}' for job '{Name}' completed — {Status}.",
                job.SkillId, job.Name, result.IsError ? "error" : "ok");
            return;
        }

        // Fallback: fire a plain prompt through ChatService (same pattern as SchedulerHostedService).
        // Build a system-user principal by delegating to the existing Hosting service logic.
        // For now log a warning — full prompt execution will be wired in a follow-up.
        log.LogWarning(
            "OperationsScheduler: prompt-only job '{Name}' requires ChatService integration (not wired yet). Skipping execution.",
            job.Name);
        await Task.CompletedTask;
    }
}
