using AskDocs.Api.Answering;
using AskDocs.Api.Ingestion;
using AskDocs.Api.Search;
using Shouldly;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AskDocs.Tests;

public class ChunkerTests
{
    [Fact]
    public void Chunks_never_cross_pages_and_keep_page_numbers()
    {
        var pages = new[] { new PageText(1, "Alpha one. Alpha two."), new PageText(2, "Beta one. Beta two.") };

        var chunks = new Chunker().Split(pages);

        chunks.Select(c => (c.PageNumber, c.Text)).ShouldBe([(1, "Alpha one. Alpha two."), (2, "Beta one. Beta two.")]);
        chunks.Select(c => c.Ordinal).ShouldBe([0, 1]);
    }

    [Fact]
    public void Long_pages_are_split_with_sentence_overlap()
    {
        var sentences = Enumerable.Range(1, 30).Select(i => $"Sentence number {i} talks about topic {i}.");
        var chunks = new Chunker(targetChars: 200, overlapChars: 60).Split([new PageText(1, string.Join(" ", sentences))]);

        chunks.Count.ShouldBeGreaterThan(3);
        chunks.ShouldAllBe(c => c.Text.Length <= 260);
        // the last sentence of one chunk starts the next one
        var lastOfFirst = chunks[0].Text.Split(". ").Last().TrimEnd('.');
        chunks[1].Text.ShouldStartWith(lastOfFirst);
    }

    [Fact]
    public void A_single_huge_sentence_is_hard_wrapped()
    {
        var chunks = new Chunker(targetChars: 100, overlapChars: 0).Split([new PageText(1, new string('x', 350))]);

        chunks.Count.ShouldBe(4);
        chunks.ShouldAllBe(c => c.Text.Length <= 100);
    }
}

public class TextExtractorTests
{
    [Fact]
    public void Pdf_text_is_extracted_per_page()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("The warranty period is five years.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Returns are accepted within 30 days.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        var path = Path.Combine(Path.GetTempPath(), $"askdocs-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, builder.Build());

        try
        {
            var pages = TextExtractor.Extract(path, "application/pdf");

            pages.Select(p => p.PageNumber).ShouldBe([1, 2]);
            pages[0].Text.ShouldContain("warranty period is five years");
            pages[1].Text.ShouldContain("within 30 days");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Normalize_rejoins_hyphenated_words_and_collapses_whitespace()
    {
        TextExtractor.Normalize("infor-\nmation   is\r\n\r\n\r\nkey").ShouldBe("information is\n\nkey");
    }
}

public class EmbeddingAndTokenizerTests
{
    [Fact]
    public void Tokenizer_drops_stop_words_and_stems_plurals()
    {
        Tokenizer.Tokenize("What are the leave policies for the employees?").ShouldBe(["leave", "policy", "employee"]);
    }

    [Fact]
    public void Hashing_embeddings_are_deterministic_and_normalised()
    {
        var embedder = new HashingEmbedder();
        var a = embedder.Embed("annual leave carry forward");
        var b = embedder.Embed("annual leave carry forward");

        a.ShouldBe(b);
        VectorMath.Cosine(a, a).ShouldBe(1f, 0.0001f);
    }

    [Fact]
    public void Related_text_is_more_similar_than_unrelated_text()
    {
        var e = new HashingEmbedder();
        var query = e.Embed("how many days of annual leave");

        VectorMath.Cosine(query, e.Embed("employees receive 24 days of paid annual leave"))
            .ShouldBeGreaterThan(VectorMath.Cosine(query, e.Embed("solar panels need regular cleaning")));
    }
}

public class HybridIndexTests
{
    private static readonly HashingEmbedder Embedder = new();
    private static readonly Guid Handbook = Guid.NewGuid(), Faq = Guid.NewGuid();

    private static HybridIndex BuildIndex()
    {
        var index = new HybridIndex();
        index.Load(new[]
        {
            (Handbook, "handbook.md", 1, "Every employee receives 24 days of paid annual leave per calendar year."),
            (Handbook, "handbook.md", 1, "The probation period is six months with a 15 day notice period."),
            (Handbook, "handbook.md", 2, "The learning budget is INR 40,000 per year for courses and certifications."),
            (Faq, "solar.txt", 1, "A 3 kW rooftop system needs about 25 to 30 square metres of roof area."),
            (Faq, "solar.txt", 1, "Net metering lets you export surplus solar electricity to the grid.")
        }.Select((c, i) => new IndexedChunk(i + 1, c.Item1, c.Item2, c.Item3, c.Item4, Embedder.Embed(c.Item4))));
        return index;
    }

    private static IReadOnlyList<SearchHit> Search(HybridIndex index, string q, ISet<Guid>? only = null) =>
        index.Search(q, Embedder.Embed(q), 3, only);

    [Theory]
    [InlineData("How long is probation?", "probation period is six months")]
    [InlineData("learning budget per year", "INR 40,000")]
    [InlineData("what is net metering", "Net metering")]
    [InlineData("roof area for 3 kW", "square metres")]
    public void Best_chunk_ranks_first(string question, string expected)
    {
        Search(BuildIndex(), question)[0].Chunk.Text.ShouldContain(expected);
    }

    [Fact]
    public void Search_can_be_scoped_to_selected_documents()
    {
        var hits = Search(BuildIndex(), "annual leave and solar", new HashSet<Guid> { Faq });

        hits.ShouldNotBeEmpty();
        hits.ShouldAllBe(h => h.Chunk.DocumentId == Faq);
    }

    [Fact]
    public void Removing_a_document_drops_its_chunks()
    {
        var index = BuildIndex();
        index.Remove(Handbook);

        index.Count.ShouldBe(2);
        Search(index, "annual leave").ShouldAllBe(h => h.Chunk.DocumentId == Faq);
    }

    [Fact]
    public void Keyword_hits_carry_a_bm25_score()
    {
        var top = Search(BuildIndex(), "probation notice")[0];

        top.Bm25.ShouldBeGreaterThan(0);
        top.Score.ShouldBeGreaterThan(0);
    }
}

public class PromptTests
{
    private static SearchHit Hit(string doc, int page, string text) =>
        new(new IndexedChunk(1, Guid.NewGuid(), doc, page, text, []), 1, 1, 1);

    [Fact]
    public void Sources_are_numbered_with_document_and_page()
    {
        var message = Prompt.BuildUserMessage("  How many leave days?  ", [Hit("handbook.md", 3, "24 days."), Hit("faq.txt", 1, "Other.")]);

        message.ShouldContain("[1] handbook.md, page 3\n24 days.");
        message.ShouldContain("[2] faq.txt, page 1\nOther.");
        message.ShouldEndWith("Question: How many leave days?");
    }

    [Fact]
    public void History_is_trimmed_to_recent_turns_starting_with_a_user_turn()
    {
        var history = Enumerable.Range(0, 10)
            .Select(i => new ChatTurn(i % 2 == 0 ? "user" : "assistant", $"turn {i}"))
            .Append(new ChatTurn("system", "ignore me"))
            .ToList();

        var trimmed = Prompt.TrimHistory(history);

        trimmed.Count.ShouldBeLessThanOrEqualTo(Prompt.MaxHistoryTurns);
        trimmed[0].Role.ShouldBe("user");
        trimmed.ShouldNotContain(t => t.Role == "system");
        trimmed[^1].Content.ShouldBe("turn 9");
    }

    [Fact]
    public void Claude_request_uses_the_configured_model_streaming_safeguards_and_history()
    {
        var request = new AnswerRequest("How many leave days?", [Hit("handbook.md", 3, "24 days.")],
            [new ChatTurn("user", "hi"), new ChatTurn("assistant", "hello")]);

        var p = ClaudeAnswerGenerator.BuildParameters(request, new ClaudeOptions());
        var json = System.Text.Json.JsonSerializer.Serialize(p);

        json.ShouldContain("\"model\":\"claude-opus-5-5\"");
        json.ShouldContain("\"effort\":\"medium\"");
        json.ShouldContain("\"fallbacks\":\"default\"");
        p.Messages.Count.ShouldBe(3); // two history turns + the grounded question
    }
}

public class AnswerQualityTests
{
    [Fact]
    public void Markdown_headings_become_their_own_sentences_and_markers_are_dropped()
    {
        var text = TextExtractor.Normalize(TextExtractor.MarkdownToText("## Leave policy\nEvery employee gets **24 days**.\n- Item one"));

        text.ShouldBe("Leave policy.\n\nEvery employee gets 24 days.\nItem one");
    }

    [Fact]
    public async Task Extractive_answers_rank_sentences_across_sources()
    {
        var chunk = new IndexedChunk(1, Guid.NewGuid(), "handbook.md", 1,
            "Every employee receives 24 days of paid annual leave. Up to 10 unused annual leave days can be carried forward. Sick leave is 12 days.", []);
        var request = new AnswerRequest("How many annual leave days can be carried forward?", [new SearchHit(chunk, 1, 1, 1)], []);

        var parts = new List<string>();
        await foreach (var part in new ExtractiveAnswerGenerator().StreamAsync(request, CancellationToken.None)) parts.Add(part);

        string.Concat(parts).ShouldContain("10 unused annual leave days can be carried forward. [1]");
    }

    [Fact]
    public void Snippet_centres_on_the_matching_sentence()
    {
        var text = string.Join(" ", Enumerable.Range(1, 20).Select(i => $"Filler sentence number {i} about nothing.")) +
                   " The warranty lasts twenty five years. " +
                   string.Join(" ", Enumerable.Range(21, 20).Select(i => $"Filler sentence number {i} about nothing."));

        var snippet = AskService.Snippet(text, Tokenizer.Tokenize("how long is the warranty").ToHashSet());

        snippet.ShouldContain("The warranty lasts twenty five years.");
        snippet.ShouldStartWith("…");
        snippet.Length.ShouldBeLessThanOrEqualTo(322);
    }
}
