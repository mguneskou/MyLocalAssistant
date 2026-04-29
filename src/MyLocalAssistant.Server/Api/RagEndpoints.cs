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
            var grantCounts = await db.RagCollectionGrants
                .GroupBy(g => g.CollectionId)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count);
            return Results.Ok(list.Select(c => new RagCollectionDto(
                c.Id, c.Name, c.Description,
                counts.TryGetValue(c.Id, out var n) ? n : 0,
                c.CreatedAt,
                c.AccessMode.ToString(),
                grantCounts.TryGetValue(c.Id, out var g) ? g : 0)));
        });

        group.MapPost("/collections", async (CreateCollectionRequest req, IngestionService svc) =>
        {
            try
            {
                var mode = ParseAccessMode(req.AccessMode);
                var c = await svc.CreateCollectionAsync(req.Name, req.Description, mode);
                return Results.Created($"/api/admin/rag/collections/{c.Id}", new RagCollectionDto(
                    c.Id, c.Name, c.Description, 0, c.CreatedAt, c.AccessMode.ToString(), 0));
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

        group.MapPatch("/collections/{id:guid}", async (Guid id, UpdateCollectionRequest req, AppDbContext db) =>
        {
            var c = await db.RagCollections.FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound();
            try { c.AccessMode = ParseAccessMode(req.AccessMode); }
            catch (ArgumentException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "validation_failed");
            }
            if (req.Description is not null) c.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
            await db.SaveChangesAsync();
            var docCount = await db.RagDocuments.CountAsync(d => d.CollectionId == id);
            var grantCount = await db.RagCollectionGrants.CountAsync(g => g.CollectionId == id);
            return Results.Ok(new RagCollectionDto(c.Id, c.Name, c.Description, docCount, c.CreatedAt, c.AccessMode.ToString(), grantCount));
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

        // ---- Collection permissions (grants) ----
        group.MapGet("/collections/{id:guid}/grants", async (Guid id, AppDbContext db) =>
        {
            if (!await db.RagCollections.AnyAsync(c => c.Id == id)) return Results.NotFound();
            var grants = await db.RagCollectionGrants.Where(g => g.CollectionId == id).ToListAsync();

            var userIds = grants.Where(g => g.PrincipalKind == PrincipalKind.User).Select(g => g.PrincipalId).ToList();
            var deptIds = grants.Where(g => g.PrincipalKind == PrincipalKind.Department).Select(g => g.PrincipalId).ToList();
            var roleIds = grants.Where(g => g.PrincipalKind == PrincipalKind.Role).Select(g => g.PrincipalId).ToList();
            var users = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
            var depts = await db.Departments.Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name);
            var roles = await db.Roles.Where(r => roleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name);

            return Results.Ok(grants.Select(g => new CollectionGrantDto(
                g.Id,
                g.PrincipalKind.ToString(),
                g.PrincipalId,
                g.PrincipalKind switch
                {
                    PrincipalKind.User => users.GetValueOrDefault(g.PrincipalId),
                    PrincipalKind.Department => depts.GetValueOrDefault(g.PrincipalId),
                    PrincipalKind.Role => roles.GetValueOrDefault(g.PrincipalId),
                    _ => null,
                },
                g.CreatedAt)));
        });

        group.MapPost("/collections/{id:guid}/grants", async (Guid id, AddCollectionGrantRequest req, AppDbContext db, HttpContext http) =>
        {
            if (!await db.RagCollections.AnyAsync(c => c.Id == id)) return Results.NotFound();
            if (!Enum.TryParse<PrincipalKind>(req.PrincipalKind, ignoreCase: true, out var kind))
                return Results.Problem("PrincipalKind must be User, Department or Role.", statusCode: 400, title: "validation_failed");
            if (req.PrincipalId == Guid.Empty)
                return Results.Problem("PrincipalId is required.", statusCode: 400, title: "validation_failed");

            // Verify the principal exists. Defense-in-depth: never grant access to a phantom id.
            var exists = kind switch
            {
                PrincipalKind.User => await db.Users.AnyAsync(u => u.Id == req.PrincipalId),
                PrincipalKind.Department => await db.Departments.AnyAsync(d => d.Id == req.PrincipalId),
                PrincipalKind.Role => await db.Roles.AnyAsync(r => r.Id == req.PrincipalId),
                _ => false,
            };
            if (!exists) return Results.Problem("Principal not found.", statusCode: 404, title: "not_found");

            // Idempotent: skip if already granted.
            var dup = await db.RagCollectionGrants.FirstOrDefaultAsync(g =>
                g.CollectionId == id && g.PrincipalKind == kind && g.PrincipalId == req.PrincipalId);
            if (dup is not null) return Results.Conflict();

            Guid? createdBy = null;
            var subClaim = http.User.FindFirst("sub")?.Value;
            if (Guid.TryParse(subClaim, out var s)) createdBy = s;

            var grant = new RagCollectionGrant
            {
                CollectionId = id,
                PrincipalKind = kind,
                PrincipalId = req.PrincipalId,
                CreatedByUserId = createdBy,
            };
            db.RagCollectionGrants.Add(grant);
            await db.SaveChangesAsync();
            return Results.Created($"/api/admin/rag/collections/{id}/grants/{grant.Id}",
                new CollectionGrantDto(grant.Id, kind.ToString(), grant.PrincipalId, null, grant.CreatedAt));
        });

        group.MapDelete("/collections/{id:guid}/grants/{grantId:long}", async (Guid id, long grantId, AppDbContext db) =>
        {
            var g = await db.RagCollectionGrants.FirstOrDefaultAsync(x => x.Id == grantId && x.CollectionId == id);
            if (g is null) return Results.NotFound();
            db.RagCollectionGrants.Remove(g);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }

    private static CollectionAccessMode ParseAccessMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return CollectionAccessMode.Restricted;
        if (Enum.TryParse<CollectionAccessMode>(raw, ignoreCase: true, out var m)) return m;
        throw new ArgumentException("AccessMode must be 'Public' or 'Restricted'.");
    }
}
