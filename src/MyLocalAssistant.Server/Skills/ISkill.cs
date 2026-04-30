using MyLocalAssistant.Server.Auth;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Server.Rag;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Skills;

/// <summary>
/// Per-invocation context handed to a skill. Contains only what the skill needs to
/// operate on this turn — never the raw HttpContext, never the AppDbContext, never
/// secrets. For plugin skills this is serialised over JSON-RPC; keep it small.
/// </summary>
public sealed record SkillContext(
    Guid UserId,
    string Username,
    bool IsAdmin,
    bool IsGlobalAdmin,
    string AgentId,
    Guid ConversationId,
    /// <summary>Per-conversation working directory: <c>&lt;install&gt;/output/&lt;conversationId&gt;/</c>. Created on first access.</summary>
    string WorkDirectory,
    /// <summary>Cancellation token bounded by the chat turn's wall-clock budget.</summary>
    CancellationToken CancellationToken)
{
    public static SkillContext FromPrincipal(
        UserPrincipals principal,
        string username,
        bool isAdmin,
        bool isGlobalAdmin,
        string agentId,
        Guid conversationId,
        string workDirectory,
        CancellationToken ct) => new(
            principal.UserId, username, isAdmin, isGlobalAdmin,
            agentId, conversationId, workDirectory, ct);
}

/// <summary>Result of a single tool invocation. Returned to the LLM as text.</summary>
public sealed record SkillResult(
    bool IsError,
    string Content,
    /// <summary>Optional structured payload retained for audit/UI; not shown to the LLM.</summary>
    string? StructuredJson = null)
{
    public static SkillResult Ok(string content, string? structured = null) => new(false, content, structured);
    public static SkillResult Error(string message) => new(true, message);
}

/// <summary>One tool invocation request resolved from the LLM's tool call.</summary>
public sealed record SkillInvocation(
    string ToolName,
    /// <summary>Raw JSON arguments object as emitted by the LLM (already validated against the tool's schema).</summary>
    string ArgumentsJson);

/// <summary>
/// Contract every skill implements. Built-in skills implement this directly;
/// plug-in skills are wrapped by <c>PluginSkill</c> which proxies over JSON-RPC
/// to a separate Windows process.
/// </summary>
public interface ISkill
{
    /// <summary>Stable string id (e.g. "math.eval", "excel-handler"). Lower-case, dot-separated by convention.</summary>
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string Category { get; }
    string Source { get; }       // "builtin" or "plugin"
    string? Version { get; }     // null for built-ins (versioned with the server)
    /// <summary>Display-name of who shipped the skill. "MyLocalAssistant" for built-ins; manifest publisher for plug-ins.</summary>
    string? Publisher { get; }
    /// <summary>Trusted-key id used to sign the manifest. Null for built-ins.</summary>
    string? KeyId { get; }

    /// <summary>The tools this skill exposes. The LLM sees these as callable functions.</summary>
    IReadOnlyList<SkillToolDto> Tools { get; }

    /// <summary>Capability requirements the host checks against the active model.</summary>
    SkillRequirementsDto Requirements { get; }

    /// <summary>
    /// Apply (and validate) the admin's <c>ConfigJson</c>. Called at startup and again
    /// whenever the global admin saves a new config. Implementations should validate
    /// strictly and throw on bad config — the registry will surface the error in the
    /// admin UI and keep the previous valid config active.
    /// </summary>
    void Configure(string? configJson);

    /// <summary>Invoke a single tool. Implementations must respect <paramref name="ctx"/>.CancellationToken.</summary>
    Task<SkillResult> InvokeAsync(SkillInvocation call, SkillContext ctx);
}
