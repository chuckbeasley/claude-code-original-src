namespace ClaudeCode.Tools.Glob;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

/// <summary>
/// Input model for <see cref="GlobTool"/>.
/// </summary>
public record GlobInput
{
    /// <summary>The glob pattern to match, e.g. <c>**/*.cs</c>.</summary>
    [JsonPropertyName("pattern")]
    public required string Pattern { get; init; }

    /// <summary>
    /// Optional directory to search. When absent the tool searches
    /// <see cref="ToolUseContext.Cwd"/> from the ambient context.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

/// <summary>
/// Output produced by <see cref="GlobTool"/>.
/// </summary>
/// <param name="Files">
/// Absolute paths of matched files, sorted by last-modified time (newest first),
/// capped at <see cref="GlobTool.MaxFiles"/>.
/// </param>
/// <param name="TotalMatches">
/// Number of files matched before the cap was applied.
/// </param>
public record GlobOutput(IReadOnlyList<string> Files, int TotalMatches);

/// <summary>
/// Tool that performs glob pattern matching against the file system and returns
/// matching file paths sorted by last-modified time (newest first).
/// Uses <c>Microsoft.Extensions.FileSystemGlobbing</c> for pattern evaluation.
/// </summary>
public sealed class GlobTool : Tool<GlobInput, GlobOutput>, ITool
{
    /// <summary>Maximum number of file paths returned in a single result.</summary>
    public const int MaxFiles = 1_000;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "glob";

    /// <inheritdoc/>
    public override string[] Aliases => ["Glob"];

    /// <inheritdoc/>
    public override string? SearchHint => "Find files matching a glob pattern, sorted by modification time";

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>Glob never writes to disk.</remarks>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    /// <remarks>Glob is stateless — parallel invocations are safe.</remarks>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Prompting / schema
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Fast file pattern matching tool that works with any codebase size. " +
            "Supports glob patterns like \"**/*.js\" or \"src/**/*.ts\". " +
            "Returns matching file paths sorted by modification time (newest first). " +
            $"Results are capped at {MaxFiles} entries.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use the glob tool to find files by name pattern. " +
            "Supply a `pattern` (required) and an optional `path` to restrict the search root. " +
            "Prefer specific patterns (e.g. `src/**/*.cs`) over broad wildcards to keep results manageable.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null)
    {
        if (input is null) return Name;
        try
        {
            var pattern = input.Value.GetProperty("pattern").GetString();
            return pattern is not null ? $"Glob({pattern})" : Name;
        }
        catch (KeyNotFoundException)
        {
            return Name;
        }
    }

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;
        try
        {
            var pattern = input.Value.GetProperty("pattern").GetString();
            return pattern is not null ? $"Searching for files matching {pattern}" : null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public override JsonElement GetInputSchema()
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "pattern": {
                  "type": "string",
                  "description": "Glob pattern to match (e.g. \"**/*.cs\", \"src/**/*.{ts,tsx}\")."
                },
                "path": {
                  "type": "string",
                  "description": "Directory to search in. Defaults to the current working directory."
                }
              },
              "required": ["pattern"]
            }
            """;
        return JsonSerializer.Deserialize<JsonElement>(schema);
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override GlobInput DeserializeInput(JsonElement json)
        => json.Deserialize<GlobInput>(_jsonOptions)
           ?? throw new InvalidOperationException("Failed to deserialize GlobInput: result was null.");

    /// <inheritdoc/>
    public override string MapResultToString(GlobOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Files.Count == 0)
            return "No files matched.";

        var lines = new System.Text.StringBuilder();
        foreach (var file in result.Files)
            lines.AppendLine(file);

        if (result.TotalMatches > result.Files.Count)
            lines.AppendLine($"(showing {result.Files.Count} of {result.TotalMatches} matches — results capped at {MaxFiles})");

        return lines.ToString().TrimEnd();
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        GlobInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(input.Pattern))
            return Task.FromResult(ValidationResult.Failure("Pattern must not be empty."));

        var searchDir = input.Path ?? context.Cwd;
        if (!Directory.Exists(searchDir))
            return Task.FromResult(ValidationResult.Failure($"Search directory does not exist: {searchDir}"));

        return Task.FromResult(ValidationResult.Success);
    }

    /// <inheritdoc/>
    public override Task<ToolResult<GlobOutput>> ExecuteAsync(
        GlobInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        var searchDir = input.Path ?? context.Cwd;

        var matcher = new Matcher();
        matcher.AddInclude(input.Pattern);

        var directoryInfo = new DirectoryInfoWrapper(new DirectoryInfo(searchDir));
        var matchResult = matcher.Execute(directoryInfo);

        // Materialise all matches first so we can report TotalMatches accurately.
        var allFiles = matchResult.Files
            .Select(f => Path.GetFullPath(Path.Combine(searchDir, f.Path)))
            .Where(File.Exists)
            .ToList();

        var totalMatches = allFiles.Count;

        var sortedFiles = allFiles
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .Take(MaxFiles)
            .ToList();

        var output = new GlobOutput(sortedFiles, totalMatches);
        return Task.FromResult(new ToolResult<GlobOutput> { Data = output });
    }

    // ExecuteRawAsync is implemented by Tool<TInput, TOutput> and includes
    // validation via ValidateInputAsync before delegating to ExecuteAsync.
}
