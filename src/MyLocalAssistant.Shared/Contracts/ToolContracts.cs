namespace MyLocalAssistant.Shared.Contracts;

/// <summary>
/// Source of a tool: <c>builtin</c> ships with the server (compiled in),
/// <c>plugin</c> is an out-of-process executable installed under <c>./tools/</c>.
/// </summary>
public static class ToolSources
{
    public const string BuiltIn = "builtin";
    public const string Plugin  = "plugin";
}

/// <summary>
/// Tool-calling protocol a tool needs the LLM to support.
/// <c>none</c> = no tool-calling needed (rare; informational only).
/// <c>tags</c> = XML-tag protocol: a custom <c>&lt;tool_call&gt;{...}&lt;/tool_call&gt;</c> grammar
/// injected into a single flattened prompt (works on most local models, see ChatService).
/// <c>json</c> = OpenAI-style JSON function calling (Llama 3.1+, Qwen 2.5, ...).
/// <c>native</c> = the provider's own structured tool-calling API (e.g. Anthropic's
/// <c>tools</c>/<c>tool_use</c> content blocks). Requires the chat provider to implement
/// <c>INativeToolChatProvider</c> (server-side); ChatService drives a typed message/tool_result
/// loop instead of text-tag scraping. Chosen over <c>tags</c> specifically because strong
/// models (Claude included) can drift to their own trained tool-call conventions and never
/// emit the literal <c>&lt;tool_call&gt;</c> string — with <c>tags</c> that drift is silent
/// (the model's fabricated tool call and result stream straight through as ordinary visible
/// text, and the real tool never runs).
/// </summary>
public static class ToolCallProtocols
{
    public const string None = "none";
    public const string Tags = "tags";
    public const string Json = "json";
    public const string Native = "native";
}

/// <summary>One tool exposed by a tool. Multiple tools may each expose several tools.</summary>
public sealed record ToolFunctionDto(
    string Name,
    string Description,
    /// <summary>JSON-Schema describing the tool arguments (object with properties).</summary>
    string ArgumentsSchemaJson);

/// <summary>Capability requirements the host checks against the active model before binding a tool.</summary>
public sealed record ToolRequirementsDto(
    /// <summary>Minimum tool-calling protocol (see <see cref="ToolCallProtocols"/>).</summary>
    string Tools,
    /// <summary>Minimum context window in thousands of tokens (e.g., 4 for a 4k model).</summary>
    int MinContextK);

/// <summary>
/// Public view of a registered tool. Returned by <c>GET /api/admin/tools</c>.
/// </summary>
public sealed record ToolDto(
    string Id,
    string Name,
    string Description,
    string Category,
    /// <summary>See <see cref="ToolSources"/>.</summary>
    string Source,
    string? Version,
    /// <summary>Publisher name from the manifest (plug-ins) or "MyLocalAssistant" (built-ins).</summary>
    string? Publisher,
    /// <summary>Trusted-key id used to sign the manifest (plug-ins only; null for built-ins).</summary>
    string? KeyId,
    bool Enabled,
    /// <summary>Free-form JSON the global admin can edit; interpretation is per-tool.</summary>
    string? ConfigJson,
    ToolRequirementsDto Requires,
    IReadOnlyList<ToolFunctionDto> Tools);

/// <summary>Patch payload for <c>PATCH /api/admin/tools/{id}</c>.</summary>
public sealed record ToolUpdateRequest(
    bool Enabled,
    string? ConfigJson);
