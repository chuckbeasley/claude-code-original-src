namespace ClaudeCode.Services.Api;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// Defines the contract for a client that communicates with the Anthropic Messages API.
/// </summary>
public interface IAnthropicClient
{
    /// <summary>
    /// Sends a message request to the Anthropic API and streams the SSE response events.
    /// </summary>
    /// <param name="request">The message request to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of parsed SSE events.</returns>
    IAsyncEnumerable<SseEvent> StreamMessageAsync(
        MessageRequest request,
        CancellationToken ct = default);

    /// <summary>Rate-limit info from the most recent API response, or null.</summary>
    RateLimitInfo? LastRateLimitInfo { get; }
}

/// <summary>
/// HTTP client for the Anthropic Messages API. Sends requests and streams raw SSE events.
/// </summary>
public sealed class AnthropicClient : IAnthropicClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string? _sessionId;
    private RateLimitInfo? _lastRateLimitInfo;

    /// <summary>Rate-limit headers from the most recent API response, or null.</summary>
    public RateLimitInfo? LastRateLimitInfo => _lastRateLimitInfo;

    /// <summary>Fired after each API response with updated rate limit information.</summary>
    public event Action<RateLimitState>? RateLimitUpdated;

    /// <summary>The most recently observed rate limit state from API responses.</summary>
    public RateLimitState? CurrentRateLimit { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Initializes a new instance of <see cref="AnthropicClient"/>.
    /// </summary>
    /// <param name="httpClient">The <see cref="HttpClient"/> to use for requests.</param>
    /// <param name="apiKey">The Anthropic API key.</param>
    /// <param name="baseUrl">Optional base URL override; defaults to <see cref="ApiConstants.DefaultBaseUrl"/>.</param>
    /// <param name="sessionId">Optional Claude Code session ID to attach to requests.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="httpClient"/> or <paramref name="apiKey"/> is null.</exception>
    public AnthropicClient(HttpClient httpClient, string apiKey, string? baseUrl = null, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _apiKey = apiKey ?? "";
        _baseUrl = baseUrl ?? ApiConstants.DefaultBaseUrl;
        _sessionId = sessionId;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SseEvent> StreamMessageAsync(
        MessageRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = $"{_baseUrl.TrimEnd('/')}{ApiConstants.MessagesEndpoint}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);

        // Authentication and protocol headers
        httpRequest.Headers.Add("x-api-key", _apiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Headers.Add("x-app", "cli");

        if (_sessionId is not null)
            httpRequest.Headers.Add("X-Claude-Code-Session-Id", _sessionId);

        httpRequest.Headers.Add("x-client-request-id", Guid.NewGuid().ToString());

        // Beta feature headers
        if (ApiConstants.DefaultBetas.Length > 0)
            httpRequest.Headers.Add("anthropic-beta", string.Join(",", ApiConstants.DefaultBetas));

        // Serialize body with stream flag forced true
        var streamRequest = request with { Stream = true };
        var json = JsonSerializer.Serialize(streamRequest, JsonOptions);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        // Send with streaming — only read headers initially
        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new AnthropicApiException(
                (int)response.StatusCode,
                errorBody,
                GetRetryAfter(response),
                GetShouldRetry(response));
        }

        // Extract and cache rate limit headers.
        _lastRateLimitInfo = ParseRateLimitHeaders(response);
        ParseUnifiedRateLimitHeaders(response);

        // Read and parse the SSE stream line by line
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? currentEvent = null;
        var dataBuilder = new StringBuilder();

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                currentEvent = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                dataBuilder.Append(line[6..]);
            }
            else if (line.Length == 0 && currentEvent is not null)
            {
                // Empty line signals end of this SSE event
                yield return new SseEvent
                {
                    EventType = currentEvent,
                    Data = dataBuilder.ToString(),
                };

                currentEvent = null;
                dataBuilder.Clear();
            }
        }
    }

    private static RateLimitInfo ParseRateLimitHeaders(HttpResponseMessage response)
    {
        static int? TryGetInt(HttpResponseMessage r, string name)
        {
            if (r.Headers.TryGetValues(name, out var vals))
                if (int.TryParse(vals.FirstOrDefault(), out var n)) return n;
            return null;
        }

        return new RateLimitInfo(
            RequestsLimit:        TryGetInt(response, "anthropic-ratelimit-requests-limit"),
            RequestsRemaining:    TryGetInt(response, "anthropic-ratelimit-requests-remaining"),
            RequestsResetSeconds: TryGetInt(response, "anthropic-ratelimit-requests-reset"),
            TokensLimit:          TryGetInt(response, "anthropic-ratelimit-tokens-limit"),
            TokensRemaining:      TryGetInt(response, "anthropic-ratelimit-tokens-remaining"),
            TokensResetSeconds:   TryGetInt(response, "anthropic-ratelimit-tokens-reset"),
            InputTokensLimit:     TryGetInt(response, "anthropic-ratelimit-input-tokens-limit"),
            InputTokensRemaining: TryGetInt(response, "anthropic-ratelimit-input-tokens-remaining"));
    }

    /// <summary>
    /// Parses unified rate limit headers (<c>anthropic-ratelimit-unified-*</c>), updates
    /// <see cref="CurrentRateLimit"/>, and fires <see cref="RateLimitUpdated"/>.
    /// No-ops if no unified headers are present.
    /// </summary>
    private void ParseUnifiedRateLimitHeaders(HttpResponseMessage response)
    {
        static int? ParseInt(HttpResponseMessage r, string name) =>
            r.Headers.TryGetValues(name, out var vals) && int.TryParse(vals.FirstOrDefault(), out var v) ? v : null;

        var requestsLimit     = ParseInt(response, "anthropic-ratelimit-unified-requests-limit");
        var requestsRemaining = ParseInt(response, "anthropic-ratelimit-unified-requests-remaining");
        var tokensLimit       = ParseInt(response, "anthropic-ratelimit-unified-tokens-limit");
        var tokensRemaining   = ParseInt(response, "anthropic-ratelimit-unified-tokens-remaining");

        DateTimeOffset? resetAt = null;
        if (response.Headers.TryGetValues("anthropic-ratelimit-unified-reset", out var resetVals)
            && DateTimeOffset.TryParse(resetVals.FirstOrDefault(), out var parsed))
            resetAt = parsed;

        // Only fire if we got at least some unified data.
        if (requestsLimit is null && tokensLimit is null) return;

        var state = new RateLimitState(
            RequestsLimit:     requestsLimit,
            RequestsRemaining: requestsRemaining,
            TokensLimit:       tokensLimit,
            TokensRemaining:   tokensRemaining,
            ResetAt:           resetAt,
            IsSubscriber:      false,
            ModelName:         null);

        CurrentRateLimit = state;
        RateLimitUpdated?.Invoke(state);
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after", out var values))
        {
            var val = values.FirstOrDefault();
            if (val is not null && int.TryParse(val, out var seconds))
                return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    private static bool? GetShouldRetry(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-should-retry", out var values))
        {
            var val = values.FirstOrDefault();
            if (val is "true") return true;
            if (val is "false") return false;
        }

        return null;
    }
}
