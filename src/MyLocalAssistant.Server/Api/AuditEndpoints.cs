using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class AuditEndpoints
{
    private const int MaxTake = 500;
    private const int MaxExport = 50_000;

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/audit").WithTags("Audit").RequireAuthorization("Admin");
        g.MapGet("/", ListAsync);
        g.MapGet("/actions", ListActionsAsync);
        g.MapGet("/export.csv", ExportCsvAsync);
        return app;
    }

    private static IQueryable<AuditEntry> ApplyFilter(
        AppDbContext db,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? action,
        string? user,
        bool? success)
    {
        var q = db.AuditEntries.AsNoTracking().AsQueryable();
        if (from is { } f) q = q.Where(a => a.Timestamp >= f);
        if (to is { } t) q = q.Where(a => a.Timestamp < t);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(user))
        {
            var u = user.Trim();
            if (Guid.TryParse(u, out var gid))
                q = q.Where(a => a.UserId == gid);
            else
                q = q.Where(a => a.Username != null && a.Username.Contains(u));
        }
        if (success is bool s) q = q.Where(a => a.Success == s);
        return q;
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? action = null,
        string? user = null,
        bool? success = null,
        int skip = 0,
        int take = 100)
    {
        if (skip < 0) skip = 0;
        take = Math.Clamp(take, 1, MaxTake);

        var q = ApplyFilter(db, from, to, action, user, success);
        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
            .Skip(skip).Take(take)
            .Select(a => new AuditEntryDto(a.Id, a.Timestamp, a.UserId, a.Username, a.Action,
                a.AgentId, a.Detail, a.IpAddress, a.Success))
            .ToListAsync(ct);
        return Results.Ok(new AuditPageDto(rows, total, skip, take));
    }

    private static async Task<IResult> ListActionsAsync(AppDbContext db, CancellationToken ct)
    {
        var actions = await db.AuditEntries.AsNoTracking()
            .Select(a => a.Action)
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync(ct);
        return Results.Ok(actions);
    }

    private static async Task ExportCsvAsync(
        HttpContext http,
        AppDbContext db,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? action = null,
        string? user = null,
        bool? success = null)
    {
        var q = ApplyFilter(db, from, to, action, user, success)
            .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
            .Take(MaxExport);

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/csv; charset=utf-8";
        var fileName = $"audit-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

        var sb = new StringBuilder();
        sb.AppendLine("Id,Timestamp,UserId,Username,Action,AgentId,IpAddress,Success,Detail");
        await http.Response.WriteAsync(sb.ToString(), ct);
        sb.Clear();

        await foreach (var a in q.AsAsyncEnumerable().WithCancellation(ct))
        {
            sb.Append(a.Id).Append(',');
            sb.Append(a.Timestamp.ToString("o", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(a.UserId?.ToString() ?? "").Append(',');
            AppendCsv(sb, a.Username); sb.Append(',');
            AppendCsv(sb, a.Action); sb.Append(',');
            AppendCsv(sb, a.AgentId); sb.Append(',');
            AppendCsv(sb, a.IpAddress); sb.Append(',');
            sb.Append(a.Success ? "1" : "0").Append(',');
            AppendCsv(sb, a.Detail);
            sb.Append('\n');
            if (sb.Length > 32_768)
            {
                await http.Response.WriteAsync(sb.ToString(), ct);
                sb.Clear();
            }
        }
        if (sb.Length > 0) await http.Response.WriteAsync(sb.ToString(), ct);
    }

    private static void AppendCsv(StringBuilder sb, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var needsQuote = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needsQuote) { sb.Append(value); return; }
        sb.Append('"');
        foreach (var ch in value)
        {
            if (ch == '"') sb.Append('"');
            sb.Append(ch);
        }
        sb.Append('"');
    }
}
