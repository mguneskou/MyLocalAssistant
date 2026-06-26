using MyLocalAssistant.Server.Skills;

namespace MyLocalAssistant.Server.Skills.Manufacturing;

/// <summary>
/// Reviews open shortages and produces a structured exception report.
/// Required tools: excel-handler (read shortage list), rag-search (policy/context), report-gen (output).
/// </summary>
public sealed class ShortageReviewSkill : ISkill
{
    public string Id          => "shortage-review";
    public string Name        => "Shortage Review";
    public string Description => "Analyses open material shortages, classifies them by urgency, and produces a structured exception report.";
    public string Category    => "Manufacturing";

    public string SystemPrompt => """
        You are a manufacturing planning assistant specialising in shortage management.
        When asked to review shortages:
        1. Use the 'excel.read' or 'sql.query' tool to load the shortage data provided.
        2. Classify each shortage as Critical (line-stop risk < 48 h), High (< 1 week), or Normal.
        3. For each critical/high shortage, propose an action: expedite, find alternative, or escalate.
        4. Summarise in a clear table: Part No | Description | Required Qty | Available | Gap | Urgency | Recommended Action.
        5. End with an executive summary of total shortages by urgency level.
        Be concise. Do not invent data; only use what was loaded.
        """;

    public IReadOnlyList<string> RequiredToolIds => ["excel-handler", "rag-search"];

    public SkillManifest Manifest { get; } = new(
        Id:             "shortage-review",
        Name:           "Shortage Review",
        Description:    "Analyses open material shortages and produces a classified exception report.",
        Category:       "Manufacturing",
        Version:        "1.0.0",
        Publisher:      "MyLocalAssistant",
        Inputs:
        [
            new("file_path",   "string", "Path to the Excel or CSV shortage file.", Required: true),
            new("as_of_date",  "string", "Reference date for urgency calculation (ISO 8601). Defaults to today.", Required: false),
        ],
        Outputs:
        [
            new("report",      "string", "Structured shortage exception report."),
            new("critical_count", "integer", "Number of critical shortages found."),
        ],
        RequiredToolIds: ["excel-handler", "rag-search"]);

    public Task<SkillResult?> ExecuteAsync(SkillContext context, CancellationToken ct)
        => Task.FromResult<SkillResult?>(null); // LLM-driven via SystemPrompt + tools
}
