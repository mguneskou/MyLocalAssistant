using MyLocalAssistant.Core.Models;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// Optional capability a chat provider can implement when the underlying API has its own
/// structured tool-calling protocol (e.g. Anthropic's <c>tools</c> / <c>tool_use</c> content
/// blocks), instead of relying on the model faithfully imitating ChatService's custom
/// <c>&lt;tool_call&gt;</c> text grammar (see <see cref="ToolCallProtocols.Tags"/>).
/// </summary>
/// <remarks>
/// The text-tag protocol works by scanning the raw token stream for a literal substring.
/// Strong models sometimes drift to their own trained tool-call conventions instead of the
/// exact string the prompt asked for — when that happens under the tags protocol, nothing
/// is intercepted, the model's fabricated tool call *and* a fabricated tool result stream
/// straight through as ordinary visible text, and the real tool never runs. A model whose
/// capability is <see cref="ToolCallProtocols.Native"/> is never asked to imitate any text
/// grammar: ChatService sends tool definitions and prior tool results as structured API
/// fields and reads tool calls back as structured content blocks, so there is no string for
/// the model to get wrong.
/// </remarks>
public interface INativeToolChatProvider
{
    /// <summary>
    /// Runs one model turn with the given system prompt, message history, and tool
    /// definitions, streaming text as it arrives and finishing with exactly one
    /// <see cref="NativeMessageCompleteEvent"/> once the turn is complete.
    /// </summary>
    IAsyncEnumerable<NativeChatEvent> GenerateWithToolsAsync(
        CatalogEntry entry,
        string? systemPrompt,
        IReadOnlyList<NativeChatMessage> messages,
        IReadOnlyList<ToolFunctionDto> tools,
        int maxTokens,
        CancellationToken ct);
}

/// <summary>One message in a native tool-calling conversation (role is "user" or "assistant").</summary>
public sealed record NativeChatMessage(string Role, IReadOnlyList<NativeContentBlock> Content);

/// <summary>Base type for the content blocks a native message can carry.</summary>
public abstract record NativeContentBlock;

/// <summary>Plain assistant/user text.</summary>
public sealed record NativeTextBlock(string Text) : NativeContentBlock;

/// <summary>A tool the model asked to call, with its arguments as raw (already-parsed) JSON.</summary>
public sealed record NativeToolUseBlock(string Id, string Name, string ArgumentsJson) : NativeContentBlock;

/// <summary>The result of executing a previously requested <see cref="NativeToolUseBlock"/>.</summary>
public sealed record NativeToolResultBlock(string ToolUseId, string Content, bool IsError) : NativeContentBlock;

/// <summary>Base type for events streamed out of <see cref="INativeToolChatProvider.GenerateWithToolsAsync"/>.</summary>
public abstract record NativeChatEvent;

/// <summary>An incremental chunk of assistant text — yield straight to the user-visible stream.</summary>
public sealed record NativeTextDeltaEvent(string Text) : NativeChatEvent;

/// <summary>
/// Terminal event for one model turn: the fully assembled assistant message (text and/or
/// tool-use blocks) plus the API's stop reason (e.g. <c>"end_turn"</c>, <c>"tool_use"</c>).
/// Exactly one of these is yielded per call, always last.
/// </summary>
public sealed record NativeMessageCompleteEvent(NativeChatMessage Message, string StopReason) : NativeChatEvent;
