using MyLocalAssistant.Server.Skills;

namespace MyLocalAssistant.Server.Skills.Manufacturing;

/// <summary>
/// Drafts a purchase requisition from a natural-language request.
/// Required tools: rag-search (supplier/price context), excel-handler (optional BOM lookup).
/// </summary>
public sealed class PurchaseRequestSkill : ISkill
{
    public string Id          => "purchase-request";
    public string Name        => "Purchase Request";
    public string Description => "Drafts a structured purchase requisition from a natural-language description of the need.";
    public string Category    => "Manufacturing";

    public string SystemPrompt => """
        You are a procurement assistant for a manufacturing company.
        When a buyer asks you to draft a purchase request:
        1. Use 'rag-search' to find approved suppliers, price history, or lead-time data if available.
        2. Extract: Item description, quantity, unit of measure, required delivery date, cost centre, and justification.
        3. Suggest the best supplier from the context, or mark as "Supplier TBD" if none found.
        4. Produce a structured Purchase Requisition in this format:
           ---
           PURCHASE REQUISITION
           Date: <today>
           Requested by: <user>
           
           Item:            <description>
           Part/Material:   <part number if known>
           Quantity:        <qty> <UoM>
           Required by:     <date>
           Suggested supplier: <name or TBD>
           Estimated unit cost: <cost or TBD>
           Cost centre:     <code or TBD>
           Justification:   <reason>
           ---
        5. End with any open questions the buyer should answer before approval.
        Do not invent prices or supplier names not found in the context.
        """;

    public IReadOnlyList<string> RequiredToolIds => ["rag-search"];

    public SkillManifest Manifest { get; } = new(
        Id:          "purchase-request",
        Name:        "Purchase Request",
        Description: "Drafts a structured purchase requisition from natural-language input.",
        Category:    "Manufacturing",
        Version:     "1.0.0",
        Publisher:   "MyLocalAssistant",
        Inputs:
        [
            new("description",      "string",  "Natural-language description of what needs to be purchased.", Required: true),
            new("required_by",      "string",  "Required delivery date (ISO 8601).",                          Required: false),
            new("cost_centre",      "string",  "Cost centre code.",                                           Required: false),
            new("quantity",         "number",  "Quantity required.",                                          Required: false),
        ],
        Outputs:
        [
            new("requisition",      "string",  "Formatted purchase requisition text."),
            new("open_questions",   "string",  "List of open items for the buyer to resolve."),
        ],
        RequiredToolIds: ["rag-search"]);

    public Task<SkillResult?> ExecuteAsync(SkillContext context, CancellationToken ct)
        => Task.FromResult<SkillResult?>(null); // LLM-driven via SystemPrompt + tools
}
