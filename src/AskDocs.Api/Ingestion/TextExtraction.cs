using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AskDocs.Api.Ingestion;

/// <summary>Text of one page (plain-text files are a single "page").</summary>
public sealed record PageText(int PageNumber, string Text);

public static class SupportedFiles
{
    public static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown"
    };

    public static bool IsSupported(string fileName) => ContentTypes.ContainsKey(Path.GetExtension(fileName));
}

public static partial class TextExtractor
{
    public static IReadOnlyList<PageText> Extract(string path, string contentType)
    {
        var pages = contentType switch
        {
            "application/pdf" => ExtractPdf(path),
            "text/markdown" => [new PageText(1, MarkdownToText(File.ReadAllText(path, Encoding.UTF8)))],
            _ => [new PageText(1, File.ReadAllText(path, Encoding.UTF8))]
        };
        return pages.Select(p => p with { Text = Normalize(p.Text) }).Where(p => p.Text.Length > 0).ToList();
    }

    private static List<PageText> ExtractPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var pages = new List<PageText>(pdf.NumberOfPages);
        foreach (var page in pdf.GetPages())
        {
            // The content-order extractor keeps reading order and line breaks far better than page.Text.
            pages.Add(new PageText(page.Number, ContentOrderTextExtractor.GetText(page)));
        }
        return pages;
    }

    /// <summary>
    /// Turns Markdown into plain prose for chunking: headings become their own sentence-terminated paragraph
    /// (so "## Leave policy" doesn't glue onto the next sentence); list markers and emphasis are dropped.
    /// </summary>
    public static string MarkdownToText(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var heading = MarkdownHeading().Match(line);
            if (heading.Success)
            {
                var title = heading.Groups[1].Value.Trim();
                return "\n" + (title.EndsWith('.') ? title : title + ".") + "\n";
            }
            return MarkdownEmphasis().Replace(MarkdownListMarker().Replace(line, ""), "$1");
        });
        return string.Join('\n', lines);
    }

    /// <summary>Collapses runs of spaces, re-joins words hyphenated across lines, keeps paragraph breaks.</summary>
    public static string Normalize(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ');
        text = HyphenatedLineBreak().Replace(text, "$1$2");
        text = SpacesAndTabs().Replace(text, " ");
        text = ManyNewLines().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$")]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+\.)\s+")]
    private static partial Regex MarkdownListMarker();

    [GeneratedRegex(@"[*_`]{1,3}([^*_`]+)[*_`]{1,3}")]
    private static partial Regex MarkdownEmphasis();

    [GeneratedRegex(@"(\w)-\n(\w)")]
    private static partial Regex HyphenatedLineBreak();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex SpacesAndTabs();

    [GeneratedRegex(@"\n\s*\n\s*")]
    private static partial Regex ManyNewLines();
}
