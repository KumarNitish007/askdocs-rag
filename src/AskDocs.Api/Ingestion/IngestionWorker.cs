using System.Threading.Channels;
using AskDocs.Api.Data;
using AskDocs.Api.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskDocs.Api.Ingestion;

public sealed class StorageOptions
{
    public string DataDirectory { get; set; } = "App_Data";
    public int MaxUploadMb { get; set; } = 25;

    public string UploadsPath(string contentRoot) => Path.Combine(Path.GetFullPath(DataDirectory, contentRoot), "uploads");
}

/// <summary>Uploads return immediately; parsing, chunking and embedding happen here, one document at a time.</summary>
public sealed class IngestionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(Guid documentId, CancellationToken ct = default) => _channel.Writer.WriteAsync(documentId, ct);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed class IngestionWorker(
    IServiceScopeFactory scopes,
    IngestionQueue queue,
    HybridIndex index,
    IEmbedder embedder,
    IOptions<StorageOptions> storage,
    IWebHostEnvironment env,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    private const int EmbeddingBatchSize = 32;
    private readonly Chunker _chunker = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        await foreach (var documentId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(documentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Indexing failed for document {DocumentId}", documentId);
                await MarkFailedAsync(documentId, ex.Message, stoppingToken);
            }
        }
    }

    /// <summary>
    /// On startup: load ready chunks into the in-memory index, and re-queue anything unfinished or indexed
    /// with a different embedder (vectors from different models aren't comparable).
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AskDocsDbContext>();

        var ready = await db.Documents.AsNoTracking()
            .Where(d => d.Status == DocumentStatus.Ready && d.EmbeddingModel == embedder.Id)
            .Select(d => new { d.Id, d.FileName })
            .ToDictionaryAsync(d => d.Id, d => d.FileName, ct);

        var chunks = await db.Chunks.AsNoTracking().Where(c => ready.Keys.Contains(c.DocumentId)).ToListAsync(ct);
        index.Load(chunks.Select(c => ToIndexed(c, ready[c.DocumentId])));

        var stale = await db.Documents
            .Where(d => d.Status != DocumentStatus.Failed && (d.Status != DocumentStatus.Ready || d.EmbeddingModel != embedder.Id))
            .Select(d => d.Id)
            .ToListAsync(ct);
        foreach (var id in stale) await queue.EnqueueAsync(id, ct);

        logger.LogInformation("Search index loaded: {Chunks} chunks from {Documents} documents; {Stale} queued for (re)indexing with {Embedder}",
            chunks.Count, ready.Count, stale.Count, embedder.Id);
    }

    private async Task ProcessAsync(Guid documentId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AskDocsDbContext>();
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null) return; // deleted while queued

        doc.Status = DocumentStatus.Processing;
        doc.Error = null;
        await db.SaveChangesAsync(ct);

        var path = Path.Combine(storage.Value.UploadsPath(env.ContentRootPath), doc.StoredName);
        var pages = TextExtractor.Extract(path, doc.ContentType);
        if (pages.Count == 0)
            throw new InvalidOperationException("No extractable text found (is this a scanned PDF? OCR is not supported yet).");

        var pieces = _chunker.Split(pages);
        var vectors = new List<float[]>(pieces.Count);
        foreach (var batch in pieces.Chunk(EmbeddingBatchSize))
            vectors.AddRange(await embedder.EmbedAsync(batch.Select(p => p.Text).ToList(), ct));

        await db.Chunks.Where(c => c.DocumentId == doc.Id).ExecuteDeleteAsync(ct);
        var chunks = pieces.Select((p, i) => new Chunk
        {
            DocumentId = doc.Id,
            Ordinal = p.Ordinal,
            PageNumber = p.PageNumber,
            Text = p.Text,
            Embedding = VectorBytes.ToBytes(vectors[i])
        }).ToList();
        db.Chunks.AddRange(chunks);

        doc.Status = DocumentStatus.Ready;
        doc.PageCount = pages.Max(p => p.PageNumber);
        doc.ChunkCount = chunks.Count;
        doc.EmbeddingModel = embedder.Id;
        doc.IndexedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        index.Upsert(doc.Id, chunks.Select(c => ToIndexed(c, doc.FileName)));
        logger.LogInformation("Indexed {File}: {Pages} page(s), {Chunks} chunk(s)", doc.FileName, doc.PageCount, chunks.Count);
    }

    private async Task MarkFailedAsync(Guid documentId, string error, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AskDocsDbContext>();
        var message = error.Length > 1000 ? error[..1000] : error;
        await db.Documents.Where(d => d.Id == documentId).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, DocumentStatus.Failed)
            .SetProperty(d => d.Error, message), ct);
    }

    private static IndexedChunk ToIndexed(Chunk c, string documentName) =>
        new(c.Id, c.DocumentId, documentName, c.PageNumber, c.Text, VectorBytes.ToVector(c.Embedding));
}
