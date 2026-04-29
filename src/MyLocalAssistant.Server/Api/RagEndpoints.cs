using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Server.Rag;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class RagEndpoints
{
    public static IEndpointRouteBuilder MapRagEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/rag")
            .WithTags("Admin/Rag")
            .RequireAuthorization("Admin");

        group.MapGet("/collections", async (IngestionService svc, AppDbContext db) =>
        {
            var list = await svc.ListCollectionsAsync();
            var counts = await db.RagDocuments
                .GroupBy(d => d.CollectionId)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count);
            return Results.Ok(list.Select(c => new RagCollectionDto(
                c.Id, c.Name, c.Description,
                counts.TryGetValue(c.Id, out var n) ? n : 0,
                c.CreatedAt)));
        });

        group.MapPost("/collections", async (CreateCollectionRequest req, IngestionService svc) =>
        {
            try
            {
                var c = await svc.CreateCollectionAsync(req.Name, req.Description);
                return Results.Created($"/api/admin/rag/collections/{c.Id}", new RagCollectionDto(
                    c.Id, c.Name, c.Description, 0, c.CreatedAt));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "validation_failed");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict, title: "conflict");
            }
        });

        group.MapDelete("/collections/{id:guid}", async (Guid id, IngestionService svc) =>
        {
            try { await svc.DeleteCollectionAsync(id); return Results.NoContent(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        group.MapGet("/collections/{id:guid}/documents", async (Guid id, IngestionService svc) =>
        {
            var docs = await svc.ListDocumentsAsync(id);
            return Results.Ok(docs.Select(d => new RagDocumentDto(
                d.Id, d.FileName, d.ContentType, d.SizeBytes, d.ChunkCount, d.IngestedAt, d.Sha256)));
        });

        group.MapPost("/collections/{id:guid}/documents",
            async (Guid id, HttpRequest request, IngestionService svc, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest("Multipart form-data with a 'file' field is required.");
            var form = await request.ReadFormAsync(ct);
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.BadRequest("Missing file.");
            try
            {
                await using var s = file.OpenReadStream();
                var doc = await svc.IngestAsync(id, s, file.FileName, file.ContentType, ct);
                return Results.Created($"/api/admin/rag/collections/{id}/documents/{doc.Id}",
                    new RagDocumentDto(doc.Id, doc.FileName, doc.ContentType, doc.SizeBytes, doc.ChunkCount, doc.IngestedAt, doc.Sha256));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "ingestion_failed");
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status415UnsupportedMediaType, title: "unsupported_type");
            }
        }).DisableAntiforgery();

        group.MapDelete("/collections/{id:guid}/documents/{docId:guid}",
            async (Guid id, Guid docId, IngestionService svc) =>
        {
            try { await svc.DeleteDocumentAsync(id, docId); return Results.NoContent(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        // Diagnostic search: vector top-K against a single collection. Verifies the full pipeline.
        group.MapPost("/collections/{id:guid}/search",
            async (Guid id, RagSearchRequest req, IVectorStore store, EmbeddingService emb, AppDbContext db, CancellationToken ct) =>
        {
            if (!emb.IsLoaded) return Results.Problem("Embedding model is not loaded.", statusCode: 400);
            if (string.IsNullOrWhiteSpace(req.Query)) return Results.BadRequest("query is required.");
            if (!await db.RagCollections.AnyAsync(c => c.Id == id, ct)) return Results.NotFound();
            await store.EnsureCollectionAsync(id.ToString("N"), emb.EmbeddingDimension, ct);
            var vec = await emb.EmbedAsync(req.Query, ct);
            var hits = await store.SearchAsync(id.ToString("N"), vec, req.K <= 0 ? 4 : req.K, ct);
            return Results.Ok(hits.Select(h => new RagSearchHitDto(h.ChunkId, h.DocumentId, h.Source, h.Page, h.Distance, h.Text)));
        });

        return app;
    }
}
