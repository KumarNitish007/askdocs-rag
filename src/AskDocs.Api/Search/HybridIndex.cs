namespace AskDocs.Api.Search;

public sealed record IndexedChunk(long Id, Guid DocumentId, string DocumentName, int PageNumber, string Text, float[] Vector);

public sealed record SearchHit(IndexedChunk Chunk, double Score, double Bm25, float Cosine);

/// <summary>
/// In-memory hybrid retriever over all indexed chunks:
/// <list type="bullet">
/// <item><b>BM25</b> for exact terms (names, codes, numbers),</item>
/// <item><b>vector cosine</b> for paraphrases,</item>
/// <item>merged with <b>Reciprocal Rank Fusion</b>, which needs no score calibration between the two.</item>
/// </list>
/// Writers swap in a new immutable snapshot, so searches never take a lock and never see a half-built index.
/// Holding a few hundred thousand chunks in memory is fine for a single-node app; a vector database is
/// the next step beyond that.
/// </summary>
public sealed class HybridIndex
{
    private const double K1 = 1.2, B = 0.75, RrfK = 60;
    private const int CandidatePool = 50;

    private Snapshot _snapshot = Snapshot.Empty;
    private readonly object _writeLock = new();

    public int Count => _snapshot.Chunks.Count;

    public void Upsert(Guid documentId, IEnumerable<IndexedChunk> chunks)
    {
        lock (_writeLock)
        {
            var kept = _snapshot.Chunks.Where(c => c.Chunk.DocumentId != documentId);
            _snapshot = Snapshot.Build(kept.Select(c => c.Chunk).Concat(chunks));
        }
    }

    public void Remove(Guid documentId) => Upsert(documentId, []);

    public void Load(IEnumerable<IndexedChunk> chunks)
    {
        lock (_writeLock) _snapshot = Snapshot.Build(chunks);
    }

    public IReadOnlyList<SearchHit> Search(string query, float[] queryVector, int topK, ISet<Guid>? onlyDocuments = null)
    {
        var s = _snapshot;
        var terms = Tokenizer.Tokenize(query).Distinct().ToList();
        var candidates = Enumerable.Range(0, s.Chunks.Count)
            .Where(i => onlyDocuments is null || onlyDocuments.Contains(s.Chunks[i].Chunk.DocumentId))
            .ToList();
        if (candidates.Count == 0) return [];

        var bm25 = candidates.ToDictionary(i => i, i => Bm25(s, i, terms));
        var cosine = candidates.ToDictionary(i => i, i => VectorMath.Cosine(queryVector, s.Chunks[i].Chunk.Vector));

        var byKeyword = candidates.Where(i => bm25[i] > 0).OrderByDescending(i => bm25[i]).Take(CandidatePool).ToList();
        var byVector = candidates.OrderByDescending(i => cosine[i]).Take(CandidatePool).ToList();

        var fused = new Dictionary<int, double>();
        for (var r = 0; r < byKeyword.Count; r++) fused[byKeyword[r]] = fused.GetValueOrDefault(byKeyword[r]) + 1 / (RrfK + r + 1);
        for (var r = 0; r < byVector.Count; r++) fused[byVector[r]] = fused.GetValueOrDefault(byVector[r]) + 1 / (RrfK + r + 1);

        return fused.OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => new SearchHit(s.Chunks[kv.Key].Chunk, kv.Value, bm25[kv.Key], cosine[kv.Key]))
            .ToList();
    }

    private static double Bm25(Snapshot s, int i, List<string> terms)
    {
        var entry = s.Chunks[i];
        double score = 0;
        foreach (var term in terms)
        {
            if (!entry.TermFrequency.TryGetValue(term, out var tf) || !s.DocumentFrequency.TryGetValue(term, out var df)) continue;
            var idf = Math.Log(1 + (s.Chunks.Count - df + 0.5) / (df + 0.5));
            score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * entry.Length / s.AverageLength));
        }
        return score;
    }

    private sealed record Entry(IndexedChunk Chunk, Dictionary<string, int> TermFrequency, int Length);

    private sealed record Snapshot(IReadOnlyList<Entry> Chunks, Dictionary<string, int> DocumentFrequency, double AverageLength)
    {
        public static readonly Snapshot Empty = new([], [], 1);

        public static Snapshot Build(IEnumerable<IndexedChunk> chunks)
        {
            var entries = new List<Entry>();
            var df = new Dictionary<string, int>();
            foreach (var chunk in chunks)
            {
                var tokens = Tokenizer.Tokenize(chunk.Text);
                var tf = tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
                foreach (var term in tf.Keys) df[term] = df.GetValueOrDefault(term) + 1;
                entries.Add(new Entry(chunk, tf, tokens.Count));
            }
            return new Snapshot(entries, df, entries.Count == 0 ? 1 : Math.Max(1, entries.Average(e => e.Length)));
        }
    }
}
