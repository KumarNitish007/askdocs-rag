using System.Security.Cryptography;
using System.Text.Json;
using AskDocs.Api.Answering;
using AskDocs.Api.Data;
using AskDocs.Api.Ingestion;
using AskDocs.Api.Search;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskDocs.Api;

public sealed record DocumentDto(
    Guid Id, string FileName, string ContentType, long SizeBytes, string Status, string? Error,
    int PageCount, int ChunkCount, DateTime UploadedAtUtc, DateTime? IndexedAtUtc);

public static class Endpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapAskDocs(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/info", (IAnswerGenerator generator, IEmbedder embedder, HybridIndex index) =>
            Results.Ok(new { provider = generator.DisplayName, embeddings = embedder.Id, indexedChunks = index.Count }));

        api.MapGet("/documents", async (AskDocsDbContext db, CancellationToken ct) =>
            (await db.Documents.AsNoTracking().OrderByDescending(d => d.UploadedAtUtc).ToListAsync(ct)).Select(ToDto));

        api.MapPost("/documents", UploadAsync).DisableAntiforgery();

        api.MapDelete("/documents/{id:guid}", async (Guid id, AskDocsDbContext db, HybridIndex index,
            IOptions<StorageOptions> storage, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return Results.NotFound();

            db.Documents.Remove(doc);
            await db.SaveChangesAsync(ct);
            index.Remove(id);
            File.Delete(Path.Combine(storage.Value.UploadsPath(env.ContentRootPath), doc.StoredName));
            return Results.NoContent();
        });

        api.MapPost("/ask", AskAsync).RequireRateLimiting("ask");
    }

    private static async Task<IResult> UploadAsync(
        IFormFile file, AskDocsDbContext db, IngestionQueue queue, IOptions<StorageOptions> storage,
        IWebHostEnvironment env, CancellationToken ct)
    {
        var limits = storage.Value;
        var fileName = Path.GetFileName(file.FileName);
        if (!SupportedFiles.IsSupported(fileName))
            return Problem("Only PDF, TXT and Markdown files are supported.");
        if (file.Length == 0 || file.Length > limits.MaxUploadMb * 1024L * 1024L)
            return Problem($"Files must be between 1 byte and {limits.MaxUploadMb} MB.");

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var sha = Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();

        if (await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Sha256 == sha, ct) is { } existing)
            return Results.Ok(ToDto(existing)); // same bytes already uploaded: idempotent

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension == ".pdf" && !buffer.ToArray().AsSpan(0, Math.Min(5, (int)buffer.Length)).SequenceEqual("%PDF-"u8))
            return Problem("The file does not look like a PDF.");

        var doc = new Document
        {
            FileName = fileName,
            ContentType = SupportedFiles.ContentTypes[extension],
            SizeBytes = file.Length,
            Sha256 = sha,
            StoredName = $"{Guid.NewGuid():N}{extension}" // never trust the client's file name on disk
        };

        var folder = limits.UploadsPath(env.ContentRootPath);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, doc.StoredName), buffer.ToArray(), ct);

        db.Documents.Add(doc);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(doc.Id, ct);

        return Results.Accepted($"/api/documents/{doc.Id}", ToDto(doc));
    }

    /// <summary>Streams the answer as Server-Sent Events: one "sources" event, many "token" events, then "done" or "error".</summary>
    private static async Task AskAsync(AskRequest request, AskService ask, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 2000)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { title = "Ask a question of up to 2000 characters." }, ct);
            return;
        }

        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no"; // nginx: don't buffer the stream

        await foreach (var ev in ask.AskAsync(request, ct))
        {
            var (name, payload) = ev switch
            {
                SourcesEvent s => ("sources", (object)s),
                TokenEvent t => ("token", t),
                DoneEvent d => ("done", d),
                ErrorEvent e => ("error", e),
                _ => throw new InvalidOperationException()
            };
            await http.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, payload.GetType(), Json)}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }

    private static IResult Problem(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid upload");

    private static DocumentDto ToDto(Document d) =>
        new(d.Id, d.FileName, d.ContentType, d.SizeBytes, d.Status.ToString(), d.Error, d.PageCount, d.ChunkCount, d.UploadedAtUtc, d.IndexedAtUtc);
}
