using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AskDocs.Api.Answering;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ChatModel { get; set; } = "llama3.2";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
}

/// <summary>Answers with a local model served by Ollama (free, offline, private). Streams NDJSON from /api/chat.</summary>
public sealed class OllamaAnswerGenerator(HttpClient http, string model) : IAnswerGenerator
{
    public string DisplayName => $"Ollama ({model})";

    public async IAsyncEnumerable<string> StreamAsync(AnswerRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<object> { new { role = "system", content = Prompt.System } };
        messages.AddRange(Prompt.TrimHistory(request.History).Select(t => new { role = t.Role, content = t.Content }));
        messages.Add(new { role = "user", content = Prompt.BuildUserMessage(request.Question, request.Sources) });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { model, messages, stream = true, options = new { temperature = 0.2 } })
        };
        using var response = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
            if (chunk?.Message?.Content is { Length: > 0 } content) yield return content;
            if (chunk?.Done == true) yield break;
        }
    }

    private sealed record ChatChunk(
        [property: JsonPropertyName("message")] ChatMessage? Message,
        [property: JsonPropertyName("done")] bool Done);

    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
}
