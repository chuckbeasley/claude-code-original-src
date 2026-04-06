namespace ClaudeCode.Services.Api;

/// <summary>
/// Exception thrown when the Anthropic API returns a non-success response.
/// </summary>
public sealed class AnthropicApiException : Exception
{
    /// <summary>Gets the HTTP status code returned by the API.</summary>
    public int StatusCode { get; }

    /// <summary>Gets the raw response body from the API.</summary>
    public string ResponseBody { get; }

    /// <summary>Gets the server-requested retry delay, if provided.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Gets the server hint for whether this request should be retried, if provided.</summary>
    public bool? ShouldRetry { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="AnthropicApiException"/>.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="responseBody">The raw response body.</param>
    /// <param name="retryAfter">Optional server-specified retry delay.</param>
    /// <param name="shouldRetry">Optional server hint for retryability.</param>
    public AnthropicApiException(int statusCode, string responseBody, TimeSpan? retryAfter = null, bool? shouldRetry = null)
        : base($"Anthropic API error {statusCode}: {TruncateBody(responseBody)}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        RetryAfter = retryAfter;
        ShouldRetry = shouldRetry;
    }

    /// <summary>
    /// Gets whether this error is retryable based on the status code and server hint.
    /// </summary>
    public bool IsRetryable => StatusCode switch
    {
        408 or 409 or 429 or 529 => true,
        >= 500 => true,
        _ => ShouldRetry ?? false,
    };

    /// <summary>
    /// Gets whether this error indicates the API is overloaded (HTTP 529 or overloaded_error body).
    /// </summary>
    public bool IsOverloaded => StatusCode == 529
        || ResponseBody.Contains("overloaded_error", StringComparison.OrdinalIgnoreCase);

    private static string TruncateBody(string body)
        => body.Length > 200 ? body[..200] + "..." : body;
}
