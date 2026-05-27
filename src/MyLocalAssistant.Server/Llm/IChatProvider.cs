using MyLocalAssistant.Core.Models;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// Single seam used by <see cref="ChatService"/> to stream tokens regardless of where
/// inference actually runs (local LLamaSharp or a cloud REST API).
/// </summary>
/// <remarks>
/// Implementations are stateless from the caller's point of view: <see cref="GenerateAsync"/>
/// is called with a fully-rendered prompt and returns text chunks. Cloud providers translate
/// that into a single-user-message chat request, which preserves <see cref="ChatService"/>'s
/// tool-calling grammar (it already injects tool definitions into the prompt body).
/// </remarks>
public interface IChatProvider
{
    /// <summary>Which catalog source this provider serves.</summary>
    ModelSource Source { get; }

    /// <summary>True iff a chat call would succeed right now (model loaded / API key configured).</summary>
    bool IsReady(CatalogEntry entry);

    /// <summary>One-line human-readable reason <see cref="IsReady"/> returned false.</summary>
    string? UnavailableReason(CatalogEntry entry);

    /// <summary>
    /// Cloud entries: a no-op (the API is always "loaded"). Local entries: load the GGUF.
    /// Throws on failure; <see cref="ModelManager"/> catches and exposes via <c>LastError</c>.
    /// </summary>
    Task LoadAsync(CatalogEntry entry, string? localFilePath, CancellationToken ct);

    /// <summary>Cloud entries: tear down the HttpClient if needed. Local entries: unload the GGUF.</summary>
    Task UnloadAsync();

    IAsyncEnumerable<string> GenerateAsync(
        CatalogEntry entry,
        string prompt,
        int maxTokens,
        IReadOnlyList<string> stops,
        CancellationToken ct);
}
