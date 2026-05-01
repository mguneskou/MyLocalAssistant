using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools;

/// <summary>
/// Process-wide catalog of every <see cref="ITool"/> available to the server. In Phase 1
/// the only registered skills are built-in (compiled in via DI). Phase 3 adds plug-in
/// skills loaded by <c>ToolRegistry.LoadPluginsAsync</c> after scanning <c>./tools/</c>.
/// </summary>
public sealed class ToolRegistry(
    IEnumerable<ITool> builtInTools,
    IServiceScopeFactory scopeFactory,
    ILogger<ToolRegistry> log)
{
    /// <summary>Lookup by skill id (case-insensitive).</summary>
    private readonly Dictionary<string, ITool> _tools = builtInTools.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
    /// <summary>Per-skill in-memory state. Populated by <see cref="SeedAsync"/> from the DB row.</summary>
    private readonly Dictionary<string, ToolState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public IReadOnlyCollection<ITool> All
    {
        get { lock (_lock) return _tools.Values.ToArray(); }
    }

    /// <summary>Register an extra tool (e.g. a verified plug-in) before <see cref="SeedAsync"/>
    /// runs. Throws if the id collides with an existing skill — built-ins win because they
    /// are wired in DI; a colliding plug-in id is a packaging bug.</summary>
    public void Register(ITool skill)
    {
        lock (_lock)
        {
            if (_tools.ContainsKey(skill.Id))
                throw new InvalidOperationException($"Tool id '{skill.Id}' already registered (built-in or earlier plug-in).");
            _tools[skill.Id] = skill;
        }
    }

    /// <summary>Remove a previously registered plug-in skill. Returns the removed instance
    /// so the caller can dispose it. Built-in skills cannot be removed (DI owns their lifetime).</summary>
    public ITool? Unregister(string id)
    {
        lock (_lock)
        {
            if (!_tools.TryGetValue(id, out var s)) return null;
            if (s.Source != ToolSources.Plugin) return null;
            _tools.Remove(id);
            return s;
        }
    }

    public bool TryGet(string id, out ITool skill)
    {
        lock (_lock)
        {
            if (_tools.TryGetValue(id, out var s)) { skill = s; return true; }
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

    /// <summary>Parse the semicolon-separated <c>Agent.ToolIds</c> column.</summary>
    public static IReadOnlyList<string> ParseToolIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
        return csv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Format a tool-id list for storage. Returns <c>null</c> when the list is empty.</summary>
    public static string? FormatToolIds(IEnumerable<string>? ids)
    {
        if (ids is null) return null;
        var arr = ids.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return arr.Length == 0 ? null : string.Join(';', arr);
    }

    /// <summary>
    /// Reconcile the in-memory catalog with the <c>ToolState</c> table. New built-in
    /// skills are inserted disabled; obsolete rows (skill id no longer registered)
    /// are kept in the DB so their config survives an upgrade where the tool is
    /// temporarily missing — they're just hidden from the catalog.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.Tools.ToListAsync(ct);
        var byId = existing.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var skill in _tools.Values)
        {
            if (!byId.TryGetValue(skill.Id, out var row))
            {
                row = new ToolState
                {
                    Id = skill.Id,
                    Source = skill.Source,
                    Enabled = false,            // global admin must opt in
                    ConfigJson = null,
                    InstalledVersion = skill.Version,
                };
                db.Tools.Add(row);
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
                log.LogError(ex, "Tool {Id} rejected its persisted config; leaving it disabled.", skill.Id);
                row.Enabled = false;
            }
            lock (_lock) _states[skill.Id] = row;
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        log.LogInformation("Tool registry seeded: total={Total}, addedRows={Added}.", _tools.Count, added);
    }

    /// <summary>Update enabled flag and/or config for a tool. Persists to DB and updates the in-memory cache.</summary>
    public async Task<ToolDto> UpdateAsync(string id, ToolUpdateRequest req, CancellationToken ct = default)
    {
        if (!TryGet(id, out var skill))
            throw new KeyNotFoundException($"Tool '{id}' not registered.");

        // Validate config against the live skill BEFORE persisting — cheap protection
        // against typos rendering a tool un-enableable until manual DB edit.
        try { skill.Configure(req.ConfigJson); }
        catch (Exception ex) { throw new ArgumentException("Invalid configuration: " + ex.Message); }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Tools.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new InvalidOperationException($"Tool row for '{id}' missing; restart server to re-seed.");
        row.Enabled = req.Enabled;
        row.ConfigJson = req.ConfigJson;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        lock (_lock) _states[id] = row;
        log.LogInformation("Tool {Id} updated: enabled={Enabled}, configChars={Chars}.", id, row.Enabled, row.ConfigJson?.Length ?? 0);
        return ToDto(skill, row);
    }

    public IReadOnlyList<ToolDto> List()
    {
        lock (_lock)
        {
            return _tools.Values
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

    private static ToolDto ToDto(ITool skill, ToolState? state) => new(
        Id: skill.Id,
        Name: skill.Name,
        Description: skill.Description,
        Category: skill.Category,
        Source: skill.Source,
        Version: skill.Version,
        Publisher: skill.Publisher,
        KeyId: skill.KeyId,
        Enabled: state?.Enabled ?? false,
        ConfigJson: state?.ConfigJson,
        Requires: skill.Requirements,
        Tools: skill.Tools);
}
