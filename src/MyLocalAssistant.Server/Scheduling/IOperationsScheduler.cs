namespace MyLocalAssistant.Server.Scheduling;

/// <summary>
/// Manages Operations Scheduler jobs: CRUD + next-run calculation.
/// Storage: {StateDirectory}/_ops-scheduler/jobs.json
/// </summary>
public interface IOperationsScheduler
{
    /// <summary>Add a new job. Returns the persisted job with Id and NextRun populated.</summary>
    Task<OperationsJob> AddAsync(OperationsJob job, CancellationToken ct = default);

    /// <summary>Update an existing job (full replace by id).</summary>
    Task<bool> UpdateAsync(OperationsJob job, CancellationToken ct = default);

    /// <summary>Disable a job by id (does not delete).</summary>
    Task<bool> DisableAsync(string jobId, CancellationToken ct = default);

    /// <summary>Delete a job permanently.</summary>
    Task<bool> DeleteAsync(string jobId, CancellationToken ct = default);

    /// <summary>Return all jobs visible to the given user (admins see all).</summary>
    Task<IReadOnlyList<OperationsJob>> ListAsync(string userId, bool isAdmin, CancellationToken ct = default);

    /// <summary>Return jobs that are enabled and due at or before <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<OperationsJob>> GetDueJobsAsync(DateTimeOffset asOf, CancellationToken ct = default);

    /// <summary>Advance NextRun after a successful execution, or disable if one-shot.</summary>
    Task AdvanceAsync(OperationsJob job, string status, CancellationToken ct = default);
}
