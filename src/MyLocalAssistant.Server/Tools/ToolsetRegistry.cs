using Microsoft.Extensions.Logging;

namespace MyLocalAssistant.Server.Tools;

/// <summary>
/// Registry of all toolsets.  Mirrors Hermes's TOOLSETS dict + resolve_toolset() logic.
/// Toolsets are resolved transitively: if a toolset includes another, all its tools are
/// included recursively with cycle detection.
/// </summary>
public sealed class ToolsetRegistry
{
    private readonly IReadOnlyDictionary<string, ToolsetDefinition> _byId;
    private readonly ILogger<ToolsetRegistry> _logger;

    // ── Built-in toolsets ─────────────────────────────────────────────────────
    // These mirror Hermes's core concept: named, composable groups of tools.

    public static readonly IReadOnlyList<ToolsetDefinition> BuiltInToolsets =
    [
        new("core",
            "Core",
            "Essential tools available in every agent session.",
            ["code.csharp", "code.python", "time.now", "memory", "workdir"],
            []),

        new("file",
            "File",
            "File manipulation: read, write, search.",
            ["workdir"],
            []),

        new("office",
            "Office",
            "Excel, Word, PowerPoint, PDF, and report generation tools.",
            ["excel-handler", "word-handler", "powerpoint-handler", "pdf-handler", "report.gen"],
            []),

        new("web",
            "Web",
            "Web search and content extraction.",
            ["web-search"],
            []),

        new("data",
            "Data",
            "SQL Server querying and data analysis.",
            ["sqlserver"],
            []),

        new("code",
            "Code",
            "Code execution: C# (Roslyn) and Python interpreter.",
            ["code.csharp", "code.python"],
            []),

        new("communication",
            "Communication",
            "Email and scheduling tools.",
            ["email", "scheduler"],
            []),

        new("rag",
            "Knowledge Base",
            "Document retrieval and knowledge base search.",
            ["rag-search"],
            []),

        new("image",
            "Image",
            "Image generation tools.",
            ["image-gen"],
            []),

        new("manufacturing",
            "Manufacturing",
            "Manufacturing planning and purchasing tools: Excel data, SQL, email, web research, and document generation.",
            [],
            ["office", "data", "web", "communication", "rag"]),

        new("full",
            "Full",
            "All available tools.",
            [],
            ["core", "file", "office", "web", "data", "code", "communication", "rag", "image"]),
    ];

    public ToolsetRegistry(ILogger<ToolsetRegistry> logger)
    {
        _logger = logger;
        _byId = BuiltInToolsets.ToDictionary(ts => ts.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var ts in BuiltInToolsets)
            _logger.LogDebug("Toolset registered: {Id} ({Name})", ts.Id, ts.Name);
    }

    public ToolsetDefinition? Find(string id) => _byId.GetValueOrDefault(id);

    public IReadOnlyCollection<ToolsetDefinition> All() =>
        (IReadOnlyCollection<ToolsetDefinition>)_byId.Values;

    /// <summary>
    /// Recursively resolve all tool ids in a toolset, handling composition and cycle detection.
    /// Equivalent to Hermes's resolve_toolset().
    /// </summary>
    public IReadOnlyList<string> Resolve(string toolsetId, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!visited.Add(toolsetId)) return Array.Empty<string>(); // cycle guard

        var ts = Find(toolsetId);
        if (ts is null) return Array.Empty<string>();

        var result = new HashSet<string>(ts.ToolIds, StringComparer.OrdinalIgnoreCase);
        foreach (var inc in ts.IncludesToolsetIds)
            foreach (var id in Resolve(inc, visited))
                result.Add(id);

        return result.ToList();
    }

    /// <summary>Resolve multiple toolset ids into a deduplicated tool id list.</summary>
    public IReadOnlyList<string> ResolveMultiple(IEnumerable<string> toolsetIds)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in toolsetIds)
            foreach (var toolId in Resolve(id))
                all.Add(toolId);
        return all.ToList();
    }

    /// <summary>Parse a semicolon-separated toolset id list (same pattern as agent.ToolIds).</summary>
    public static IReadOnlyList<string> ParseIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
        return csv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
