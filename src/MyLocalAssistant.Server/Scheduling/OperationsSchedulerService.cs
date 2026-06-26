using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;
using Microsoft.Extensions.Logging;

namespace MyLocalAssistant.Server.Scheduling;

/// <summary>
/// File-backed implementation of IOperationsScheduler.
/// Jobs are persisted as JSON in {StateDirectory}/_ops-scheduler/jobs.json.
/// Thread-safe via a SemaphoreSlim around all file operations.
/// </summary>
public sealed class OperationsSchedulerService : IOperationsScheduler
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<OperationsSchedulerService> _logger;

    public OperationsSchedulerService(ILogger<OperationsSchedulerService> logger)
    {
        _logger = logger;
        var dir = Path.Combine(ServerPaths.StateDirectory, "_ops-scheduler");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "jobs.json");
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<OperationsJob> AddAsync(OperationsJob job, CancellationToken ct = default)
    {
        job.NextRun = ComputeNextRun(job, DateTimeOffset.UtcNow);
        await MutateAsync(jobs => jobs.Add(job), ct);
        _logger.LogInformation("OperationsScheduler: job added '{Name}' ({Id}).", job.Name, job.Id);
        return job;
    }

    public async Task<bool> UpdateAsync(OperationsJob job, CancellationToken ct = default)
    {
        bool found = false;
        await MutateAsync(jobs =>
        {
            var idx = jobs.FindIndex(j => j.Id == job.Id);
            if (idx < 0) return;
            job.NextRun = ComputeNextRun(job, DateTimeOffset.UtcNow);
            jobs[idx] = job;
            found = true;
        }, ct);
        return found;
    }

    public async Task<bool> DisableAsync(string jobId, CancellationToken ct = default)
    {
        bool found = false;
        await MutateAsync(jobs =>
        {
            var job = jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is null) return;
            job.Enabled = false;
            found = true;
        }, ct);
        return found;
    }

    public async Task<bool> DeleteAsync(string jobId, CancellationToken ct = default)
    {
        bool found = false;
        await MutateAsync(jobs =>
        {
            var removed = jobs.RemoveAll(j => j.Id == jobId);
            found = removed > 0;
        }, ct);
        return found;
    }

    public async Task<IReadOnlyList<OperationsJob>> ListAsync(
        string userId, bool isAdmin, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct);
        return isAdmin ? all : all.Where(j => j.CreatedByUserId == userId).ToList();
    }

    public async Task<IReadOnlyList<OperationsJob>> GetDueJobsAsync(
        DateTimeOffset asOf, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct);
        return all.Where(j => j.Enabled && j.NextRun.HasValue && j.NextRun.Value <= asOf).ToList();
    }

    public async Task AdvanceAsync(OperationsJob job, string status, CancellationToken ct = default)
    {
        await MutateAsync(jobs =>
        {
            var stored = jobs.FirstOrDefault(j => j.Id == job.Id);
            if (stored is null) return;
            stored.LastRun = DateTimeOffset.UtcNow;
            stored.LastRunStatus = status;
            if (stored.TriggerType == JobTriggerType.OneShot)
            {
                stored.Enabled = false;
                stored.NextRun = null;
            }
            else
            {
                stored.NextRun = ComputeNextRun(stored, DateTimeOffset.UtcNow.AddSeconds(1));
            }
        }, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DateTimeOffset? ComputeNextRun(OperationsJob job, DateTimeOffset from)
    {
        if (job.TriggerType == JobTriggerType.OneShot)
            return job.RunAt;

        if (string.IsNullOrWhiteSpace(job.CronExpression)) return null;

        try
        {
            var tz   = TimeZoneInfo.FindSystemTimeZoneById(job.TimeZoneId);
            var expr = CronExpression.Parse(job.CronExpression,
                job.CronExpression.Split(' ').Length == 6
                    ? CronFormat.IncludeSeconds : CronFormat.Standard);
            return expr.GetNextOccurrence(from, tz);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<List<OperationsJob>> LoadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_filePath)) return [];
            var json = await File.ReadAllTextAsync(_filePath, ct);
            return JsonSerializer.Deserialize<List<OperationsJob>>(json, s_json) ?? [];
        }
        finally { _lock.Release(); }
    }

    private async Task MutateAsync(Action<List<OperationsJob>> mutate, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            List<OperationsJob> jobs = [];
            if (File.Exists(_filePath))
            {
                var raw = await File.ReadAllTextAsync(_filePath, ct);
                jobs = JsonSerializer.Deserialize<List<OperationsJob>>(raw, s_json) ?? [];
            }
            mutate(jobs);
            await File.WriteAllTextAsync(_filePath,
                JsonSerializer.Serialize(jobs, s_json), ct);
        }
        finally { _lock.Release(); }
    }
}
