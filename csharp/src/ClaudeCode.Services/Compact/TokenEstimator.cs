namespace ClaudeCode.Services.Compact;

using System.Text.Json;
using ClaudeCode.Services.Api;

/// <summary>
/// Provides token count estimates for conversation messages using a regex that
/// approximates the cl100k_base tokenizer split points used by OpenAI and Anthropic models.
/// This is significantly more accurate than the chars/4 heuristic, especially for code
/// and non-English text.
/// </summary>
public static class TokenEstimator
{
    // Regex approximating cl100k_base split points
    private static readonly System.Text.RegularExpressions.Regex _tokenPat =
        new(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Estimates the token count for a string by counting regex match spans
    /// that approximate cl100k_base tokenizer split points.
    /// </summary>
    /// <param name="text">The text to estimate. <see langword="null"/> or empty returns 0.</param>
    /// <returns>Estimated token count; 0 for null or empty input.</returns>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return _tokenPat.Matches(text).Count;
    }

    /// <summary>
    /// Estimates the total token count for a sequence of raw message strings,
    /// adding 4 tokens of overhead per message for role framing.
    /// </summary>
    /// <param name="messages">The message strings to estimate. Must not be <see langword="null"/>.</param>
    /// <returns>Estimated total token count including per-message overhead.</returns>
    public static int EstimateMessages(IEnumerable<string> messages) =>
        messages.Sum(EstimateTokens) + messages.Count() * 4; // 4 tokens overhead per message

    /// <summary>
    /// Estimates the total token count for a list of conversation messages,
    /// including a small per-message overhead for role framing.
    /// </summary>
    /// <param name="messages">The messages to estimate. Must not be <see langword="null"/>.</param>
    /// <returns>Estimated total token count across all messages.</returns>
    public static int EstimateMessageTokens(IReadOnlyList<MessageParam> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        int total = 0;
        foreach (var msg in messages)
        {
            // Role overhead (~4 tokens per turn for formatting)
            total += 4;
            total += EstimateContentTokens(msg.Content);
        }
        return total;
    }

    /// <summary>
    /// Gets the context window token limit for a given model identifier.
    /// All current Claude models use a 200 K token context window.
    /// </summary>
    /// <param name="model">The model identifier. Must not be <see langword="null"/>.</param>
    /// <returns>The context window size in tokens.</returns>
    public static int GetContextWindow(string model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // All current Claude (Haiku, Sonnet, Opus) generations share a 200 K context.
        if (model.Contains("opus", StringComparison.OrdinalIgnoreCase))
            return 200_000;
        if (model.Contains("sonnet", StringComparison.OrdinalIgnoreCase))
            return 200_000;
        if (model.Contains("haiku", StringComparison.OrdinalIgnoreCase))
            return 200_000;

        return 200_000; // Conservative default for unknown models
    }

    /// <summary>
    /// Returns <see langword="true"/> when the estimated token usage exceeds 60% of the
    /// model's context window and the conversation is long enough to benefit from compaction.
    /// </summary>
    /// <param name="messages">The current conversation history. Must not be <see langword="null"/>.</param>
    /// <param name="model">The model identifier in use. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> if auto-compaction should be triggered; otherwise <see langword="false"/>.
    /// </returns>
    public static bool ShouldAutoCompact(IReadOnlyList<MessageParam> messages, string model)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(model);

        var estimated = EstimateMessageTokens(messages);
        var contextWindow = GetContextWindow(model);
        return estimated > contextWindow * 0.6;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Estimates tokens for a content field that is either a plain string,
    /// an array of content blocks, or any other JSON value.
    /// </summary>
    private static int EstimateContentTokens(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return EstimateTokens(content.GetString() ?? string.Empty);

        if (content.ValueKind == JsonValueKind.Array)
        {
            int total = 0;
            foreach (var block in content.EnumerateArray())
                total += EstimateTokens(block.GetRawText());
            return total;
        }

        return EstimateTokens(content.GetRawText());
    }
}
