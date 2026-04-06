namespace ClaudeCode.Services.Api;

/// <summary>
/// Generates user-facing rate limit warning and error messages.
/// </summary>
public static class RateLimitMessages
{
    /// <summary>Returns a warning message when requests are running low (below 10% of limit).</summary>
    /// <param name="state">Rate limit state parsed from the most recent API response headers, or null.</param>
    /// <returns>
    /// A human-readable warning string when <see cref="RateLimitState.RequestsRemaining"/> is at or
    /// below 10% of <see cref="RateLimitState.RequestsLimit"/>; otherwise null.
    /// </returns>
    public static string? GetWarningMessage(RateLimitState? state)
    {
        if (state is null) return null;
        if (state.RequestsLimit is null || state.RequestsRemaining is null) return null;

        if (state.RequestsRemaining.Value <= state.RequestsLimit.Value * 0.1)
        {
            return $"API rate limit warning: {state.RequestsRemaining}/{state.RequestsLimit} requests remaining. "
                 + $"Resets at {state.ResetAt?.UtcDateTime:HH:mm:ss}.";
        }

        return null;
    }

    /// <summary>Returns an error message when the rate limit has been hit (requests exhausted).</summary>
    /// <param name="state">Rate limit state parsed from the most recent API response headers, or null.</param>
    /// <returns>
    /// A human-readable error string when <see cref="RateLimitState.RequestsRemaining"/> is zero;
    /// otherwise null.
    /// </returns>
    public static string? GetErrorMessage(RateLimitState? state)
    {
        if (state is null) return null;
        if (state.RequestsRemaining is null) return null;

        if (state.RequestsRemaining.Value == 0)
        {
            return $"API rate limit reached. {state.RequestsLimit} requests/window allowed. "
                 + $"Resets at {state.ResetAt?.UtcDateTime:HH:mm:ss} UTC.";
        }

        return null;
    }
}
