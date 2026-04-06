namespace ClaudeCode.Services.AwaySummary;

using System.Text;
using System.Text.Json;
using ClaudeCode.Services.Api;

/// <summary>
/// Generates a 1-3 sentence recap of a conversation for a user returning after being away.
/// Uses a fast model with an 8-second hard timeout and returns <see langword="null"/> silently on
/// any failure (timeout, API error, network error, or empty response).
/// </summary>
public sealed class AwaySummaryService
{
    private const string SummaryModel = "claude-haiku-4-5-20251001";
    private const int SummaryMaxTokens = 256;
    private const int TimeoutMs = 8_000;
    private const int MaxMessages = 30;
    private const string SummaryPrompt =
        "Summarize what has happened in 1-3 sentences for someone returning to this conversation.";

    private readonly IAnthropicClient _client;

    /// <summary>
    /// Initializes a new <see cref="AwaySummaryService"/>.
    /// </summary>
    /// <param name="client">Anthropic API client. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> is null.</exception>
    public AwaySummaryService(IAnthropicClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// Generates a 1-3 sentence summary of the conversation for a user returning after being away.
    /// Takes at most the last 30 messages from <paramref name="messages"/>, appends a summary
    /// request turn, and calls the API with an internal 8-second timeout.
    /// </summary>
    /// <param name="messages">
    /// The full conversation history to summarize. May be empty; must not be <see langword="null"/>.
    /// </param>
    /// <param name="ct">Caller-supplied cancellation token.</param>
    /// <returns>
    /// A trimmed, non-empty summary string, or <see langword="null"/> when the summary cannot be
    /// generated (empty history, timeout, API/network error, or empty response).
    /// </returns>
    public async Task<string?> GetSummaryAsync(
        IReadOnlyList<MessageParam> messages,
        CancellationToken ct = default)
    {
        if (messages is null || messages.Count == 0)
            return null;

        // Take the last min(messages.Count, 30) messages.
        var slice = messages.Count > MaxMessages
            ? messages.Skip(messages.Count - MaxMessages).ToList()
            : messages.ToList();

        // Append the summary request turn.
        slice.Add(new MessageParam
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(SummaryPrompt),
        });

        var request = new MessageRequest
        {
            Model = SummaryModel,
            MaxTokens = SummaryMaxTokens,
            Messages = slice,
        };

        // Combine caller token with an 8-second hard cap.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeoutMs);

        var responseText = new StringBuilder();

        try
        {
            await foreach (var sseEvent in _client
                .StreamMessageAsync(request, timeoutCts.Token)
                .ConfigureAwait(false))
            {
                if (sseEvent.EventType != "content_block_delta")
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(sseEvent.Data);
                    if (doc.RootElement.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("type", out var typeEl)
                        && typeEl.GetString() == "text_delta"
                        && delta.TryGetProperty("text", out var textEl))
                    {
                        var text = textEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                            responseText.Append(text);
                    }
                }
                catch (JsonException)
                {
                    // Malformed SSE data — skip this fragment.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or external cancellation — return null silently.
            return null;
        }
        catch
        {
            // Network error, API error, or any unexpected failure — return null silently.
            return null;
        }

        var result = responseText.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
