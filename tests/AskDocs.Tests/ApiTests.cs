using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AskDocs.Api;
using AskDocs.Api.Answering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AskDocs.Tests;

/// <summary>Boots the real app (SQLite in a temp folder, offline providers) and talks to it over HTTP.</summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), $"askdocs-tests-{Guid.NewGuid():N}");

    /// <summary>When set, replaces the answer generator (to inspect exactly what a model would receive).</summary>
    public IAnswerGenerator? Generator { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:DataDirectory", DataDir);
        builder.UseSetting("AI:Provider", "Extractive");
        builder.UseSetting("AI:Embeddings", "Hashing");
        builder.UseSetting("RateLimit:AsksPerMinute", "1000");
        if (Generator is not null)
            builder.ConfigureServices(s => s.AddSingleton(Generator));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataDir, true); } catch (IOException) { /* sqlite may still hold the file briefly */ }
    }
}

public sealed record SseEvent(string Name, JsonElement Data);

internal static class ApiHelpers
{
    private static readonly string SamplesDir = Path.Combine(AppContext.BaseDirectory, "samples");

    public static async Task<DocumentDto> UploadAsync(this HttpClient client, string fileName, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fileName);
        var response = await client.PostAsync("/api/documents", form);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DocumentDto>())!;
    }

    public static Task<DocumentDto> UploadSampleAsync(this HttpClient client, string name) =>
        client.UploadAsync(name, File.ReadAllBytes(Path.Combine(SamplesDir, name)));

    public static async Task<DocumentDto> WaitUntilReadyAsync(this HttpClient client, Guid id)
    {
        for (var i = 0; i < 100; i++)
        {
            var docs = await client.GetFromJsonAsync<List<DocumentDto>>("/api/documents");
            var doc = docs!.Single(d => d.Id == id);
            if (doc.Status is "Ready" or "Failed") return doc;
            await Task.Delay(100);
        }
        throw new TimeoutException("Document was not indexed in time.");
    }

    public static async Task<List<SseEvent>> AskAsync(this HttpClient client, string question, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["question"] = question };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);

        var response = await client.PostAsJsonAsync("/api/ask", body);
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");

        var events = new List<SseEvent>();
        foreach (var block in (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var name = lines.Single(l => l.StartsWith("event: "))["event: ".Length..];
            var data = lines.Single(l => l.StartsWith("data: "))["data: ".Length..];
            events.Add(new SseEvent(name, JsonDocument.Parse(data).RootElement.Clone()));
        }
        return events;
    }

    public static string Answer(this List<SseEvent> events) =>
        string.Concat(events.Where(e => e.Name == "token").Select(e => e.Data.GetProperty("text").GetString()));
}

public sealed class ApiTests : IDisposable
{
    private readonly AppFactory _factory = new();
    private readonly HttpClient _client;

    public ApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Uploaded_document_is_indexed_and_answers_cite_it()
    {
        var doc = await _client.UploadSampleAsync("brightpath-employee-handbook.md");
        (await _client.WaitUntilReadyAsync(doc.Id)).ChunkCount.ShouldBeGreaterThan(0);

        var events = await _client.AskAsync("How many days of paid annual leave do employees get?");

        events.Select(e => e.Name).First().ShouldBe("sources");
        events.Select(e => e.Name).Last().ShouldBe("done");
        var citations = events[0].Data.GetProperty("citations");
        citations.GetArrayLength().ShouldBeGreaterThan(0);
        citations[0].GetProperty("documentName").GetString().ShouldBe("brightpath-employee-handbook.md");
        events.Answer().ShouldContain("24 days of paid annual leave");
        events.Answer().ShouldContain("[1]");
    }

    [Fact]
    public async Task Unrelated_questions_are_answered_with_not_found_without_calling_the_model()
    {
        var doc = await _client.UploadSampleAsync("solar-rooftop-faq.txt");
        await _client.WaitUntilReadyAsync(doc.Id);

        var events = await _client.AskAsync("Who won the cricket world cup?");

        events[0].Data.GetProperty("citations").GetArrayLength().ShouldBe(0);
        events.Answer().ShouldBe(AskService.NotFoundAnswer);
        events[^1].Data.GetProperty("answered").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Questions_can_be_scoped_to_selected_documents()
    {
        var handbook = await _client.UploadSampleAsync("brightpath-employee-handbook.md");
        var faq = await _client.UploadSampleAsync("solar-rooftop-faq.txt");
        await _client.WaitUntilReadyAsync(handbook.Id);
        await _client.WaitUntilReadyAsync(faq.Id);

        var events = await _client.AskAsync("How long do panels last and what is the leave policy?", new { documentIds = new[] { faq.Id } });

        var citations = events[0].Data.GetProperty("citations").EnumerateArray().ToList();
        citations.ShouldNotBeEmpty();
        citations.ShouldAllBe(c => c.GetProperty("documentName").GetString() == "solar-rooftop-faq.txt");
    }

    [Fact]
    public async Task Same_file_uploaded_twice_is_stored_once()
    {
        var first = await _client.UploadSampleAsync("solar-rooftop-faq.txt");
        var second = await _client.UploadSampleAsync("solar-rooftop-faq.txt");

        second.Id.ShouldBe(first.Id);
        (await _client.GetFromJsonAsync<List<DocumentDto>>("/api/documents"))!.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("notes.docx", "hello")]
    [InlineData("fake.pdf", "this is not a pdf")]
    public async Task Unsupported_or_disguised_files_are_rejected(string name, string content)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", name } };

        var response = await _client.PostAsync("/api/documents", form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deleting_a_document_removes_it_from_search()
    {
        var doc = await _client.UploadSampleAsync("solar-rooftop-faq.txt");
        await _client.WaitUntilReadyAsync(doc.Id);

        (await _client.DeleteAsync($"/api/documents/{doc.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _client.AskAsync("What is net metering?"))[0].Data.GetProperty("citations").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Empty_question_is_a_bad_request()
    {
        (await _client.PostAsJsonAsync("/api/ask", new { question = " " })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

/// <summary>What a real model would receive: grounded sources, the question and trimmed history.</summary>
public sealed class GeneratorContractTests
{
    private sealed class RecordingGenerator : IAnswerGenerator
    {
        public AnswerRequest? Last { get; private set; }
        public bool Fail { get; init; }
        public string DisplayName => "Recorder";

        public async IAsyncEnumerable<string> StreamAsync(AnswerRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Last = request;
            await Task.Yield();
            if (Fail) throw new HttpRequestException("provider down");
            yield return "ok";
        }
    }

    [Fact]
    public async Task Generator_receives_ranked_sources_and_history()
    {
        var recorder = new RecordingGenerator();
        using var factory = new AppFactory { Generator = recorder };
        var client = factory.CreateClient();
        var doc = await client.UploadSampleAsync("brightpath-employee-handbook.md");
        await client.WaitUntilReadyAsync(doc.Id);

        var events = await client.AskAsync("What is the notice period after confirmation?",
            new { history = new[] { new { role = "user", content = "Tell me about probation" }, new { role = "assistant", content = "It is six months [1]." } } });

        events[0].Data.GetProperty("provider").GetString().ShouldBe("Recorder");
        recorder.Last.ShouldNotBeNull();
        recorder.Last.Sources[0].Chunk.Text.ShouldContain("60 days");
        recorder.Last.History.Count.ShouldBe(2);
        events.Answer().ShouldBe("ok");
    }

    [Fact]
    public async Task Provider_failures_become_an_error_event_not_a_broken_stream()
    {
        using var factory = new AppFactory { Generator = new RecordingGenerator { Fail = true } };
        var client = factory.CreateClient();
        var doc = await client.UploadSampleAsync("solar-rooftop-faq.txt");
        await client.WaitUntilReadyAsync(doc.Id);

        var events = await client.AskAsync("What is net metering?");

        events[^1].Name.ShouldBe("error");
        events[^1].Data.GetProperty("message").GetString()!.ShouldContain("Recorder");
    }
}
