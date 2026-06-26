using MyLocalAssistant.Server.Tools;

namespace MyLocalAssistant.Server.Skills;

/// <summary>
/// A Skill is a reusable, named workflow that combines a system prompt, a set of
/// tools, and an output contract.  Skills are the Hermes-inspired abstraction that
/// sits above raw tool calls and below a full conversation.
/// </summary>
public interface ISkill
{
    /// <summary>Stable lowercase identifier, e.g. "shortage-review".</summary>
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string Category { get; }

    /// <summary>System-level instructions injected before the user message when this skill is active.</summary>
    string SystemPrompt { get; }

    /// <summary>Ids of tools this skill requires.  The executor validates all are available.</summary>
    IReadOnlyList<string> RequiredToolIds { get; }

    /// <summary>Manifest that describes inputs/outputs shown in the admin UI and API.</summary>
    SkillManifest Manifest { get; }

    /// <summary>
    /// Execute the skill.  The executor calls this after injecting the system prompt and
    /// resolving tools.  Skills that don't need custom logic can return null — the
    /// executor will run the LLM with the standard tool loop.
    /// </summary>
    Task<SkillResult?> ExecuteAsync(SkillContext context, CancellationToken ct);
}
