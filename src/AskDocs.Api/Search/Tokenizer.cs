using System.Text;

namespace AskDocs.Api.Search;

public static class Tokenizer
{
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "can", "do", "does", "for", "from", "has", "have",
        "how", "i", "if", "in", "into", "is", "it", "its", "me", "my", "of", "on", "or", "our", "so", "than",
        "that", "the", "their", "them", "then", "there", "these", "they", "this", "to", "was", "we", "were",
        "what", "when", "where", "which", "who", "why", "will", "with", "you", "your"
    ];

    /// <summary>Lower-cased words and numbers, stop words removed, light plural stemming.</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();

        void Flush()
        {
            if (word.Length == 0) return;
            var token = Stem(word.ToString());
            word.Clear();
            if (token.Length > 1 && !StopWords.Contains(token)) tokens.Add(token);
        }

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) word.Append(char.ToLowerInvariant(c));
            else Flush();
        }
        Flush();
        return tokens;
    }

    /// <summary>Just enough stemming to match "policies" with "policy" and "days" with "day".</summary>
    private static string Stem(string w) =>
        w.Length > 4 && w.EndsWith("ies") ? w[..^3] + "y"
        : w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us") ? w[..^1]
        : w;
}
