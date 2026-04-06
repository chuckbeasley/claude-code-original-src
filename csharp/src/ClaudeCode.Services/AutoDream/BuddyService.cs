namespace ClaudeCode.Services.AutoDream;

using ClaudeCode.Services.Api;
using System.Text.Json;

/// <summary>
/// Generates a brief one-sentence context note describing what the user is currently working on.
/// Called after each completed assistant turn; the result is displayed as a dim hint above
/// the next REPL prompt when <see cref="ClaudeCode.Core.State.ReplModeFlags.BuddyEnabled"/> is set.
/// </summary>
public sealed class BuddyService
{
    private const string BuddyModel = "claude-haiku-4-5-20251001";
    private const int BuddyMaxTokens = 64;
    private const int BuddyTimeoutMs = 5_000;
    private const string BuddySystemPrompt =
        "You are a silent context summarizer. Respond with exactly ONE sentence of at most 12 words describing what the user is currently working on. Do not ask questions. Do not include caveats.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IAnthropicClient _client;
    private readonly CostTracker _costTracker;

    /// <summary>
    /// Initializes a new <see cref="BuddyService"/>.
    /// </summary>
    /// <param name="client">The Anthropic API client. Must not be <see langword="null"/>.</param>
    /// <param name="costTracker">Cost and usage accumulator. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public BuddyService(IAnthropicClient client, CostTracker costTracker)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _costTracker = costTracker ?? throw new ArgumentNullException(nameof(costTracker));
    }

    /// <summary>
    /// Generates a one-sentence summary of the current conversation context using a lightweight model.
    /// Takes the last 6 messages (3 user/assistant pairs) from <paramref name="recentMessages"/>
    /// and applies an internal 5-second timeout.
    /// </summary>
    /// <param name="recentMessages">The full conversation history to analyze.</param>
    /// <param name="ct">Caller-supplied cancellation token (e.g. the session token).</param>
    /// <returns>
    /// A trimmed, non-empty summary sentence; or <see langword="null"/> when the note cannot be
    /// generated within the timeout, on API/network error, or when the response is empty.
    /// </returns>
    public async Task<string?> GetContextNoteAsync(
        IReadOnlyList<MessageParam> recentMessages,
        CancellationToken ct)
    {
        if (recentMessages is null || recentMessages.Count == 0)
            return null;

        // Take the last 6 messages (3 user/assistant pairs) to keep the request small.
        var slice = recentMessages.Count > 6
            ? recentMessages.Skip(recentMessages.Count - 6).ToList()
            : recentMessages.ToList();

        var request = new MessageRequest
        {
            Model = BuddyModel,
            MaxTokens = BuddyMaxTokens,
            Messages = slice,
            System =
            [
                new SystemBlock { Text = BuddySystemPrompt },
            ],
        };

        // Enforce the 5-second cap regardless of the caller's token lifetime.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(BuddyTimeoutMs);

        var responseText = new System.Text.StringBuilder();

        try
        {
            await foreach (var sseEvent in _client
                .StreamMessageAsync(request, timeoutCts.Token)
                .ConfigureAwait(false))
            {
                switch (sseEvent.EventType)
                {
                    case "message_start":
                    {
                        // Record input-token usage so it appears in /cost output.
                        var payload = TryDeserialize<MessageStartPayload>(sseEvent.Data);
                        if (payload?.Message.Usage is { } usage)
                            _costTracker.AddUsage(BuddyModel, usage);
                        break;
                    }

                    case "content_block_delta":
                    {
                        var payload = TryDeserialize<ContentBlockDeltaPayload>(sseEvent.Data);
                        if (payload is null)
                            break;

                        var delta = payload.Delta;
                        if (delta.TryGetProperty("type", out var typeEl)
                            && typeEl.GetString() == "text_delta"
                            && delta.TryGetProperty("text", out var textEl))
                        {
                            var text = textEl.GetString();
                            if (!string.IsNullOrEmpty(text))
                                responseText.Append(text);
                        }

                        break;
                    }
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
            // Network error, API error, or any unexpected exception — return null silently.
            return null;
        }

        var result = responseText.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static T? TryDeserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch { return null; }
    }
}
