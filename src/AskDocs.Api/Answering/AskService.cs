using System.Runtime.CompilerServices;
using AskDocs.Api.Search;
using Microsoft.Extensions.Options;

namespace AskDocs.Api.Answering;

public sealed class RetrievalOptions
{
    public int TopK { get; set; } = 6;

    /// <summary>
    /// Minimum cosine similarity for a source to count as relevant when it shares no keyword with the question.
    /// Below this (and with no keyword hit) the question is answered with "not found" and the model isn't called.
    /// </summary>
    public float MinSimilarity { get; set; } = 0.35f;
}

public sealed record AskRequest(string Question, IReadOnlyList<Guid>? DocumentIds, IReadOnlyList<ChatTurn>? History);

public sealed record Citation(int Number, Guid DocumentId, string DocumentName, int PageNumber, string Snippet, double Score);

/// <summary>Events streamed to the client over Server-Sent Events.</summary>
public abstract record AskEvent;
public sealed record SourcesEvent(IReadOnlyList<Citation> Citations, string Provider) : AskEvent;
public sealed record TokenEvent(string Text) : AskEvent;
public sealed record DoneEvent(bool Answered) : AskEvent;
public sealed record ErrorEvent(string Message) : AskEvent;

public sealed class AskService(
    HybridIndex index,
    IEmbedder embedder,
    IAnswerGenerator generator,
    IOptions<RetrievalOptions> options,
    ILogger<AskService> logger)
{
    public const string NotFoundAnswer = "I couldn't find anything about that in the uploaded documents. Try rephrasing, or upload a document that covers it.";

    public async IAsyncEnumerable<AskEvent> AskAsync(AskRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var o = options.Value;
        var question = request.Question.Trim();

        var queryVector = (await embedder.EmbedAsync([question], ct))[0];
        var scope = request.DocumentIds is { Count: > 0 } ids ? ids.ToHashSet() : null;
        var hits = index.Search(question, queryVector, o.TopK, scope)
            .Where(h => h.Bm25 > 0 || h.Cosine >= o.MinSimilarity)
            .ToList();

        var terms = Tokenizer.Tokenize(question).ToHashSet();
        var citations = hits.Select((h, i) => new Citation(i + 1, h.Chunk.DocumentId, h.Chunk.DocumentName, h.Chunk.PageNumber,
            Snippet(h.Chunk.Text, terms), Math.Round(h.Score, 4))).ToList();
        yield return new SourcesEvent(citations, generator.DisplayName);

        // Relevance gate: no grounded context means no model call (and no chance to answer from thin air).
        if (hits.Count == 0)
        {
            yield return new TokenEvent(NotFoundAnswer);
            yield return new DoneEvent(false);
            yield break;
        }

        var stream = generator.StreamAsync(new AnswerRequest(question, hits, request.History ?? []), ct).GetAsyncEnumerator(ct);
        string? failure = null;
        try
        {
            while (true)
            {
                string token;
                try
                {
                    if (!await stream.MoveNextAsync()) break;
                    token = stream.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Answer generation failed with {Provider}", generator.DisplayName);
                    failure = $"The {generator.DisplayName} provider failed to answer ({ex.GetType().Name}). Check the server logs and provider configuration.";
                    break;
                }
                yield return new TokenEvent(token);
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }

        if (failure is not null)
        {
            yield return new ErrorEvent(failure);
            yield break;
        }

        yield return new DoneEvent(true);
    }

    /// <summary>The ~320-character window of the chunk that best matches the question, so the UI shows the cited part.</summary>
    internal static string Snippet(string text, ISet<string> terms, int maxLength = 320)
    {
        if (text.Length <= maxLength) return text;

        var sentences = Sentences.Split(text).ToList();
        var best = sentences.Select((s, i) => (Index: i, Score: Sentences.Overlap(s, terms))).MaxBy(x => x.Score).Index;

        var window = new List<string> { sentences[best] };
        var length = sentences[best].Length;
        int before = best - 1, after = best + 1;
        while (true)
        {
            if (after < sentences.Count && length + sentences[after].Length < maxLength) { window.Add(sentences[after]); length += sentences[after++].Length + 1; }
            else if (before >= 0 && length + sentences[before].Length < maxLength) { window.Insert(0, sentences[before]); length += sentences[before--].Length + 1; }
            else break;
        }

        var snippet = string.Join(' ', window);
        if (snippet.Length > maxLength) snippet = snippet[..maxLength].TrimEnd();
        return (before >= 0 ? "…" : "") + snippet + (after < sentences.Count ? "…" : "");
    }
}
