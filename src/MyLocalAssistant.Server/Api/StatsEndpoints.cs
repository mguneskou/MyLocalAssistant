using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class StatsEndpoints
{
    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/stats", GetStatsAsync)
           .WithTags("Admin/Stats")
           .RequireAuthorization("Admin");
        return app;
    }

    private static async Task<IResult> GetStatsAsync(
        AppDbContext db,
        CancellationToken ct,
        int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTimeOffset.UtcNow.AddDays(-days);

        // Pull all chat audit entries in the window.
        var chatEntries = await db.AuditEntries.AsNoTracking()
            .Where(a => a.Action == "chat.send" && a.Timestamp >= since)
            .Select(a => new { a.AgentId, a.Success, a.Timestamp })
            .ToListAsync(ct);

        var total = chatEntries.Count;
        var errors = chatEntries.Count(a => !a.Success);

        var byAgent = chatEntries
            .GroupBy(a => a.AgentId ?? "(unknown)")
            .Select(g => new AgentStatDto(g.Key, g.Count(), g.Count(a => !a.Success)))
            .OrderByDescending(x => x.Count)
            .ToList();

        var daily = chatEntries
            .GroupBy(a => DateOnly.FromDateTime(a.Timestamp.UtcDateTime))
            .Select(g => new DayStat(g.Key, g.Count()))
            .OrderBy(d => d.Day)
            .ToList();

        // Fill in missing days with 0.
        var allDays = new List<DayStat>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-i));
            var found = daily.FirstOrDefault(d => d.Day == day);
            allDays.Add(found ?? new DayStat(day, 0));
        }

        var activeUsers = await db.AuditEntries.AsNoTracking()
            .Where(a => a.Action == "chat.send" && a.Timestamp >= since && a.UserId != null)
            .Select(a => a.UserId)
            .Distinct()
            .CountAsync(ct);

        return Results.Ok(new StatsDto(
            total,
            activeUsers,
            total == 0 ? 0 : (double)errors / total,
            byAgent,
            allDays));
    }
}
