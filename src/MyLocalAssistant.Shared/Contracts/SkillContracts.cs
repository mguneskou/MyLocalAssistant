namespace MyLocalAssistant.Shared.Contracts;

/// <summary>
/// Source of a skill: <c>builtin</c> ships with the server (compiled in),
/// <c>plugin</c> is an out-of-process executable installed under <c>./skills/</c>.
/// </summary>
public static class SkillSources
{
    public const string BuiltIn = "builtin";
    public const string Plugin  = "plugin";
}

/// <summary>
/// Tool-calling protocol a skill needs the LLM to support.
/// <c>none</c> = no tool-calling needed (rare; informational only).
/// <c>tags</c> = XML-tag protocol (works on most local models, see ChatService).
/// <c>json</c> = OpenAI-style JSON function calling (Llama 3.1+, Qwen 2.5, ...).
/// </summary>
public static class ToolCallProtocols
{
    public const string None = "none";
    public const string Tags = "tags";
    public const string Json = "json";
}

/// <summary>One tool exposed by a skill. Multiple skills may each expose several tools.</summary>
public sealed record SkillToolDto(
    string Name,
    string Description,
    /// <summary>JSON-Schema describing the tool arguments (object with properties).</summary>
    string ArgumentsSchemaJson);

/// <summary>Capability requirements the host checks against the active model before binding a skill.</summary>
public sealed record SkillRequirementsDto(
    /// <summary>Minimum tool-calling protocol (see <see cref="ToolCallProtocols"/>).</summary>
    string Tools,
    /// <summary>Minimum context window in thousands of tokens (e.g., 4 for a 4k model).</summary>
    int MinContextK);

/// <summary>
/// Public view of a registered skill. Returned by <c>GET /api/admin/skills</c>.
/// </summary>
public sealed record SkillDto(
    string Id,
    string Name,
    string Description,
    string Category,
    /// <summary>See <see cref="SkillSources"/>.</summary>
    string Source,
    string? Version,
    bool Enabled,
    /// <summary>Free-form JSON the global admin can edit; interpretation is per-skill.</summary>
    string? ConfigJson,
    SkillRequirementsDto Requires,
    IReadOnlyList<SkillToolDto> Tools);

/// <summary>Patch payload for <c>PATCH /api/admin/skills/{id}</c>.</summary>
public sealed record SkillUpdateRequest(
    bool Enabled,
    string? ConfigJson);
