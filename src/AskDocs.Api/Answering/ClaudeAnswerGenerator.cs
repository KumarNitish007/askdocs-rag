using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;

namespace AskDocs.Api.Answering;

public sealed class ClaudeOptions
{
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>low | medium | high. Grounded Q&A over short sources rarely needs more than medium.</summary>
    public string Effort { get; set; } = "medium";

    public int MaxTokens { get; set; } = 8000;
}

/// <summary>
/// Answers with Claude through the official Anthropic SDK, streaming tokens as they are generated.
/// The API key is read by the SDK from ANTHROPIC_API_KEY (or an `ant auth login` profile); it is never in config files.
/// </summary>
public sealed class ClaudeAnswerGenerator(AnthropicClient client, IOptions<ClaudeOptions> options, ILogger<ClaudeAnswerGenerator> logger)
    : IAnswerGenerator
{
    public string DisplayName => $"Claude ({options.Value.Model})";

    public async IAsyncEnumerable<string> StreamAsync(AnswerRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = BuildParameters(request, options.Value);

        string? stopReason = null;
        await foreach (var ev in client.Beta.Messages.CreateStreaming(parameters, ct))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                yield return text.Text;
            else if (ev.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason is { } reason)
                stopReason = reason.ToString().Replace("_", "").ToLowerInvariant(); // "refusal", "maxtokens", ...
        }

        if (stopReason is not null && stopReason.Contains("refusal"))
        {
            logger.LogWarning("Claude declined to answer a question");
            yield return "\n\n_The model declined to answer this request._";
        }
        else if (stopReason is not null && stopReason.Contains("maxtokens"))
        {
            yield return "\n\n_The answer was cut off at the configured length limit._";
        }
    }

    /// <summary>The exact request sent to the Messages API (separate so tests can inspect it).</summary>
    internal static MessageCreateParams BuildParameters(AnswerRequest request, ClaudeOptions o)
    {
        var messages = Prompt.TrimHistory(request.History)
            .Select(t => new BetaMessageParam { Role = t.Role == "user" ? Role.User : Role.Assistant, Content = t.Content })
            .ToList();
        messages.Add(new BetaMessageParam { Role = Role.User, Content = Prompt.BuildUserMessage(request.Question, request.Sources) });

        return new MessageCreateParams
        {
            Model = o.Model,
            MaxTokens = o.MaxTokens,
            System = Prompt.System,
            Messages = messages,
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(o.Effort) },
            // If a safety classifier declines the request, the API re-serves it inside the same call with the
            // server-defined fallback model for that refusal category, instead of returning an empty refusal.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
        };
    }

    private static Effort ParseEffort(string value) => value.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        _ => Effort.Medium
    };
}
