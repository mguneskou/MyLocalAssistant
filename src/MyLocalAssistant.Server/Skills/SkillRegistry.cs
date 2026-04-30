using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Skills;

/// <summary>
/// Process-wide catalog of every <see cref="ISkill"/> available to the server. In Phase 1
/// the only registered skills are built-in (compiled in via DI). Phase 3 adds plug-in
/// skills loaded by <c>SkillRegistry.LoadPluginsAsync</c> after scanning <c>./skills/</c>.
/// </summary>
public sealed class SkillRegistry(
    IEnumerable<ISkill> builtInSkills,
    IServiceScopeFactory scopeFactory,
    ILogger<SkillRegistry> log)
{
    /// <summary>Lookup by skill id (case-insensitive).</summary>
    private readonly Dictionary<string, ISkill> _skills = builtInSkills.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
    /// <summary>Per-skill in-memory state. Populated by <see cref="SeedAsync"/> from the DB row.</summary>
    private readonly Dictionary<string, SkillState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public IReadOnlyCollection<ISkill> All
    {
        get { lock (_lock) return _skills.Values.ToArray(); }
    }

    /// <summary>Register an extra skill (e.g. a verified plug-in) before <see cref="SeedAsync"/>
    /// runs. Throws if the id collides with an existing skill — built-ins win because they
    /// are wired in DI; a colliding plug-in id is a packaging bug.</summary>
    public void Register(ISkill skill)
    {
        lock (_lock)
        {
            if (_skills.ContainsKey(skill.Id))
                throw new InvalidOperationException($"Skill id '{skill.Id}' already registered (built-in or earlier plug-in).");
            _skills[skill.Id] = skill;
        }
    }

    public bool TryGet(string id, out ISkill skill)
    {
        lock (_lock)
        {
            if (_skills.TryGetValue(id, out var s)) { skill = s; return true; }
            skill = null!;
            return false;
        }
    }

    public bool IsEnabled(string id)
    {
        lock (_lock)
        {
            return _states.TryGetValue(id, out var st) && st.Enabled;
        }
    }

    public string? GetConfig(string id)
    {
        lock (_lock)
        {
            return _states.TryGetValue(id, out var st) ? st.ConfigJson : null;
        }
    }

    /// <summary>Parse the semicolon-separated <c>Agent.SkillIds</c> column.</summary>
    public static IReadOnlyList<string> ParseSkillIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
        return csv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Format a skill-id list for storage. Returns <c>null</c> when the list is empty.</summary>
    public static string? FormatSkillIds(IEnumerable<string>? ids)
    {
        if (ids is null) return null;
        var arr = ids.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return arr.Length == 0 ? null : string.Join(';', arr);
    }

    /// <summary>
    /// Reconcile the in-memory catalog with the <c>SkillState</c> table. New built-in
    /// skills are inserted disabled; obsolete rows (skill id no longer registered)
    /// are kept in the DB so their config survives an upgrade where the skill is
    /// temporarily missing — they're just hidden from the catalog.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.Skills.ToListAsync(ct);
        var byId = existing.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var skill in _skills.Values)
        {
            if (!byId.TryGetValue(skill.Id, out var row))
            {
                row = new SkillState
                {
                    Id = skill.Id,
                    Source = skill.Source,
                    Enabled = false,            // global admin must opt in
                    ConfigJson = null,
                    InstalledVersion = skill.Version,
                };
                db.Skills.Add(row);
                added++;
            }
            else if (row.Source != skill.Source || row.InstalledVersion != skill.Version)
            {
                row.Source = skill.Source;
                row.InstalledVersion = skill.Version;
                row.UpdatedAt = DateTimeOffset.UtcNow;
            }
            // Push the persisted config into the live skill instance (and validate).
            try
            {
                skill.Configure(row.ConfigJson);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Skill {Id} rejected its persisted config; leaving it disabled.", skill.Id);
                row.Enabled = false;
            }
            lock (_lock) _states[skill.Id] = row;
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        log.LogInformation("Skill registry seeded: total={Total}, addedRows={Added}.", _skills.Count, added);
    }

    /// <summary>Update enabled flag and/or config for a skill. Persists to DB and updates the in-memory cache.</summary>
    public async Task<SkillDto> UpdateAsync(string id, SkillUpdateRequest req, CancellationToken ct = default)
    {
        if (!TryGet(id, out var skill))
            throw new KeyNotFoundException($"Skill '{id}' not registered.");

        // Validate config against the live skill BEFORE persisting — cheap protection
        // against typos rendering a skill un-enableable until manual DB edit.
        try { skill.Configure(req.ConfigJson); }
        catch (Exception ex) { throw new ArgumentException("Invalid configuration: " + ex.Message); }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Skills.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new InvalidOperationException($"Skill row for '{id}' missing; restart server to re-seed.");
        row.Enabled = req.Enabled;
        row.ConfigJson = req.ConfigJson;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        lock (_lock) _states[id] = row;
        log.LogInformation("Skill {Id} updated: enabled={Enabled}, configChars={Chars}.", id, row.Enabled, row.ConfigJson?.Length ?? 0);
        return ToDto(skill, row);
    }

    public IReadOnlyList<SkillDto> List()
    {
        lock (_lock)
        {
            return _skills.Values
                .OrderBy(s => s.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s =>
                {
                    _states.TryGetValue(s.Id, out var st);
                    return ToDto(s, st);
                })
                .ToArray();
        }
    }

    private static SkillDto ToDto(ISkill skill, SkillState? state) => new(
        Id: skill.Id,
        Name: skill.Name,
        Description: skill.Description,
        Category: skill.Category,
        Source: skill.Source,
        Version: skill.Version,
        Enabled: state?.Enabled ?? false,
        ConfigJson: state?.ConfigJson,
        Requires: skill.Requirements,
        Tools: skill.Tools);
}
