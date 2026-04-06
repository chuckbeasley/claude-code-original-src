namespace ClaudeCode.Services.Compact;

/// <summary>
/// Compacts large tool results inline before they are added to the message history.
/// Reduces context bloat from verbose tool outputs (e.g., grep results with 200 lines).
/// </summary>
public sealed class MicroCompactService
{
    /// <summary>
    /// Character length above which a tool result is considered large and subject to truncation.
    /// </summary>
    private const int LargeResultThreshold = 3000;

    /// <summary>
    /// If <paramref name="toolResult"/> exceeds <see cref="LargeResultThreshold"/> characters,
    /// truncates it intelligently by keeping the first 40% and last 20% of the original length,
    /// inserting a "[... N chars omitted ...]" notice between the two retained segments.
    /// Otherwise returns <paramref name="toolResult"/> unchanged.
    /// </summary>
    /// <param name="toolResult">The raw tool output string. Must not be <see langword="null"/>.</param>
    /// <param name="toolName">The tool's canonical name, used only for diagnostic context.</param>
    /// <returns>
    /// The original string when it is within the threshold, or a compacted replacement string.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="toolResult"/> is <see langword="null"/>.</exception>
    public string MaybeCompact(string toolResult, string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolResult);

        if (toolResult.Length <= LargeResultThreshold)
            return toolResult;

        int keepHead = (int)(LargeResultThreshold * 0.4);
        int keepTail = (int)(LargeResultThreshold * 0.2);
        int omitted = toolResult.Length - keepHead - keepTail;

        return toolResult[..keepHead]
            + $"\n[... {omitted:N0} chars omitted by micro-compact ({toolName}) ...]\n"
            + toolResult[^keepTail..];
    }
}
