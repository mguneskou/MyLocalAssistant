using Apache.Arrow;
using Apache.Arrow.Types;
using lancedb;
using Microsoft.Extensions.Logging;
using LanceTable = lancedb.Table;

namespace MyLocalAssistant.Server.Rag;

/// <summary>
/// LanceDB-backed vector store. One Connection per process; one LanceTable per collection
/// (LanceTable name = "coll_{collectionId-without-dashes}"). Tables have schema:
///   chunk_id : string, document_id : string, text : string, source : string,
///   page : int32, vector : FixedSizeList&lt;float, dim&gt;.
/// </summary>
public sealed class LanceDbVectorStore : IVectorStore
{
    private readonly ILogger<LanceDbVectorStore> _log;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Dictionary<string, (LanceTable LanceTable, int Dim)> _tables = new(StringComparer.OrdinalIgnoreCase);
    private Connection? _conn;

    public LanceDbVectorStore(ILogger<LanceDbVectorStore> log)
    {
        _log = log;
    }

    private async Task<Connection> EnsureConnectedAsync()
    {
        if (_conn is not null) return _conn;
        await _initLock.WaitAsync();
        try
        {
            if (_conn is null)
            {
                Directory.CreateDirectory(ServerPaths.VectorsDirectory);
                var c = new Connection();
                await c.Connect(ServerPaths.VectorsDirectory);
                _conn = c;
                _log.LogInformation("LanceDB connected at {Path}", ServerPaths.VectorsDirectory);
            }
            return _conn;
        }
        finally { _initLock.Release(); }
    }

    private static string TableName(string collectionId) =>
        "coll_" + collectionId.Replace("-", "").ToLowerInvariant();

    private static Schema BuildSchema(int dim)
    {
        var valueField = new Field("item", FloatType.Default, nullable: false);
        var vectorType = new FixedSizeListType(valueField, dim);
        return new Schema.Builder()
            .Field(new Field("chunk_id", StringType.Default, nullable: false))
            .Field(new Field("document_id", StringType.Default, nullable: false))
            .Field(new Field("text", StringType.Default, nullable: false))
            .Field(new Field("source", StringType.Default, nullable: false))
            .Field(new Field("page", Int32Type.Default, nullable: false))
            .Field(new Field("vector", vectorType, nullable: false))
            .Build();
    }

    public async Task EnsureCollectionAsync(string collectionId, int dimension, CancellationToken ct = default)
    {
        if (dimension <= 0) throw new ArgumentException("Dimension must be > 0.", nameof(dimension));
        if (_tables.TryGetValue(collectionId, out var existing))
        {
            if (existing.Dim != dimension)
                throw new InvalidOperationException(
                    $"Collection '{collectionId}' was created with dim={existing.Dim}, but embedding model produces dim={dimension}. " +
                    "Either re-ingest after changing the embedding model, or delete the collection.");
            return;
        }

        var conn = await EnsureConnectedAsync();
        var name = TableName(collectionId);
        var names = await conn.TableNames();
        LanceTable LanceTable;
        if (names.Contains(name))
        {
            LanceTable = await conn.OpenTable(name);
        }
        else
        {
            var schema = BuildSchema(dimension);
            LanceTable = await conn.CreateTable(name, new CreateTableOptions { Schema = schema });
            _log.LogInformation("Created LanceDB LanceTable {Name} (dim={Dim})", name, dimension);
        }
        _tables[collectionId] = (LanceTable, dimension);
    }

    public async Task UpsertAsync(string collectionId, IReadOnlyList<VectorRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return;
        if (!_tables.TryGetValue(collectionId, out var entry))
            throw new InvalidOperationException($"Collection '{collectionId}' not initialized. Call EnsureCollectionAsync first.");
        var (LanceTable, dim) = entry;

        var schema = BuildSchema(dim);
        var chunkIdBuilder = new StringArray.Builder();
        var docIdBuilder = new StringArray.Builder();
        var textBuilder = new StringArray.Builder();
        var sourceBuilder = new StringArray.Builder();
        var pageBuilder = new Int32Array.Builder();
        var vectorField = new Field("item", FloatType.Default, nullable: false);
        var vectorBuilder = new FixedSizeListArray.Builder(vectorField, dim);
        var valueBuilder = (FloatArray.Builder)vectorBuilder.ValueBuilder;

        foreach (var r in records)
        {
            if (r.Vector.Length != dim)
                throw new InvalidOperationException($"Record vector length {r.Vector.Length} != LanceTable dim {dim}.");
            chunkIdBuilder.Append(r.ChunkId);
            docIdBuilder.Append(r.DocumentId.ToString("N"));
            textBuilder.Append(r.Text);
            sourceBuilder.Append(r.Source);
            pageBuilder.Append(r.Page);
            vectorBuilder.Append();
            foreach (var v in r.Vector) valueBuilder.Append(v);
        }

        var batch = new RecordBatch(schema, new IArrowArray[]
        {
            chunkIdBuilder.Build(),
            docIdBuilder.Build(),
            textBuilder.Build(),
            sourceBuilder.Build(),
            pageBuilder.Build(),
            vectorBuilder.Build(),
        }, records.Count);

        await LanceTable.Add(batch);
    }

    public async Task DeleteByDocumentAsync(string collectionId, Guid documentId, CancellationToken ct = default)
    {
        if (!_tables.TryGetValue(collectionId, out var entry)) return;
        var docKey = documentId.ToString("N");
        try
        {
            await entry.LanceTable.Delete($"document_id = '{docKey}'");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Delete by document failed for {Doc} in {Coll}", documentId, collectionId);
        }
    }

    public async Task DeleteCollectionAsync(string collectionId, CancellationToken ct = default)
    {
        var conn = await EnsureConnectedAsync();
        var name = TableName(collectionId);
        if (_tables.TryGetValue(collectionId, out var entry))
        {
            entry.LanceTable.Dispose();
            _tables.Remove(collectionId);
        }
        try { await conn.DropTable(name); }
        catch (Exception ex) { _log.LogWarning(ex, "DropTable failed for {Name}", name); }
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(string collectionId, float[] query, int k, CancellationToken ct = default)
    {
        if (!_tables.TryGetValue(collectionId, out var entry)) return new List<VectorHit>();
        // NearestTo accepts double[] or float[]; use double[] per LanceDB sample docs.
        var dq = new double[query.Length];
        for (var i = 0; i < query.Length; i++) dq[i] = query[i];

        using var q = entry.LanceTable.Query()
            .NearestTo(dq)
            .DistanceType(DistanceType.Cosine)
            .Limit(k);
        var rows = await q.ToList();

        var hits = new List<VectorHit>(rows.Count);
        foreach (var row in rows)
        {
            float distance = 0f;
            if (row.TryGetValue("_distance", out var d) && d is not null)
            {
                if (d is float f) distance = f;
                else if (d is double dd) distance = (float)dd;
                else float.TryParse(d.ToString(), out distance);
            }
            var docIdStr = row.TryGetValue("document_id", out var dv) ? dv?.ToString() : null;
            Guid.TryParseExact(docIdStr, "N", out var docGuid);
            hits.Add(new VectorHit(
                ChunkId: row.TryGetValue("chunk_id", out var ci) ? ci?.ToString() ?? "" : "",
                DocumentId: docGuid,
                Text: row.TryGetValue("text", out var tx) ? tx?.ToString() ?? "" : "",
                Distance: distance,
                Source: row.TryGetValue("source", out var sr) ? sr?.ToString() ?? "" : "",
                Page: row.TryGetValue("page", out var pg) && pg is not null && int.TryParse(pg.ToString(), out var p) ? p : 0));
        }
        return hits;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var t in _tables.Values) t.LanceTable.Dispose();
        _tables.Clear();
        _conn?.Dispose();
        _conn = null;
        await Task.CompletedTask;
    }
}
