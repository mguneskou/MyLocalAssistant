using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Auth;

public sealed class DepartmentService(AppDbContext db, ILogger<DepartmentService> log)
{
    /// <summary>
    /// The fixed list of departments shipped with the product. Mirrors the agent
    /// catalog (each agent maps 1:1 to a department of the same name). Admins
    /// cannot add/rename/delete departments — they are seeded on startup.
    /// </summary>
    public static readonly IReadOnlyList<string> SeedNames = new[]
    {
        // Universal
        "General Assistant",
        "Documentation",
        "Translator",
        "Meeting Notes",
        // Engineering & operations
        "R&D",
        "NPI",
        "Process / ME",
        "Quality / NCR / CAPA",
        "Maintenance / TPM",
        "EHS",
        // Business
        "Supply Chain / Procurement",
        "Sales / CRM",
        "Customer Support",
        // Restricted
        "HR",
        "Finance",
        "IT / Code Helper",
    };

    public async Task<List<DepartmentDto>> ListAsync(CancellationToken ct)
    {
        return await db.Departments
            .OrderBy(d => d.Name)
            .Select(d => new DepartmentDto(d.Id, d.Name, d.Users.Count))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Idempotent: inserts any seed departments missing from the database. Never
    /// renames or removes existing rows.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var existing = await db.Departments.Select(d => d.Name).ToListAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var name in SeedNames)
        {
            if (existingSet.Contains(name)) continue;
            db.Departments.Add(new Department { Name = name });
            added++;
        }
        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogInformation("Seeded {Count} department(s).", added);
        }
    }
}
