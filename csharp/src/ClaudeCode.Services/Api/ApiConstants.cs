namespace ClaudeCode.Services.Api;

/// <summary>
/// Compile-time and initialisation-time constants for the Anthropic Messages API client.
/// </summary>
public static class ApiConstants
{
    /// <summary>Default base URL for the Anthropic API.</summary>
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>Path for the Messages endpoint.</summary>
    public const string MessagesEndpoint = "/v1/messages";

    /// <summary>Default HTTP timeout in milliseconds (10 minutes).</summary>
    public const int DefaultTimeoutMs = 600_000;

    /// <summary>Maximum number of retries for transient failures (e.g. 529, 503, 429).</summary>
    public const int DefaultMaxRetries = 10;

    /// <summary>Base delay in milliseconds before the first retry.</summary>
    public const int BaseDelayMs = 500;

    /// <summary>Maximum number of retries specifically for HTTP 529 (overloaded) responses.</summary>
    public const int Max529Retries = 3;

    /// <summary>Upper bound on exponential back-off delay in milliseconds.</summary>
    public const int MaxBackoffMs = 32_000;

    /// <summary>Model used when no explicit model override is provided.</summary>
    public const string DefaultModel = "claude-sonnet-4-6";

    /// <summary>
    /// Anthropic beta feature flags sent on every request via the
    /// <c>anthropic-beta</c> header.
    /// </summary>
    public static readonly string[] DefaultBetas =
    [
        "interleaved-thinking-2025-05-14",
        "token-efficient-tools-2026-03-28",
    ];

    /// <summary>
    /// Per-model pricing expressed in USD per one million tokens.
    /// Keyed by model identifier (case-insensitive).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ModelPricing> ModelPricing =
        new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-haiku-4-5-20251001"]    = new(InputPerMillion: 1.0,  OutputPerMillion: 5.0,  CacheReadPerMillion: 0.1,  CacheWritePerMillion: 1.25),
            ["claude-3-5-haiku-20241022"]    = new(InputPerMillion: 0.8,  OutputPerMillion: 4.0,  CacheReadPerMillion: 0.08, CacheWritePerMillion: 1.0),
            ["claude-sonnet-4-6"]            = new(InputPerMillion: 3.0,  OutputPerMillion: 15.0, CacheReadPerMillion: 0.3,  CacheWritePerMillion: 3.75),
            ["claude-sonnet-4-5-20250929"]   = new(InputPerMillion: 3.0,  OutputPerMillion: 15.0, CacheReadPerMillion: 0.3,  CacheWritePerMillion: 3.75),
            ["claude-sonnet-4-20250514"]     = new(InputPerMillion: 3.0,  OutputPerMillion: 15.0, CacheReadPerMillion: 0.3,  CacheWritePerMillion: 3.75),
            ["claude-3-7-sonnet-20250219"]   = new(InputPerMillion: 3.0,  OutputPerMillion: 15.0, CacheReadPerMillion: 0.3,  CacheWritePerMillion: 3.75),
            ["claude-3-5-sonnet-20241022"]   = new(InputPerMillion: 3.0,  OutputPerMillion: 15.0, CacheReadPerMillion: 0.3,  CacheWritePerMillion: 3.75),
            ["claude-opus-4-6"]              = new(InputPerMillion: 15.0, OutputPerMillion: 75.0, CacheReadPerMillion: 1.5,  CacheWritePerMillion: 18.75),
            ["claude-opus-4-5-20251101"]     = new(InputPerMillion: 5.0,  OutputPerMillion: 25.0, CacheReadPerMillion: 0.5,  CacheWritePerMillion: 6.25),
            ["claude-opus-4-1-20250805"]     = new(InputPerMillion: 15.0, OutputPerMillion: 75.0, CacheReadPerMillion: 1.5,  CacheWritePerMillion: 18.75),
            ["claude-opus-4-20250514"]       = new(InputPerMillion: 15.0, OutputPerMillion: 75.0, CacheReadPerMillion: 1.5,  CacheWritePerMillion: 18.75),
        };
}

/// <summary>
/// Pricing rates for a single model, expressed in USD per one million tokens.
/// </summary>
/// <param name="InputPerMillion">Cost per million input (prompt) tokens.</param>
/// <param name="OutputPerMillion">Cost per million output (completion) tokens.</param>
/// <param name="CacheReadPerMillion">Cost per million tokens read from the prompt cache.</param>
/// <param name="CacheWritePerMillion">Cost per million tokens written into the prompt cache.</param>
public record ModelPricing(
    double InputPerMillion,
    double OutputPerMillion,
    double CacheReadPerMillion,
    double CacheWritePerMillion);
