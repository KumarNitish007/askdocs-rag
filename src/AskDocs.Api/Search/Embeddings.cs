using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AskDocs.Api.Search;

public interface IEmbedder
{
    /// <summary>Identifies the model + dimensions; stored with every vector so a model change is detectable.</summary>
    string Id { get; }

    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

/// <summary>
/// Zero-dependency embedder: signed feature hashing of word unigrams and bigrams into a fixed-size,
/// L2-normalised vector. Not semantic like a neural model, but deterministic, fast and offline. Combined
/// with BM25 in the hybrid retriever it gives good results on keyword-heavy questions.
/// </summary>
public sealed class HashingEmbedder(int dimensions = 512) : IEmbedder
{
    public string Id => $"hashing-{dimensions}";

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
        Task.FromResult(texts.Select(Embed).ToArray());

    public float[] Embed(string text)
    {
        var vector = new float[dimensions];
        var tokens = Tokenizer.Tokenize(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            Add(vector, tokens[i], 1f);
            if (i + 1 < tokens.Count) Add(vector, tokens[i] + " " + tokens[i + 1], 0.5f);
        }
        return VectorMath.Normalize(vector);
    }

    private void Add(float[] vector, string feature, float weight)
    {
        // FNV-1a: stable across processes (string.GetHashCode is randomised per run).
        var hash = 2166136261u;
        foreach (var c in feature) hash = (hash ^ c) * 16777619u;
        vector[hash % (uint)dimensions] += (hash & 0x80000000) == 0 ? weight : -weight;
    }
}

/// <summary>Neural embeddings from a local Ollama server (e.g. nomic-embed-text).</summary>
public sealed class OllamaEmbedder(HttpClient http, string model) : IEmbedder
{
    public string Id => $"ollama-{model}";

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/api/embed", new { model, input = texts }, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct)
                   ?? throw new InvalidOperationException("Empty embedding response from Ollama.");
        return body.Embeddings.Select(VectorMath.Normalize).ToArray();
    }

    private sealed record EmbedResponse([property: JsonPropertyName("embeddings")] float[][] Embeddings);
}

public static class VectorMath
{
    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        if (sum == 0) return v;
        var inv = (float)(1 / Math.Sqrt(sum));
        for (var i = 0; i < v.Length; i++) v[i] *= inv;
        return v;
    }

    /// <summary>Cosine similarity of two already-normalised vectors (= dot product).</summary>
    public static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        float dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }
}
