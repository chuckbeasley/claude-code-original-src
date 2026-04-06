namespace ClaudeCode.Services.Api;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// Anthropic API client for Google Vertex AI.
/// Uses a Bearer token from the <c>VERTEX_ACCESS_TOKEN</c> environment variable,
/// the <c>gcloud auth print-access-token</c> CLI, or a GCP service account as a fallback.
/// </summary>
public sealed class VertexAnthropicClient : IAnthropicClient
{
    private readonly HttpClient _httpClient;
    private readonly string _project;
    private readonly string _region;
    private readonly string _accessToken;
    private readonly string? _sessionId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc/>
    public RateLimitInfo? LastRateLimitInfo { get; private set; }

    /// <summary>
    /// Initializes a new instance of <see cref="VertexAnthropicClient"/>.
    /// </summary>
    /// <param name="httpClient">The <see cref="HttpClient"/> to use for requests.</param>
    /// <param name="project">GCP project ID.</param>
    /// <param name="region">GCP region, e.g. "us-east5".</param>
    /// <param name="accessToken">OAuth 2.0 Bearer access token.</param>
    /// <param name="sessionId">Optional Claude Code session ID to attach to requests.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="httpClient"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if any required string parameter is null or whitespace.</exception>
    public VertexAnthropicClient(
        HttpClient httpClient,
        string project,
        string region,
        string accessToken,
        string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        _httpClient  = httpClient;
        _project     = project;
        _region      = region;
        _accessToken = accessToken;
        _sessionId   = sessionId;
    }

    /// <summary>
    /// Attempts to obtain a GCP OAuth 2.0 access token using the following priority:
    /// <list type="number">
    ///   <item><c>VERTEX_ACCESS_TOKEN</c> environment variable.</item>
    ///   <item><c>gcloud auth print-access-token</c> CLI (if gcloud is installed and authenticated).</item>
    /// </list>
    /// Returns <see langword="null"/> when no token can be obtained.
    /// </summary>
    public static string? ObtainAccessToken()
    {
        // 1. Explicit env var — highest priority, no subprocess needed.
        var token = Environment.GetEnvironmentVariable("VERTEX_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(token)) return token;

        // 2. gcloud CLI — requires gcloud to be installed and authenticated.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("gcloud", "auth print-access-token")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    return output;
            }
        }
        catch { /* gcloud not found or not authenticated — fall through */ }

        return null;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SseEvent> StreamMessageAsync(
        MessageRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var modelId  = request.Model;
        var endpoint = $"https://{_region}-aiplatform.googleapis.com/v1/projects/{Uri.EscapeDataString(_project)}/locations/{Uri.EscapeDataString(_region)}/publishers/anthropic/models/{Uri.EscapeDataString(modelId)}:streamRawPredict";

        // Vertex AI requires the anthropic_version field alongside the standard request body.
        var body = JsonSerializer.Serialize(
            new
            {
                anthropic_version = "vertex-2023-10-16",
                stream            = true,
                max_tokens        = request.MaxTokens,
                messages          = request.Messages,
                system            = request.System,
                tools             = request.Tools,
            },
            JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Content   = new StringContent(body, Encoding.UTF8, "application/json");
        httpRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

        if (_sessionId is not null)
            httpRequest.Headers.TryAddWithoutValidation("x-claude-code-session-id", _sessionId);

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Vertex AI error {(int)response.StatusCode}: {err}", null, response.StatusCode);
        }

        // Cache rate-limit info from response headers before streaming begins.
        LastRateLimitInfo = ParseRateLimitHeaders(response);

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);

        string? currentEvent = null;
        var dataBuilder = new StringBuilder();

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataBuilder.Append(line[5..].Trim());
            }
            else if (line.Length == 0 && dataBuilder.Length > 0)
            {
                yield return new SseEvent
                {
                    EventType = currentEvent ?? "message",
                    Data      = dataBuilder.ToString(),
                };
                currentEvent = null;
                dataBuilder.Clear();
            }
        }
    }

    /// <summary>
    /// Extracts Vertex AI rate-limit headers into a <see cref="RateLimitInfo"/>.
    /// Vertex exposes <c>x-ratelimit-limit-tokens</c> and <c>x-ratelimit-remaining-tokens</c>;
    /// these are mapped to the <c>InputTokens</c> slots of the record for downstream consumers.
    /// </summary>
    private static RateLimitInfo ParseRateLimitHeaders(HttpResponseMessage r)
    {
        static int? Get(HttpResponseMessage resp, string name)
        {
            if (resp.Headers.TryGetValues(name, out var vals)
                && int.TryParse(vals.FirstOrDefault(), out var n))
                return n;
            return null;
        }

        return new RateLimitInfo(
            RequestsLimit:        null,
            RequestsRemaining:    null,
            RequestsResetSeconds: null,
            TokensLimit:          null,
            TokensRemaining:      null,
            TokensResetSeconds:   null,
            InputTokensLimit:     Get(r, "x-ratelimit-limit-tokens"),
            InputTokensRemaining: Get(r, "x-ratelimit-remaining-tokens"));
    }
}
