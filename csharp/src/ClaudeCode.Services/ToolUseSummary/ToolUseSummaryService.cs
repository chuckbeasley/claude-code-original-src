namespace ClaudeCode.Services.ToolUseSummary;

using System.Collections.Concurrent;

/// <summary>
/// Aggregated usage statistics for a single tool across the current session.
/// </summary>
/// <param name="ToolName">The canonical tool name.</param>
/// <param name="CallCount">Total number of invocations recorded.</param>
/// <param name="ErrorCount">Number of invocations that resulted in an error.</param>
/// <param name="AvgDurationMs">Mean execution time in milliseconds.</param>
public sealed record ToolUseStat(
    string ToolName,
    int CallCount,
    int ErrorCount,
    double AvgDurationMs);

/// <summary>
/// Tracks and aggregates tool usage statistics across the current REPL session.
/// Thread-safe: all operations use <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// and <see cref="System.Threading.Interlocked"/> primitives.
/// </summary>
public sealed class ToolUseSummaryService
{
    // Mutable accumulator stored alongside each tool entry.
    private sealed class Accumulator
    {
        public int CallCount;
        public int ErrorCount;
        public long TotalDurationMs;
    }

    private readonly ConcurrentDictionary<string, Accumulator> _stats =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Records a single tool invocation.
    /// </summary>
    /// <param name="toolName">The canonical tool name. Must not be null or whitespace.</param>
    /// <param name="isError"><see langword="true"/> when the invocation resulted in an error.</param>
    /// <param name="durationMs">Wall-clock execution time in milliseconds.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="toolName"/> is null or whitespace.</exception>
    public void RecordToolUse(string toolName, bool isError, long durationMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var acc = _stats.GetOrAdd(toolName, _ => new Accumulator());
        System.Threading.Interlocked.Increment(ref acc.CallCount);
        if (isError)
            System.Threading.Interlocked.Increment(ref acc.ErrorCount);
        System.Threading.Interlocked.Add(ref acc.TotalDurationMs, durationMs);
    }

    /// <summary>
    /// Returns a snapshot of all recorded statistics, sorted by call count descending.
    /// </summary>
    public IReadOnlyList<ToolUseStat> GetSummary()
    {
        return _stats
            .Select(kv => new ToolUseStat(
                ToolName: kv.Key,
                CallCount: kv.Value.CallCount,
                ErrorCount: kv.Value.ErrorCount,
                AvgDurationMs: kv.Value.CallCount > 0
                    ? (double)kv.Value.TotalDurationMs / kv.Value.CallCount
                    : 0.0))
            .OrderByDescending(s => s.CallCount)
            .ToList();
    }

    /// <summary>
    /// Returns a Markdown-formatted table of tool usage statistics.
    /// Returns an empty string when no tools have been recorded.
    /// Format: "| Tool | Calls | Errors | Avg ms |\n|---|---..."
    /// </summary>
    public string BuildReport()
    {
        var stats = GetSummary();
        if (stats.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("| Tool | Calls | Errors | Avg ms |");
        sb.AppendLine("|------|------:|-------:|-------:|");

        foreach (var s in stats)
            sb.AppendLine($"| {s.ToolName} | {s.CallCount} | {s.ErrorCount} | {s.AvgDurationMs:F0} |");

        return sb.ToString();
    }
}
