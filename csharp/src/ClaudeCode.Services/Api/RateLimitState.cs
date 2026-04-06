namespace ClaudeCode.Services.Api;

/// <summary>
/// Parsed rate limit state from Anthropic API response headers.
/// </summary>
public sealed record RateLimitState(
    int? RequestsLimit,
    int? RequestsRemaining,
    int? TokensLimit,
    int? TokensRemaining,
    DateTimeOffset? ResetAt,
    bool IsSubscriber,
    string? ModelName);
