namespace ClaudeCode.Tools.ToolSearch;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="ToolSearchTool"/>.</summary>
public record ToolSearchInput
{
    /// <summary>The search query matched against tool names, aliases, and search hints.</summary>
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    /// <summary>Maximum number of results to return. Defaults to 5.</summary>
    [JsonPropertyName("max_results")]
    public int? MaxResults { get; init; }
}

/// <summary>A single tool match returned by <see cref="ToolSearchTool"/>.</summary>
/// <param name="Name">Canonical tool name.</param>
/// <param name="Description">Tool description (from <see cref="ITool.GetDescriptionAsync"/>).</param>
/// <param name="MatchReason">How the query matched this tool.</param>
public record ToolSearchMatch(string Name, string Description, string MatchReason);

/// <summary>Strongly-typed output for the <see cref="ToolSearchTool"/>.</summary>
/// <param name="Query">The query that was searched.</param>
/// <param name="Matches">Tools that matched the query.</param>
public record ToolSearchOutput(string Query, IReadOnlyList<ToolSearchMatch> Matches);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Searches the <see cref="ToolRegistry"/> for tools whose name, aliases, or
/// <see cref="ITool.SearchHint"/> contain the query string (case-insensitive).
/// Only enabled tools are included in results.
/// </summary>
public sealed class ToolSearchTool : Tool<ToolSearchInput, ToolSearchOutput>
{
    private const int DefaultMaxResults = 5;
    private const int AbsoluteMaxResults = 50;

    private readonly ToolRegistry _registry;

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Search query matched against tool names, aliases, and search hints" },
            max_results = new
            {
                type = "integer",
                description = $"Maximum results to return (default {DefaultMaxResults}, max {AbsoluteMaxResults})",
                minimum = 1,
                maximum = AbsoluteMaxResults,
            },
        },
        required = new[] { "query" },
    });

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Initialises a new <see cref="ToolSearchTool"/> backed by the given registry.
    /// </summary>
    /// <param name="registry">The tool registry to search. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="registry"/> is <see langword="null"/>.
    /// </exception>
    public ToolSearchTool(ToolRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "ToolSearch";

    /// <inheritdoc/>
    public override string[] Aliases => ["tool_search", "find_tool"];

    /// <inheritdoc/>
    public override string? SearchHint => "search for available tools by name or capability";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Searches the tool registry for tools whose name, aliases, or search hint " +
            "match the query string. Returns tool names and descriptions.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `ToolSearch` to discover available tools. " +
            "Provide a `query` string; up to `max_results` (default 5) matching tool names " +
            "and descriptions are returned. Matching is case-insensitive.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "ToolSearch";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;
        if (input.Value.TryGetProperty("query", out var q) &&
            q.ValueKind == JsonValueKind.String)
        {
            return $"Searching tools for '{q.GetString()}'";
        }
        return "Searching tools";
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        ToolSearchInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Query))
            return Task.FromResult(ValidationResult.Failure("query must not be empty."));

        if (input.MaxResults.HasValue && input.MaxResults.Value <= 0)
            return Task.FromResult(ValidationResult.Failure(
                $"max_results must be a positive integer, got {input.MaxResults.Value}."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override ToolSearchInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<ToolSearchInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize ToolSearchInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(ToolSearchOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Matches.Count == 0)
            return $"No tools found matching '{result.Query}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {result.Matches.Count} tool(s) matching '{result.Query}':");
        sb.AppendLine();

        foreach (var match in result.Matches)
        {
            sb.Append("- **").Append(match.Name).Append("** (").Append(match.MatchReason).AppendLine(")");
            if (!string.IsNullOrWhiteSpace(match.Description))
                sb.Append("  ").AppendLine(match.Description);
        }

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<ToolSearchOutput>> ExecuteAsync(
        ToolSearchInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        int limit = Math.Clamp(input.MaxResults ?? DefaultMaxResults, 1, AbsoluteMaxResults);
        var query = input.Query.Trim();

        var matches = new List<ToolSearchMatch>();

        foreach (var tool in _registry.GetAll())
        {
            ct.ThrowIfCancellationRequested();

            if (!tool.IsEnabled())
                continue;

            string? matchReason = GetMatchReason(tool, query);
            if (matchReason is null)
                continue;

            // Fetch description asynchronously; fall back gracefully on failure.
            string description;
            try
            {
                description = await tool.GetDescriptionAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                description = string.Empty;
            }

            matches.Add(new ToolSearchMatch(tool.Name, description, matchReason));

            if (matches.Count >= limit)
                break;
        }

        return new ToolResult<ToolSearchOutput>
        {
            Data = new ToolSearchOutput(query, matches),
        };
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the match reason string when <paramref name="tool"/> matches
    /// <paramref name="query"/>, or <see langword="null"/> when it does not.
    /// Priority: name &gt; alias &gt; search hint.
    /// </summary>
    private static string? GetMatchReason(ITool tool, string query)
    {
        if (tool.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return "name match";

        foreach (var alias in tool.Aliases)
        {
            if (alias.Contains(query, StringComparison.OrdinalIgnoreCase))
                return $"alias '{alias}'";
        }

        if (tool.SearchHint is not null &&
            tool.SearchHint.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return "search hint";
        }

        return null;
    }
}
