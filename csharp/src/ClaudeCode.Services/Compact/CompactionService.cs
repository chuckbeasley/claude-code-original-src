namespace ClaudeCode.Services.Compact;

using System.Text;
using System.Text.Json;
using ClaudeCode.Services.Api;

/// <summary>
/// Metrics describing the outcome of a compaction operation.
/// </summary>
/// <param name="Summary">The generated summary text inserted into the compacted history.</param>
/// <param name="OriginalTokenEstimate">Estimated token count before compaction.</param>
/// <param name="CompactedTokenEstimate">Estimated token count after compaction.</param>
/// <param name="MessagesRemoved">Number of messages removed from history and replaced by the summary.</param>
public sealed record CompactionResult(
    string Summary,
    int OriginalTokenEstimate,
    int CompactedTokenEstimate,
    int MessagesRemoved);

/// <summary>
/// Summarizes older conversation messages into a single compact summary message,
/// preserving the most recent context while dramatically reducing token usage.
/// Uses the small/fast model to generate the summary cheaply.
/// </summary>
public sealed class CompactionService
{
    /// <summary>
    /// Number of recent messages (from the end of history) to preserve verbatim.
    /// Keeps the last 2 user/assistant pairs (4 messages) as live context.
    /// </summary>
    private const int RecentMessageCount = 4;

    /// <summary>
    /// Maximum characters of a single message's content included in the summary prompt.
    /// Long messages are truncated to keep the summarization request within bounds.
    /// </summary>
    private const int MaxContentCharsPerMessage = 2000;

    /// <summary>Maximum tokens the summary model may generate.</summary>
    private const int SummaryMaxTokens = 4096;

    private readonly IAnthropicClient _client;

    /// <summary>
    /// Initializes a new <see cref="CompactionService"/>.
    /// </summary>
    /// <param name="client">The Anthropic API client used to generate the summary. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> is <see langword="null"/>.</exception>
    public CompactionService(IAnthropicClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// Compacts the supplied conversation history by summarizing older messages into a single
    /// summary turn. The most recent <see cref="RecentMessageCount"/> messages are preserved verbatim.
    /// </summary>
    /// <param name="messages">
    /// The full conversation history. If fewer than 4 messages, returns the list unchanged.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <param name="model">
    /// The main conversation model identifier; used only for token estimation. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of the <see cref="CompactionResult"/> metrics and the new compacted message list.
    /// When compaction produces no summary (empty result or no messages to remove),
    /// the original <paramref name="messages"/> list is returned unchanged and
    /// <see cref="CompactionResult.MessagesRemoved"/> is 0.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="messages"/> or <paramref name="model"/> is <see langword="null"/>.</exception>
    public async Task<(CompactionResult Result, List<MessageParam> CompactedMessages)> CompactAsync(
        List<MessageParam> messages,
        string model,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(model);

        // Not enough messages to meaningfully compact.
        if (messages.Count < RecentMessageCount)
            return (new CompactionResult(string.Empty, 0, 0, 0), messages);

        var originalEstimate = TokenEstimator.EstimateMessageTokens(messages);

        var recentCount = Math.Min(RecentMessageCount, messages.Count);
        var oldMessages = messages[..^recentCount];
        var recentMessages = messages[^recentCount..];

        if (oldMessages.Count == 0)
            return (new CompactionResult(string.Empty, originalEstimate, originalEstimate, 0), messages);

        var summaryPrompt = BuildSummaryPrompt(oldMessages);
        var summaryModel = ModelResolver.GetSmallFastModel();

        var request = new MessageRequest
        {
            Model = summaryModel,
            MaxTokens = SummaryMaxTokens,
            Messages =
            [
                new MessageParam
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement(summaryPrompt),
                },
            ],
            System =
            [
                new SystemBlock
                {
                    Text = "You are a conversation summarizer. Produce a concise summary of the " +
                           "conversation that preserves all important context, decisions, code changes, " +
                           "and file paths mentioned. Be thorough but brief.",
                },
            ],
        };

        var summaryBuilder = new StringBuilder();

        await foreach (var evt in _client.StreamMessageAsync(request, ct).ConfigureAwait(false))
        {
            if (evt.EventType != "content_block_delta")
                continue;

            using var doc = JsonDocument.Parse(evt.Data);
            var delta = doc.RootElement.GetProperty("delta");

            if (delta.TryGetProperty("type", out var typeEl)
                && typeEl.GetString() == "text_delta"
                && delta.TryGetProperty("text", out var textEl))
            {
                summaryBuilder.Append(textEl.GetString());
            }
        }

        var summary = summaryBuilder.ToString();

        if (string.IsNullOrWhiteSpace(summary))
            return (new CompactionResult(string.Empty, originalEstimate, originalEstimate, 0), messages);

        // Build compacted history: synthetic summary exchange + preserved recent messages.
        var compactedMessages = new List<MessageParam>(recentCount + 2)
        {
            new()
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(
                    $"[Previous conversation summary]\n{summary}\n[End of summary — conversation continues below]"),
            },
            new()
            {
                Role = "assistant",
                Content = JsonSerializer.SerializeToElement(
                    "I understand the context from the conversation summary. Let's continue."),
            },
        };
        compactedMessages.AddRange(recentMessages);

        // Post-compact: trim old tool result blocks to recover tokens.
        var cleanedMessages = new List<MessageParam>(CleanupToolHistory(compactedMessages));
        var compactedEstimate = TokenEstimator.EstimateMessageTokens(cleanedMessages);

        return (
            new CompactionResult(summary, originalEstimate, compactedEstimate, oldMessages.Count),
            cleanedMessages);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Trims old tool result blocks from the message list to recover tokens after compaction.
    /// Keeps the last 3 tool use/result pairs; replaces older ones with a one-liner placeholder.
    /// </summary>
    private static IReadOnlyList<MessageParam> CleanupToolHistory(IReadOnlyList<MessageParam> messages)
    {
        var result = new List<MessageParam>(messages);
        int toolResultCount = 0;

        for (int i = result.Count - 1; i >= 0; i--)
        {
            // Tool results arrive as user-role messages whose content JSON contains "tool_result".
            if (result[i].Role == "user"
                && result[i].Content.GetRawText().Contains("tool_result"))
            {
                toolResultCount++;
                if (toolResultCount > 3)
                {
                    // Replace with a compressed placeholder.
                    result[i] = result[i] with
                    {
                        Content = JsonSerializer.SerializeToElement("[tool result omitted after compact]"),
                    };
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the text prompt sent to the summarization model, listing each message
    /// by role with its extracted text content.
    /// </summary>
    private static string BuildSummaryPrompt(List<MessageParam> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Summarize the following conversation. Include all key details:");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            var role = msg.Role == "user" ? "User" : "Assistant";
            var content = ExtractTextContent(msg.Content);

            if (content.Length > MaxContentCharsPerMessage)
                content = string.Concat(content.AsSpan(0, MaxContentCharsPerMessage), "... [truncated]");

            sb.AppendLine($"**{role}**: {content}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extracts displayable text from a content field that may be a plain string,
    /// an array of typed content blocks, or a raw JSON value.
    /// </summary>
    private static string ExtractTextContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();

            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var typeEl))
                    continue;

                var type = typeEl.GetString();

                if (type == "text" && block.TryGetProperty("text", out var textEl))
                {
                    sb.Append(textEl.GetString());
                }
                else if (type == "tool_result")
                {
                    sb.Append("[tool result]");
                }
                else if (type == "tool_use")
                {
                    var name = block.TryGetProperty("name", out var nameEl)
                        ? nameEl.GetString()
                        : "unknown";
                    sb.Append($"[tool: {name}]");
                }
            }

            return sb.ToString();
        }

        return content.GetRawText();
    }
}
