using System.Threading.RateLimiting;
using Anthropic;
using AskDocs.Api;
using AskDocs.Api.Answering;
using AskDocs.Api.Data;
using AskDocs.Api.Ingestion;
using AskDocs.Api.Search;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<RetrievalOptions>(builder.Configuration.GetSection("Retrieval"));
builder.Services.Configure<ClaudeOptions>(builder.Configuration.GetSection("AI:Claude"));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("AI:Ollama"));

var storage = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
var dataDir = Path.GetFullPath(storage.DataDirectory, builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataDir);
builder.Services.AddDbContext<AskDocsDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "askdocs.db")}"));

builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = (storage.MaxUploadMb + 1) * 1024L * 1024L);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = (storage.MaxUploadMb + 1) * 1024L * 1024L);

// ---- AI providers (pluggable) -------------------------------------------------------------
// AI:Provider   = Extractive (default, no model) | Ollama | Claude
// AI:Embeddings = Hashing (default, offline)     | Ollama
var provider = builder.Configuration["AI:Provider"] ?? "Extractive";
var embeddings = builder.Configuration["AI:Embeddings"] ?? "Hashing";

builder.Services.AddHttpClient("ollama", (sp, c) =>
{
    c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OllamaOptions>>().Value.BaseUrl);
    c.Timeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddSingleton<IEmbedder>(sp => embeddings.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
    ? new OllamaEmbedder(sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama"), sp.GetRequiredService<IOptions<OllamaOptions>>().Value.EmbeddingModel)
    : new HashingEmbedder());

switch (provider.ToLowerInvariant())
{
    case "claude":
        // Credentials come from ANTHROPIC_API_KEY (or an `ant auth login` profile) - never from config files.
        builder.Services.AddSingleton(_ => new AnthropicClient());
        builder.Services.AddSingleton<IAnswerGenerator, ClaudeAnswerGenerator>();
        break;
    case "ollama":
        builder.Services.AddSingleton<IAnswerGenerator>(sp => new OllamaAnswerGenerator(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama"), sp.GetRequiredService<IOptions<OllamaOptions>>().Value.ChatModel));
        break;
    default:
        builder.Services.AddSingleton<IAnswerGenerator, ExtractiveAnswerGenerator>();
        break;
}

builder.Services.AddSingleton<HybridIndex>();
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddScoped<AskService>();

// Protects the paid model API from runaway clients.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("ask", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimit:AsksPerMinute", 20), Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddProblemDetails();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<AskDocsDbContext>().Database.EnsureCreatedAsync();

app.Logger.LogInformation("AskDocs answering with {Provider}, embeddings: {Embedder}",
    app.Services.GetRequiredService<IAnswerGenerator>().DisplayName, app.Services.GetRequiredService<IEmbedder>().Id);

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

app.MapAskDocs();
app.MapGet("/health", () => Results.Ok("healthy"));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
