using MyLocalAssistant.Server.Tools;

namespace MyLocalAssistant.Server.Skills;

/// <summary>Per-invocation context handed to a skill.  Mirrors ToolContext but also carries the active skill.</summary>
public sealed record SkillContext(
    Guid UserId,
    string Username,
    bool IsAdmin,
    string AgentId,
    Guid ConversationId,
    string WorkDirectory,
    /// <summary>Raw user message that triggered this skill invocation.</summary>
    string UserMessage,
    /// <summary>Resolved tool instances the skill declared as required.</summary>
    IReadOnlyDictionary<string, ITool> Tools,
    CancellationToken CancellationToken);
