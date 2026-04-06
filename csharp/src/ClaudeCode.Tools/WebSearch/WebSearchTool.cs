namespace ClaudeCode.Tools.WebSearch;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

/// <summary>Input parameters for the <see cref="WebSearchTool"/>.</summary>
public record WebSearchInput
{
    /// <summary>The search query string.</summary>
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    /// <summary>Optional maximum number of results to return.</summary>
    [JsonPropertyName("max_results")]
    public int? MaxResults { get; init; }
}

/// <summary>Output produced by the <see cref="WebSearchTool"/>.</summary>
public record WebSearchOutput(string Results);

/// <summary>
/// Placeholder web search tool. Requires an external API key (e.g. Brave Search API)
/// to return real results. Returns a configuration message until that key is supplied.
/// </summary>
public sealed class WebSearchTool : Tool<WebSearchInput, WebSearchOutput>
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "The search query" },
            max_results = new { type = "integer", description = "Maximum number of results" },
        },
        required = new[] { "query" },
    });

    /// <inheritdoc/>
    public override string Name => "WebSearch";

    /// <inheritdoc/>
    public override string? SearchHint => "search the web";

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Search the web");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult("Searches the web for information.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "WebSearch";

    /// <inheritdoc/>
    public override WebSearchInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<WebSearchInput>(json.GetRawText(), JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize WebSearchInput");

    private static readonly HttpClient SearchClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <inheritdoc/>
    public override async Task<ToolResult<WebSearchOutput>> ExecuteAsync(
        WebSearchInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(input.Query))
            return new ToolResult<WebSearchOutput>
            {
                Data = new WebSearchOutput("Search query cannot be empty."),
            };

        var apiKey = Environment.GetEnvironmentVariable("BRAVE_SEARCH_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return new ToolResult<WebSearchOutput>
            {
                Data = new WebSearchOutput("Web search requires BRAVE_SEARCH_API_KEY environment variable."),
            };

        try
        {
            var results = await SearchBraveAsync(input.Query, input.MaxResults ?? 5, apiKey, ct)
                .ConfigureAwait(false);
            return new ToolResult<WebSearchOutput> { Data = new WebSearchOutput(results) };
        }
        catch (Exception ex)
        {
            return new ToolResult<WebSearchOutput>
            {
                Data = new WebSearchOutput($"Web search failed: {ex.Message}"),
            };
        }
    }

    private static async Task<string> SearchBraveAsync(
        string query, int maxResults, string apiKey, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var url = $"https://api.search.brave.com/res/v1/web/search?q={encoded}&count={maxResults}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Accept", "application/json");
        req.Headers.Add("Accept-Encoding", "gzip");
        req.Headers.Add("X-Subscription-Token", apiKey);

        using var resp = await SearchClient.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var preview = json.Length > 500 ? json[..500] : json;
            return $"Brave Search error ({(int)resp.StatusCode}): {preview}";
        }

        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();

        if (doc.RootElement.TryGetProperty("web", out var web) &&
            web.TryGetProperty("results", out var results))
        {
            int count = 0;
            foreach (var result in results.EnumerateArray())
            {
                if (count >= maxResults) break;
                var title = result.TryGetProperty("title", out var t) ? t.GetString() : "";
                var resultUrl = result.TryGetProperty("url", out var u) ? u.GetString() : "";
                var desc = result.TryGetProperty("description", out var d) ? d.GetString() : "";
                sb.AppendLine($"[{count + 1}] {title}");
                sb.AppendLine($"URL: {resultUrl}");
                if (!string.IsNullOrEmpty(desc))
                    sb.AppendLine(desc);
                sb.AppendLine();
                count++;
            }
        }

        return sb.Length > 0 ? sb.ToString().TrimEnd() : "No results found.";
    }

    /// <inheritdoc/>
    public override string MapResultToString(WebSearchOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Results;
    }
}
