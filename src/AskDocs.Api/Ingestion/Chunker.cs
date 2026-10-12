using System.Text;
using System.Text.RegularExpressions;

namespace AskDocs.Api.Ingestion;

public sealed record TextChunk(int Ordinal, int PageNumber, string Text);

/// <summary>
/// Splits pages into overlapping, sentence-aligned chunks.
/// Chunks never cross a page boundary, so every citation points at exactly one page.
/// </summary>
public sealed partial class Chunker(int targetChars = 900, int overlapChars = 150)
{
    public IReadOnlyList<TextChunk> Split(IEnumerable<PageText> pages)
    {
        var chunks = new List<TextChunk>();
        foreach (var page in pages)
        {
            var sentences = SplitSentences(page.Text);
            var current = new List<string>();
            var length = 0;

            foreach (var sentence in sentences)
            {
                if (length > 0 && length + sentence.Length > targetChars)
                {
                    chunks.Add(new TextChunk(chunks.Count, page.PageNumber, Join(current)));

                    // Carry the tail of the previous chunk forward so an answer spanning the cut is still retrievable.
                    var carried = new List<string>();
                    var carriedLength = 0;
                    for (var i = current.Count - 1; i >= 0 && carriedLength + current[i].Length <= overlapChars; i--)
                    {
                        carried.Insert(0, current[i]);
                        carriedLength += current[i].Length + 1;
                    }
                    current = carried;
                    length = carriedLength;
                }

                current.Add(sentence);
                length += sentence.Length + 1;
            }

            if (current.Count > 0)
                chunks.Add(new TextChunk(chunks.Count, page.PageNumber, Join(current)));
        }
        return chunks;
    }

    /// <summary>Sentence split that also hard-wraps very long "sentences" (tables, run-on text).</summary>
    private List<string> SplitSentences(string text)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var raw in SentenceBoundary().Split(paragraph.Replace('\n', ' ')))
            {
                var sentence = raw.Trim();
                for (var start = 0; start < sentence.Length; start += targetChars)
                    result.Add(sentence.Substring(start, Math.Min(targetChars, sentence.Length - start)));
            }
        }
        return result;
    }

    private static string Join(List<string> sentences) =>
        new StringBuilder().AppendJoin(' ', sentences).ToString();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9""'(\[])")]
    private static partial Regex SentenceBoundary();
}
