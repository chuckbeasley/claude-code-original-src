namespace ClaudeCode.Services.Compact;

/// <summary>
/// Automatically triggers compaction when context approaches the model's limit.
/// Uses a circuit breaker to prevent compaction loops.
/// </summary>
public class AutoCompactService
{
    private int _consecutiveFailures = 0;
    private DateTimeOffset _lastCompactionTime = DateTimeOffset.MinValue;
    private const int MaxConsecutiveFailures = 3;
    private const double TriggerThresholdFraction = 0.85; // compact at 85% context usage

    /// <summary>
    /// Returns <see langword="true"/> when compaction should be triggered based on current token usage
    /// and the circuit breaker state.
    /// </summary>
    /// <param name="usedTokens">Estimated tokens currently in use.</param>
    /// <param name="contextLimit">Total context window size for the model.</param>
    public bool ShouldCompact(int usedTokens, int contextLimit)
    {
        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            // Circuit breaker open — don't compact until cooldown
            if ((DateTimeOffset.UtcNow - _lastCompactionTime).TotalMinutes < 10)
                return false;
            _consecutiveFailures = 0; // reset after cooldown
        }
        return (double)usedTokens / contextLimit >= TriggerThresholdFraction;
    }

    /// <summary>
    /// Records the outcome of a compaction attempt.
    /// Resets the failure counter on success; increments it on failure.
    /// </summary>
    /// <param name="success"><see langword="true"/> when compaction succeeded.</param>
    public void RecordCompactionAttempt(bool success)
    {
        _lastCompactionTime = DateTimeOffset.UtcNow;
        if (success) _consecutiveFailures = 0;
        else _consecutiveFailures++;
    }
}
