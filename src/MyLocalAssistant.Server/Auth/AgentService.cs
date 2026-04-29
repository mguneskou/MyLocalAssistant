using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Auth;

public sealed class AgentService(AppDbContext db, ILogger<AgentService> log)
{
    /// <summary>
    /// Built-in agent catalog. One agent per shipped department name (so dept-bound
    /// matching is by Name equality), plus IsGeneric=true for the Universal tier
    /// (visible to every signed-in user). Local admin can toggle Enabled and pick a
    /// DefaultModelId; SystemPrompts are sealed and only the future global admin
    /// package may change them.
    /// </summary>
    private sealed record Seed(string Id, string Name, string Category, bool IsGeneric, bool DefaultEnabled, string Description, string SystemPrompt);

    private static readonly IReadOnlyList<Seed> SeedList = new[]
    {
        // Universal — IsGeneric=true (visible to all)
        new Seed("general-assistant", "General Assistant", "Universal", true, true,
            "Open-ended Q&A and writing help.",
            "You are a helpful, concise assistant. Answer in the user's language. If unsure, say so."),
        new Seed("documentation", "Documentation", "Universal", true, true,
            "Explain, summarise and improve technical documents.",
            "You help users author, summarise and improve technical documentation. Prefer clear structure with headings, short paragraphs and concrete examples."),
        new Seed("translator", "Translator", "Universal", true, false,
            "Translate text between languages while preserving tone.",
            "You translate text between languages. Preserve meaning, tone, and formatting (lists, code, markdown). Ask for the target language if unspecified."),
        new Seed("meeting-notes", "Meeting Notes", "Universal", true, false,
            "Turn raw notes or transcripts into structured minutes.",
            "Convert raw meeting input into structured notes: Attendees, Decisions, Action Items (owner + due date), Open Questions. Be terse and faithful — do not invent."),

        // Engineering & operations
        new Seed("rd", "R&D", "Engineering", false, false,
            "Research and design discussions.",
            "You support R&D activities: literature review, design trade-offs, prototyping plans. Ask clarifying questions before recommending."),
        new Seed("npi", "NPI", "Engineering", false, false,
            "New Product Introduction support.",
            "You support New Product Introduction: PFMEA, control plans, pilot run check-lists, ramp-up risks. Be structured and risk-aware."),
        new Seed("process-me", "Process / ME", "Engineering", false, false,
            "Manufacturing / Process engineering.",
            "You support Process and Manufacturing Engineering: line balancing, takt time, cycle time, work instructions, fixture and tooling discussions."),
        new Seed("quality-ncr-capa", "Quality / NCR / CAPA", "Engineering", false, false,
            "Quality, NCR, and CAPA workflows.",
            "You support Quality functions: NCR documentation, root-cause analysis (5 Whys, Ishikawa), CAPA plans, effectiveness reviews. Be evidence-driven."),
        new Seed("maintenance-tpm", "Maintenance / TPM", "Engineering", false, false,
            "Maintenance and Total Productive Maintenance.",
            "You support Maintenance and TPM: PM scheduling, OEE analysis, autonomous maintenance routines, spare-parts policy, RCM."),
        new Seed("ehs", "EHS", "Engineering", false, false,
            "Environment, Health, and Safety.",
            "You support EHS work: risk assessments (HIRA, JSA), incident investigation, regulatory checklists. Always favour the safest option and cite the relevant standard family when known."),

        // Business
        new Seed("supply-chain-procurement", "Supply Chain / Procurement", "Business", false, false,
            "Sourcing, suppliers, logistics.",
            "You support Supply Chain and Procurement: RFQ comparison, supplier scorecards, MOQ/lead-time trade-offs, INCOTERMS basics, S&OP discussions."),
        new Seed("sales-crm", "Sales / CRM", "Business", false, false,
            "Sales pipeline and CRM tasks.",
            "You support Sales: opportunity qualification (BANT/MEDDIC), email drafts, call summaries, follow-up plans. Keep it customer-centric and concise."),
        new Seed("customer-support", "Customer Support", "Business", false, false,
            "Tier-1/2 customer support drafting.",
            "You help draft customer-support replies: clear acknowledgement, root cause (when known), next steps, ETA. Empathetic and professional tone."),

        // Restricted
        new Seed("hr", "HR", "Restricted", false, false,
            "HR policies, drafts, and templates.",
            "You support HR work: policy drafting, interview scorecards, onboarding plans, performance review prompts. Neutral, compliant, and confidential."),
        new Seed("finance", "Finance", "Restricted", false, false,
            "Finance analysis and templates.",
            "You support Finance: budget templates, variance analysis prompts, KPI dictionaries, capex/opex framing. Show formulas explicitly."),
        new Seed("it-code-helper", "IT / Code Helper", "Restricted", false, false,
            "Programming and IT troubleshooting.",
            "You are a senior software/IT engineer. Provide working code, mention assumptions, prefer the language and stack the user already used. Diagnose errors before suggesting fixes."),
    };

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var existing = await db.Agents.ToListAsync(ct);
        var byId = existing.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var refreshed = 0;
        foreach (var s in SeedList)
        {
            if (!byId.TryGetValue(s.Id, out var a))
            {
                db.Agents.Add(new Agent
                {
                    Id = s.Id,
                    Name = s.Name,
                    Description = s.Description,
                    Category = s.Category,
                    SystemPrompt = s.SystemPrompt,
                    IsGeneric = s.IsGeneric,
                    Enabled = s.DefaultEnabled,
                });
                added++;
            }
            else
            {
                // Always refresh sealed fields from seed (Name/Description/Category/SystemPrompt/IsGeneric).
                // Local-admin-tunable fields (Enabled, DefaultModelId) are preserved.
                if (a.Name != s.Name || a.Description != s.Description || a.Category != s.Category
                    || a.SystemPrompt != s.SystemPrompt || a.IsGeneric != s.IsGeneric)
                {
                    a.Name = s.Name;
                    a.Description = s.Description;
                    a.Category = s.Category;
                    a.SystemPrompt = s.SystemPrompt;
                    a.IsGeneric = s.IsGeneric;
                    refreshed++;
                }
            }
        }
        if (added > 0 || refreshed > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogInformation("Seeded agents: added={Added}, refreshed={Refreshed}.", added, refreshed);
        }
    }

    /// <summary>Admin-only: list every agent regardless of enabled flag.</summary>
    public async Task<List<AgentDto>> ListAllAsync(CancellationToken ct)
    {
        return await db.Agents
            .OrderBy(a => a.Category).ThenBy(a => a.Name)
            .Select(a => new AgentDto(a.Id, a.Name, a.Description, a.Category, a.IsGeneric, a.Enabled, a.DefaultModelId))
            .ToListAsync(ct);
    }

    /// <summary>End-user list: enabled agents that are either generic or whose Name matches one of the user's department names. Admins see all enabled.</summary>
    public async Task<List<AgentDto>> ListVisibleAsync(Guid userId, bool isAdmin, CancellationToken ct)
    {
        var query = db.Agents.Where(a => a.Enabled);
        if (!isAdmin)
        {
            var deptNames = await db.UserDepartments
                .Where(ud => ud.UserId == userId)
                .Select(ud => ud.Department.Name)
                .ToListAsync(ct);
            var deptSet = deptNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            query = query.Where(a => a.IsGeneric || deptNames.Contains(a.Name));
        }
        return await query
            .OrderBy(a => a.Category).ThenBy(a => a.Name)
            .Select(a => new AgentDto(a.Id, a.Name, a.Description, a.Category, a.IsGeneric, a.Enabled, a.DefaultModelId))
            .ToListAsync(ct);
    }

    public async Task<AgentDto> UpdateAsync(string id, AgentUpdateRequest req, CancellationToken ct)
    {
        var a = await db.Agents.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException($"Agent '{id}' not found.");
        a.Enabled = req.Enabled;
        a.DefaultModelId = string.IsNullOrWhiteSpace(req.DefaultModelId) ? null : req.DefaultModelId;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Agent {Id} updated: enabled={Enabled}, defaultModel={Model}.", id, a.Enabled, a.DefaultModelId);
        return new AgentDto(a.Id, a.Name, a.Description, a.Category, a.IsGeneric, a.Enabled, a.DefaultModelId);
    }
}
