namespace MyLocalAssistant.Shared.Contracts;

/// <summary>
/// Result of extracting text from a file the user attached to a single chat turn.
/// The extracted text is one-shot context — the server does not persist it to the RAG store.
/// </summary>
public sealed record AttachmentExtractResult(
    string FileName,
    int CharCount,
    int PageCount,
    bool Truncated,
    string Text);
