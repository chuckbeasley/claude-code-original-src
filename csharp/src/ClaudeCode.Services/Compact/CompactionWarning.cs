namespace ClaudeCode.Services.Compact;

/// <summary>
/// Emits a single console warning when context usage crosses the warning threshold.
/// The warning is suppressed after the first display until <see cref="Reset"/> is called.
/// </summary>
public class CompactionWarning
{
    private bool _warned = false;
    private const double WarnThresholdFraction = 0.75; // warn at 75%

    /// <summary>
    /// Displays a context-usage warning if the threshold has been crossed and no warning
    /// has been shown yet in this session segment.
    /// </summary>
    /// <param name="usedTokens">Estimated tokens currently in use.</param>
    /// <param name="contextLimit">Total context window size for the model.</param>
    public void MaybeWarn(int usedTokens, int contextLimit)
    {
        if (_warned) return;
        var fraction = (double)usedTokens / contextLimit;
        if (fraction >= WarnThresholdFraction)
        {
            _warned = true;
            var pct = (int)(fraction * 100);
            Spectre.Console.AnsiConsole.MarkupLine(
                $"[yellow]Context at {pct}% ({usedTokens:N0}/{contextLimit:N0} tokens).[/] " +
                $"Run [blue]/compact[/] to summarize and free space.");
        }
    }

    /// <summary>
    /// Resets the warning state so the next threshold crossing will display the warning again.
    /// Call after a successful compaction.
    /// </summary>
    public void Reset() { _warned = false; }
}
