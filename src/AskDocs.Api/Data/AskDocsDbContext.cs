using Microsoft.EntityFrameworkCore;

namespace AskDocs.Api.Data;

public enum DocumentStatus
{
    Queued,
    Processing,
    Ready,
    Failed
}

public sealed class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the file, so the same upload is never indexed twice.</summary>
    public required string Sha256 { get; set; }

    /// <summary>Name of the stored file under the uploads folder (a generated name, never the user's path).</summary>
    public required string StoredName { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Queued;
    public string? Error { get; set; }
    public int PageCount { get; set; }
    public int ChunkCount { get; set; }

    /// <summary>Which embedder produced the vectors; a change of embedder triggers re-indexing.</summary>
    public string? EmbeddingModel { get; set; }

    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? IndexedAtUtc { get; set; }

    public List<Chunk> Chunks { get; set; } = [];
}

public sealed class Chunk
{
    public long Id { get; set; }
    public Guid DocumentId { get; set; }
    public int Ordinal { get; set; }
    public int PageNumber { get; set; }
    public required string Text { get; set; }

    /// <summary>float[] embedding stored as little-endian bytes.</summary>
    public required byte[] Embedding { get; set; }
}

public sealed class AskDocsDbContext(DbContextOptions<AskDocsDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Chunk> Chunks => Set<Chunk>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Document>(e =>
        {
            e.Property(d => d.FileName).HasMaxLength(260);
            e.Property(d => d.ContentType).HasMaxLength(100);
            e.Property(d => d.Sha256).HasMaxLength(64);
            e.HasIndex(d => d.Sha256).IsUnique();
            e.Property(d => d.StoredName).HasMaxLength(100);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.Error).HasMaxLength(1000);
            e.Property(d => d.EmbeddingModel).HasMaxLength(100);
            e.HasMany(d => d.Chunks).WithOne().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Chunk>(e =>
        {
            e.HasIndex(c => new { c.DocumentId, c.Ordinal }).IsUnique();
        });
    }
}

public static class VectorBytes
{
    public static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] ToVector(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }
}
