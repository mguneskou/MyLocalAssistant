using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;

namespace MyLocalAssistant.Server.Auth;

/// <summary>
/// Writes audit rows. Uses a dedicated short-lived scope so callers (e.g. SSE streaming
/// handlers that may live for many seconds) do not have to share the request DbContext.
/// </summary>
public sealed class AuditWriter(IServiceScopeFactory scopes, ILogger<AuditWriter> log)
{
    public async Task WriteAsync(
        string action,
        Guid? userId,
        string? username,
        bool success,
        string? agentId = null,
        string? detail = null,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AuditEntries.Add(new AuditEntry
            {
                Action = action,
                UserId = userId,
                Username = username,
                AgentId = agentId,
                Detail = detail,
                IpAddress = ipAddress,
                Success = success,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to write audit entry for {Action}", action);
        }
    }
}
