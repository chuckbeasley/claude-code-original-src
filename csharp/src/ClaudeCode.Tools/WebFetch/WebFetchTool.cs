namespace ClaudeCode.Tools.WebFetch;

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

/// <summary>Input parameters for the <see cref="WebFetchTool"/>.</summary>
public record WebFetchInput
{
    /// <summary>The URL to fetch.</summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>Optional maximum character length for the returned content.</summary>
    [JsonPropertyName("max_length")]
    public int? MaxLength { get; init; }
}

/// <summary>Output produced by the <see cref="WebFetchTool"/>.</summary>
public record WebFetchOutput(string Content, string Url, int StatusCode, string? ContentType);

/// <summary>
/// Tool that fetches the content of an HTTP or HTTPS URL and returns it as text.
/// </summary>
public sealed class WebFetchTool : Tool<WebFetchInput, WebFetchOutput>
{
    /// <summary>
    /// Shared <see cref="HttpClient"/> instance. Initialised once per process; never disposed.
    /// The 30-second timeout covers slow servers without blocking the session indefinitely.
    /// </summary>
    private static readonly HttpClient SharedClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent", "ClaudeCode/0.1");
        return client;
    }

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            url = new { type = "string", description = "The URL to fetch" },
            max_length = new { type = "integer", description = "Max response length in characters" },
        },
        required = new[] { "url" },
    });

    /// <inheritdoc/>
    public override string Name => "WebFetch";

    /// <inheritdoc/>
    public override string? SearchHint => "fetch URL content";

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Fetch content from a URL");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult("Fetches the content of a URL and returns it as text.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "WebFetch";

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    /// <inheritdoc/>
    public override WebFetchInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<WebFetchInput>(json.GetRawText(), JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize WebFetchInput");

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        WebFetchInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var uri))
            return Task.FromResult(ValidationResult.Failure($"Invalid URL: {input.Url}"));

        if (uri.Scheme is not ("http" or "https"))
            return Task.FromResult(ValidationResult.Failure("Only http and https URLs are supported."));

        return Task.FromResult(ValidationResult.Success);
    }

    /// <inheritdoc/>
    public override async Task<ToolResult<WebFetchOutput>> ExecuteAsync(
        WebFetchInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        using var response = await SharedClient.GetAsync(input.Url, ct).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType;

        const int DefaultMaxLength = 50_000;
        var maxLen = input.MaxLength ?? DefaultMaxLength;
        if (content.Length > maxLen)
            content = content[..maxLen] + $"\n\n... [truncated, {content.Length - maxLen} chars remaining]";

        return new ToolResult<WebFetchOutput>
        {
            Data = new WebFetchOutput(content, input.Url, (int)response.StatusCode, contentType),
        };
    }

    /// <inheritdoc/>
    public override string MapResultToString(WebFetchOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        var header = $"URL: {result.Url}\nStatus: {result.StatusCode}\nContent-Type: {result.ContentType ?? "unknown"}\n\n";
        return header + result.Content;
    }
}
