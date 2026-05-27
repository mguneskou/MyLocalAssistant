namespace MyLocalAssistant.Core.Inference;

public interface ILlmProvider : IAsyncDisposable
{
    string? LoadedModelId { get; }
    string Backend { get; }

    Task LoadAsync(string modelPath, string modelId, int contextSize, CancellationToken ct = default);

    IAsyncEnumerable<string> GenerateAsync(string prompt, int maxTokens, CancellationToken ct = default);

    /// <summary>
    /// Stream tokens with extra stop sequences appended to the provider's built-in
    /// anti-prompts. Used by the tool-calling loop to halt on <c>&lt;/tool_call&gt;</c>.
    /// </summary>
    IAsyncEnumerable<string> GenerateAsync(string prompt, int maxTokens, IReadOnlyList<string> extraStops, CancellationToken ct = default);

    Task UnloadAsync();
}
