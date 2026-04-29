using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Server.Persistence;

namespace MyLocalAssistant.Server.Rag;

/// <summary>
/// Orchestrates collection management and document ingestion (parse → chunk → embed → upsert).
/// </summary>
public sealed class IngestionService(
    AppDbContext db,
    IVectorStore store,
    EmbeddingService embedding,
    ILogger<IngestionService> log)
{
    public async Task<RagCollection> CreateCollectionAsync(string name, string? description, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        var trimmed = name.Trim();
        if (await db.RagCollections.AnyAsync(x => x.Name == trimmed, ct))
            throw new InvalidOperationException($"Collection '{trimmed}' already exists.");
        var c = new RagCollection { Name = trimmed, Description = description?.Trim() };
        db.RagCollections.Add(c);
        await db.SaveChangesAsync(ct);
        return c;
    }

    public async Task<List<RagCollection>> ListCollectionsAsync(CancellationToken ct = default)
    {
        var rows = await db.RagCollections.ToListAsync(ct);
        return rows.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task DeleteCollectionAsync(Guid collectionId, CancellationToken ct = default)
    {
        var c = await db.RagCollections.Include(x => x.Documents).FirstOrDefaultAsync(x => x.Id == collectionId, ct)
            ?? throw new KeyNotFoundException("Collection not found.");
        await store.DeleteCollectionAsync(c.Id.ToString("N"), ct);
        db.RagDocuments.RemoveRange(c.Documents);
        db.RagCollections.Remove(c);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<RagDocument>> ListDocumentsAsync(Guid collectionId, CancellationToken ct = default)
    {
        var rows = await db.RagDocuments.Where(d => d.CollectionId == collectionId).ToListAsync(ct);
        return rows.OrderByDescending(d => d.IngestedAt).ToList();
    }

    public async Task DeleteDocumentAsync(Guid collectionId, Guid documentId, CancellationToken ct = default)
    {
        var d = await db.RagDocuments.FirstOrDefaultAsync(x => x.Id == documentId && x.CollectionId == collectionId, ct)
            ?? throw new KeyNotFoundException("Document not found.");
        await store.DeleteByDocumentAsync(collectionId.ToString("N"), documentId, ct);
        db.RagDocuments.Remove(d);
        await db.SaveChangesAsync(ct);
    }

    public async Task<RagDocument> IngestAsync(
        Guid collectionId,
        Stream content,
        string fileName,
        string? contentType,
        CancellationToken ct = default)
    {
        if (!embedding.IsLoaded)
            throw new InvalidOperationException("Embedding model is not loaded. Activate one in Server Settings first.");
        if (!DocumentParsers.IsSupported(fileName))
            throw new InvalidOperationException($"File type not supported: {Path.GetExtension(fileName)}");

        var collection = await db.RagCollections.FirstOrDefaultAsync(c => c.Id == collectionId, ct)
            ?? throw new KeyNotFoundException("Collection not found.");

        // Buffer the upload into memory so we can hash + parse + count size deterministically.
        await using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        ms.Position = 0;
        var size = ms.Length;
        var sha = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
        ms.Position = 0;

        await store.EnsureCollectionAsync(collection.Id.ToString("N"), embedding.EmbeddingDimension, ct);

        var pages = DocumentParsers.Parse(ms, fileName);
        var document = new RagDocument
        {
            CollectionId = collection.Id,
            FileName = fileName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType!,
            SizeBytes = size,
            Sha256 = sha,
        };
        db.RagDocuments.Add(document);
        await db.SaveChangesAsync(ct);

        try
        {
            // Replace any prior version of this filename in the collection (best-effort cleanup).
            var prior = await db.RagDocuments
                .Where(d => d.CollectionId == collection.Id && d.FileName == fileName && d.Id != document.Id)
                .ToListAsync(ct);
            foreach (var p in prior)
            {
                await store.DeleteByDocumentAsync(collection.Id.ToString("N"), p.Id, ct);
                db.RagDocuments.Remove(p);
            }
            if (prior.Count > 0) await db.SaveChangesAsync(ct);

            var batch = new List<VectorRecord>();
            var totalChunks = 0;
            foreach (var page in pages)
            {
                var idx = 0;
                foreach (var chunk in TextChunker.Chunk(page.Text))
                {
                    ct.ThrowIfCancellationRequested();
                    var vec = await embedding.EmbedAsync(chunk, ct);
                    batch.Add(new VectorRecord(
                        ChunkId: $"{document.Id:N}#{page.Page}#{idx}",
                        DocumentId: document.Id,
                        Text: chunk,
                        Vector: vec,
                        Source: fileName,
                        Page: page.Page));
                    idx++;
                    totalChunks++;
                    if (batch.Count >= 64)
                    {
                        await store.UpsertAsync(collection.Id.ToString("N"), batch, ct);
                        batch.Clear();
                    }
                }
            }
            if (batch.Count > 0)
                await store.UpsertAsync(collection.Id.ToString("N"), batch, ct);

            document.ChunkCount = totalChunks;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Ingested {File} into {Coll}: {Chunks} chunks.", fileName, collection.Name, totalChunks);
            return document;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ingestion failed for {File} in {Coll}; rolling back.", fileName, collection.Name);
            try { await store.DeleteByDocumentAsync(collection.Id.ToString("N"), document.Id, ct); } catch { /* best-effort */ }
            db.RagDocuments.Remove(document);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }
}
