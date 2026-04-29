using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Auth;

public sealed class DepartmentService(AppDbContext db, ILogger<DepartmentService> log)
{
    public async Task<List<DepartmentDto>> ListAsync(CancellationToken ct)
    {
        return await db.Departments
            .OrderBy(d => d.Name)
            .Select(d => new DepartmentDto(d.Id, d.Name, d.Users.Count))
            .ToListAsync(ct);
    }

    public async Task<(DepartmentDto? Dto, string? Code)> CreateAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, ProblemCodes.ValidationFailed);
        var trimmed = name.Trim();
        if (await db.Departments.AnyAsync(d => d.Name == trimmed, ct))
            return (null, ProblemCodes.Conflict);

        var d = new Department { Name = trimmed };
        db.Departments.Add(d);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Created department {Name}", d.Name);
        return (new DepartmentDto(d.Id, d.Name, 0), null);
    }

    public async Task<(DepartmentDto? Dto, string? Code)> RenameAsync(Guid id, string newName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newName)) return (null, ProblemCodes.ValidationFailed);
        var trimmed = newName.Trim();
        var d = await db.Departments.FindAsync(new object[] { id }, ct);
        if (d is null) return (null, ProblemCodes.NotFound);
        if (await db.Departments.AnyAsync(x => x.Id != id && x.Name == trimmed, ct))
            return (null, ProblemCodes.Conflict);
        d.Name = trimmed;
        await db.SaveChangesAsync(ct);
        var count = await db.UserDepartments.CountAsync(ud => ud.DepartmentId == id, ct);
        return (new DepartmentDto(d.Id, d.Name, count), null);
    }

    public async Task<string?> DeleteAsync(Guid id, CancellationToken ct)
    {
        var d = await db.Departments.FindAsync(new object[] { id }, ct);
        if (d is null) return ProblemCodes.NotFound;
        db.Departments.Remove(d); // cascade removes UserDepartment rows
        await db.SaveChangesAsync(ct);
        log.LogWarning("Deleted department {Name}", d.Name);
        return null;
    }
}
