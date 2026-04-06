namespace ClaudeCode.Services.Api;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// IAnthropicClient implementation for Azure AI Foundry / Azure OpenAI compatible endpoints.
/// Supports both API key (<c>api-key</c> header) and AAD Bearer token authentication.
/// </summary>
public sealed class AzureAnthropicClient : IAnthropicClient
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string? _apiKey;
    private readonly string? _bearerToken;
    private readonly string _apiVersion;

    private const string DefaultApiVersion = "2024-12-01-preview";
    private const string AnthropicVersion  = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc/>
    public RateLimitInfo? LastRateLimitInfo => null;

    /// <summary>
    /// Initializes a new instance of <see cref="AzureAnthropicClient"/>.
    /// </summary>
    /// <param name="httpClient">The <see cref="HttpClient"/> to use for requests.</param>
    /// <param name="endpoint">
    /// Full Azure endpoint URL, e.g. <c>https://my-resource.openai.azure.com</c>.
    /// </param>
    /// <param name="apiKey">
    /// Optional API key sent as the <c>api-key</c> request header.
    /// </param>
    /// <param name="bearerToken">
    /// Optional AAD Bearer token sent as the <c>Authorization: Bearer</c> header.
    /// </param>
    /// <param name="apiVersion">
    /// Optional Azure API version query parameter; defaults to <c>2024-12-01-preview</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="httpClient"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="endpoint"/> is null or whitespace.
    /// </exception>
    public AzureAnthropicClient(
        HttpClient httpClient,
        string endpoint,
        string? apiKey      = null,
        string? bearerToken = null,
        string? apiVersion  = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        _httpClient  = httpClient;
        _endpoint    = endpoint;
        _apiKey      = apiKey;
        _bearerToken = bearerToken;
        _apiVersion  = apiVersion ?? DefaultApiVersion;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SseEvent> StreamMessageAsync(
        MessageRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // SendRequestAsync handles all HTTP errors before the first yield.
        using var response = await SendRequestAsync(request, ct).ConfigureAwait(false);

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
    /// Builds and sends the HTTP request to the Azure endpoint.
    /// Converts <see cref="HttpRequestException"/> and non-2xx responses to
    /// <see cref="AnthropicApiException"/> so the caller never sees raw transport errors.
    /// </summary>
    /// <exception cref="AnthropicApiException">
    /// Thrown on network failure or a non-2xx HTTP response.
    /// </exception>
    private async Task<HttpResponseMessage> SendRequestAsync(
        MessageRequest request,
        CancellationToken ct)
    {
        var url = $"{_endpoint.TrimEnd('/')}/openai/deployments/messages?api-version={_apiVersion}";

        var body = JsonSerializer.Serialize(
            new
            {
                anthropic_version = AnthropicVersion,
                stream            = true,
                model             = request.Model,
                max_tokens        = request.MaxTokens,
                messages          = request.Messages,
                system            = request.System,
                tools             = request.Tools,
                tool_choice       = request.ToolChoice,
                temperature       = request.Temperature,
                thinking          = request.Thinking,
                metadata          = request.Metadata,
            },
            JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (_apiKey is not null)
            httpRequest.Headers.TryAddWithoutValidation("api-key", _apiKey);

        if (_bearerToken is not null)
            httpRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _bearerToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AnthropicApiException(0, $"Azure request failed: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new AnthropicApiException((int)response.StatusCode, err);
            }
        }

        return response;
    }
}
