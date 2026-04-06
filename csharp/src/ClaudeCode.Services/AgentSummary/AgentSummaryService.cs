namespace ClaudeCode.Services.AgentSummary;

using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeCode.Services.Api;

/// <summary>
/// Generates compressed summaries of sub-agent work for inclusion in parent context.
/// Uses a fast model to produce concise 2-3 sentence summaries. Results are cached
/// in-process so that the same agent ID is never re-summarised in the same session.
/// </summary>
public sealed class AgentSummaryService
{
    private readonly IAnthropicClient _client;

    private const string SummaryModel = "claude-haiku-4-5-20251001";
    private const int SummaryMaxTokens = 512;
    private const int TruncationInputChars = 4000;

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Initialises a new <see cref="AgentSummaryService"/>.
    /// </summary>
    /// <param name="client">Anthropic API client. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> is null.</exception>
    public AgentSummaryService(IAnthropicClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// Given the full output of a completed sub-agent, generates a 2-3 sentence
    /// summary using a fast model. The summary is cached keyed on <paramref name="agentId"/>
    /// so that the same agent is never re-summarised within the session.
    /// Returns <paramref name="fullOutput"/> unchanged if the API call fails.
    /// </summary>
    /// <param name="agentId">Unique identifier of the sub-agent session. Must not be null.</param>
    /// <param name="fullOutput">The full text output produced by the sub-agent. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A 2-3 sentence summary, or <paramref name="fullOutput"/> on error.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="agentId"/> or <paramref name="fullOutput"/> is null.
    /// </exception>
    public async Task<string> SummarizeAsync(
        string agentId,
        string fullOutput,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(fullOutput);

        // Return cached result immediately when available.
        if (_cache.TryGetValue(agentId, out var cached))
            return cached;

        // Truncate input to avoid overwhelming the summary model.
        var truncated = fullOutput.Length > TruncationInputChars
            ? fullOutput[..TruncationInputChars]
            : fullOutput;

        var prompt =
            "Summarize this agent's work in 2-3 sentences, focusing on what was accomplished " +
            "and any key outputs or findings:\n\n" + truncated;

        try
        {
            var request = new MessageRequest
            {
                Model = SummaryModel,
                MaxTokens = SummaryMaxTokens,
                Messages =
                [
                    new MessageParam
                    {
                        Role = "user",
                        Content = JsonSerializer.SerializeToElement(prompt),
                    }
                ],
            };

            var textBuilder = new System.Text.StringBuilder();
            await foreach (var evt in _client.StreamMessageAsync(request, ct).ConfigureAwait(false))
            {
                if (evt.EventType != "content_block_delta")
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(evt.Data);
                    if (doc.RootElement.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("type", out var typeEl)
                        && typeEl.GetString() == "text_delta"
                        && delta.TryGetProperty("text", out var textEl))
                    {
                        textBuilder.Append(textEl.GetString());
                    }
                }
                catch (JsonException)
                {
                    // Malformed SSE data — skip this fragment.
                }
            }

            var summary = textBuilder.ToString().Trim();
            if (string.IsNullOrEmpty(summary))
                return fullOutput;

            _cache.TryAdd(agentId, summary);
            return summary;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // API failure — return the original output unchanged.
            return fullOutput;
        }
    }

    // -------------------------------------------------------------------------
    // Internal static helpers (used by coordinator-mode formatting)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Compresses a sub-agent result string using heuristics (no API call).
    /// Used internally to format multi-agent summaries without making API calls.
    /// </summary>
    internal static string HeuristicSummarize(string agentResult, string agentTask, int maxLines = 5)
    {
        if (agentResult.Length <= 500) return agentResult;

        var lines = agentResult.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= maxLines) return agentResult;

        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"[Agent: {agentTask}]");

        bool inSummary = false;
        int summaryLines = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("## Summary", StringComparison.Ordinal)
                || line.StartsWith("# Summary", StringComparison.Ordinal))
            {
                inSummary = true;
                continue;
            }

            if (inSummary && line.StartsWith('#')) break;
            if (inSummary && summaryLines < 5) { summary.AppendLine(line); summaryLines++; }
        }

        if (summaryLines == 0)
        {
            summary.AppendLine(lines[0]);
            if (lines.Length > 3) summary.AppendLine("...");
            summary.AppendLine(lines[^2]);
            summary.AppendLine(lines[^1]);
        }

        return summary.ToString().TrimEnd();
    }

    /// <summary>Formats multiple agent results into a coordinator summary table.</summary>
    public static string FormatMultiAgentResults(
        IEnumerable<(string task, string result, bool success)> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Agent Results");
        foreach (var (task, result, success) in results)
        {
            var icon = success ? "\u2713" : "\u2717";
            sb.AppendLine($"{icon} **{task}**: {HeuristicSummarize(result, task, 2)}");
        }

        return sb.ToString();
    }
}
