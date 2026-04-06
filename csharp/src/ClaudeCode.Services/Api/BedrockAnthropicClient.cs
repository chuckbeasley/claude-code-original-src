namespace ClaudeCode.Services.Api;

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Anthropic API client for AWS Bedrock using SigV4 request signing.
/// Translates the standard Anthropic Messages format to the Bedrock
/// invoke-with-response-stream endpoint format.
/// </summary>
public sealed class BedrockAnthropicClient : IAnthropicClient
{
    private readonly HttpClient _httpClient;
    private readonly string _region;
    private readonly string _accessKeyId;
    private readonly string _secretAccessKey;
    private readonly string? _sessionToken;
    private readonly string? _sessionId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc/>
    public RateLimitInfo? LastRateLimitInfo { get; private set; }

    /// <summary>
    /// Initializes a new instance of <see cref="BedrockAnthropicClient"/>.
    /// </summary>
    /// <param name="httpClient">The <see cref="HttpClient"/> to use for requests.</param>
    /// <param name="region">AWS region, e.g. "us-east-1".</param>
    /// <param name="accessKeyId">AWS access key ID.</param>
    /// <param name="secretAccessKey">AWS secret access key.</param>
    /// <param name="sessionToken">Optional STS session token for temporary credentials.</param>
    /// <param name="sessionId">Optional Claude Code session ID to attach to requests.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="httpClient"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if any required string parameter is null or whitespace.</exception>
    public BedrockAnthropicClient(
        HttpClient httpClient,
        string region,
        string accessKeyId,
        string secretAccessKey,
        string? sessionToken = null,
        string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretAccessKey);

        _httpClient = httpClient;
        _region = region;
        _accessKeyId = accessKeyId;
        _secretAccessKey = secretAccessKey;
        _sessionToken = sessionToken;
        _sessionId = sessionId;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SseEvent> StreamMessageAsync(
        MessageRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Map Anthropic model ID to Bedrock model ID.
        var bedrockModelId = MapModelId(request.Model);
        var endpoint = $"https://bedrock-runtime.{_region}.amazonaws.com/model/{Uri.EscapeDataString(bedrockModelId)}/invoke-with-response-stream";

        var body = JsonSerializer.Serialize(request, JsonOptions);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        var now       = DateTime.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd");
        var amzDate   = now.ToString("yyyyMMddTHHmmssZ");

        // Build the canonical header map used for both SigV4 signing and request population.
        // host and content-type must be included in the signed headers but are handled
        // specially when adding to the HttpRequestMessage (see below).
        var uri = new Uri(endpoint);
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["content-type"]         = "application/json",
            ["host"]                 = uri.Host,
            ["x-amz-content-sha256"] = HexEncode(SHA256.HashData(bodyBytes)),
            ["x-amz-date"]           = amzDate,
        };
        if (_sessionToken is not null)
            headers["x-amz-security-token"] = _sessionToken;

        var authHeader = BuildSigV4Auth(
            method:    "POST",
            uri:       uri,
            headers:   headers,
            payload:   bodyBytes,
            service:   "bedrock",
            region:    _region,
            accessKey: _accessKeyId,
            secretKey: _secretAccessKey,
            dateStamp: dateStamp,
            amzDate:   amzDate);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);

        // Set content with the correct media type on Content.Headers (not Request.Headers).
        httpRequest.Content = new ByteArrayContent(bodyBytes);
        httpRequest.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        // Add signed headers to the request, skipping content-type (on Content.Headers above)
        // and host (managed automatically by HttpClient to avoid duplicate/override issues).
        foreach (var (k, v) in headers)
        {
            if (k is "content-type" or "host") continue;
            httpRequest.Headers.TryAddWithoutValidation(k, v);
        }
        httpRequest.Headers.TryAddWithoutValidation("Authorization", authHeader);

        if (_sessionId is not null)
            httpRequest.Headers.TryAddWithoutValidation("x-claude-code-session-id", _sessionId);

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Bedrock error {(int)response.StatusCode}: {errorBody}", null, response.StatusCode);
        }

        // Bedrock returns a binary-framed event stream; for the invoke-with-response-stream
        // endpoint the response body carries SSE-formatted data when content-type is application/json.
        await foreach (var evt in ParseSseStreamAsync(response, ct).ConfigureAwait(false))
            yield return evt;
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Maps a canonical Anthropic model ID to the corresponding Bedrock cross-region
    /// inference profile ID. Returns the input unchanged if no match is found,
    /// allowing callers who already have a Bedrock model ID to pass it through directly.
    /// </summary>
    private static string MapModelId(string anthropicModelId)
    {
        return anthropicModelId switch
        {
            var m when m.Contains("claude-opus-4")     => "us.anthropic.claude-opus-4-5-20251101-v1:0",
            var m when m.Contains("claude-sonnet-4")   => "us.anthropic.claude-sonnet-4-5-20251101-v1:0",
            var m when m.Contains("claude-haiku-4")    => "us.anthropic.claude-haiku-4-5-20251001-v1:0",
            var m when m.Contains("claude-3-5-sonnet") => "anthropic.claude-3-5-sonnet-20241022-v2:0",
            var m when m.Contains("claude-3-5-haiku")  => "anthropic.claude-3-5-haiku-20241022-v1:0",
            var m when m.Contains("claude-3-opus")     => "anthropic.claude-3-opus-20240229-v1:0",
            _ => anthropicModelId, // pass through if already a Bedrock ID
        };
    }

    /// <summary>
    /// Reads an SSE stream line-by-line and yields one <see cref="SseEvent"/> per complete
    /// event block (event: + data: + blank separator line).
    /// </summary>
    private static async IAsyncEnumerable<SseEvent> ParseSseStreamAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
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
    /// Builds the AWS SigV4 <c>Authorization</c> header value for a POST request.
    /// </summary>
    private static string BuildSigV4Auth(
        string method, Uri uri, SortedDictionary<string, string> headers,
        byte[] payload, string service, string region,
        string accessKey, string secretKey, string dateStamp, string amzDate)
    {
        var payloadHash = HexEncode(SHA256.HashData(payload));

        // Canonical headers: each entry is "key:value\n", keys already sorted via SortedDictionary.
        var canonicalHeaders     = string.Concat(headers.Select(h => $"{h.Key}:{h.Value}\n"));
        var signedHeaders        = string.Join(";", headers.Keys);

        var canonicalUri         = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        var canonicalQueryString = string.Empty; // no query string for this endpoint
        var canonicalRequest     = string.Join("\n",
            method, canonicalUri, canonicalQueryString,
            canonicalHeaders, signedHeaders, payloadHash);

        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign    = string.Join("\n",
            "AWS4-HMAC-SHA256", amzDate, credentialScope,
            HexEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = GetSigningKey(secretKey, dateStamp, region, service);
        using var hmacFinal = new HMACSHA256(signingKey);
        var signature = HexEncode(hmacFinal.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    /// <summary>
    /// Derives the SigV4 signing key by iterating HMAC-SHA256 over the date, region, service,
    /// and termination string. Each intermediate <see cref="HMACSHA256"/> is disposed promptly.
    /// </summary>
    private static byte[] GetSigningKey(string key, string dateStamp, string region, string service)
    {
        using var hmacDate    = new HMACSHA256(Encoding.UTF8.GetBytes("AWS4" + key));
        var kDate             = hmacDate.ComputeHash(Encoding.UTF8.GetBytes(dateStamp));

        using var hmacRegion  = new HMACSHA256(kDate);
        var kRegion           = hmacRegion.ComputeHash(Encoding.UTF8.GetBytes(region));

        using var hmacService = new HMACSHA256(kRegion);
        var kService          = hmacService.ComputeHash(Encoding.UTF8.GetBytes(service));

        using var hmacFinal   = new HMACSHA256(kService);
        return hmacFinal.ComputeHash(Encoding.UTF8.GetBytes("aws4_request"));
    }

    /// <summary>Returns a lowercase hexadecimal string for the given byte array.</summary>
    private static string HexEncode(byte[] bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();
}
