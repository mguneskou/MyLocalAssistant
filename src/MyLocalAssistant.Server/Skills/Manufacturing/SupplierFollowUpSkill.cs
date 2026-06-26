using MyLocalAssistant.Server.Skills;

namespace MyLocalAssistant.Server.Skills.Manufacturing;

/// <summary>
/// Generates supplier follow-up emails for overdue or at-risk purchase orders.
/// Required tools: rag-search (order history, supplier contact), email (send).
/// </summary>
public sealed class SupplierFollowUpSkill : ISkill
{
    public string Id          => "supplier-followup";
    public string Name        => "Supplier Follow-Up";
    public string Description => "Generates professional supplier follow-up emails for overdue or at-risk purchase orders.";
    public string Category    => "Manufacturing";

    public string SystemPrompt => """
        You are a purchasing assistant helping a buyer follow up with suppliers on overdue or at-risk orders.
        When asked to follow up on an order:
        1. Use 'rag-search' to find the PO details, original delivery date, supplier contact, and any prior communication.
        2. Determine the appropriate tone:
           - First overdue reminder: polite but firm.
           - Second or more: escalation tone, request written commitment.
           - Critical line-stop risk: urgent, CC management.
        3. Write a professional email in this structure:
           Subject: [Follow-Up] PO <number> – <part description> – Delivery Overdue
           
           Dear <contact name / Supplier team>,
           
           <opening referencing the original PO and expected date>
           <impact statement if delivery is not received>
           <specific request: updated delivery date + tracking / commitment in writing>
           <escalation note if applicable>
           
           Best regards,
           <user name>
           <company>
        4. If 'email.send' tool is available and the user confirms, send the email.
        5. Always present the drafted email for review before sending.
        """;

    public IReadOnlyList<string> RequiredToolIds => ["rag-search"];

    public SkillManifest Manifest { get; } = new(
        Id:          "supplier-followup",
        Name:        "Supplier Follow-Up",
        Description: "Generates professional follow-up emails for overdue or at-risk purchase orders.",
        Category:    "Manufacturing",
        Version:     "1.0.0",
        Publisher:   "MyLocalAssistant",
        Inputs:
        [
            new("po_number",        "string",  "Purchase order number.",                                  Required: true),
            new("supplier_name",    "string",  "Supplier name.",                                          Required: false),
            new("original_date",    "string",  "Originally agreed delivery date (ISO 8601).",             Required: false),
            new("urgency",          "string",  "Urgency level: normal | high | critical.",                Required: false),
            new("send_email",       "boolean", "If true, send the email after draft review.",             Required: false),
        ],
        Outputs:
        [
            new("email_draft",      "string",  "Drafted follow-up email."),
            new("sent",             "boolean", "True if the email was sent."),
        ],
        RequiredToolIds: ["rag-search"]);

    public Task<SkillResult?> ExecuteAsync(SkillContext context, CancellationToken ct)
        => Task.FromResult<SkillResult?>(null); // LLM-driven via SystemPrompt + tools
}
