namespace MyLocalAssistant.Server.Rag;

/// <summary>One row in the vector store.</summary>
public sealed record VectorRecord(
    string ChunkId,
    Guid DocumentId,
    string Text,
    float[] Vector,
    string Source,
    int Page);

/// <summary>One search result with score and metadata.</summary>
public sealed record VectorHit(
    string ChunkId,
    Guid DocumentId,
    string Text,
    float Distance,
    string Source,
    int Page);

/// <summary>
/// Abstraction over the vector DB so we can swap LanceDB for something else.
/// One physical "table" per collection (id-based).
/// </summary>
public interface IVectorStore : IAsyncDisposable
{
    /// <summary>Create the per-collection table if missing. Throws if dim mismatch.</summary>
    Task EnsureCollectionAsync(string collectionId, int dimension, CancellationToken ct = default);

    /// <summary>Append rows. Caller is responsible for deleting any prior versions of the same DocumentId.</summary>
    Task UpsertAsync(string collectionId, IReadOnlyList<VectorRecord> records, CancellationToken ct = default);

    Task DeleteByDocumentAsync(string collectionId, Guid documentId, CancellationToken ct = default);

    Task DeleteCollectionAsync(string collectionId, CancellationToken ct = default);

    Task<IReadOnlyList<VectorHit>> SearchAsync(string collectionId, float[] query, int k, CancellationToken ct = default);
}
