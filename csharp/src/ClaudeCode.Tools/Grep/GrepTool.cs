namespace ClaudeCode.Tools.Grep;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeCode.Core.Tools;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

/// <summary>
/// Input model for <see cref="GrepTool"/>.
/// </summary>
public record GrepInput
{
    /// <summary>Regular expression pattern to search for.</summary>
    [JsonPropertyName("pattern")]
    public required string Pattern { get; init; }

    /// <summary>
    /// Optional directory to search. Defaults to <see cref="ToolUseContext.Cwd"/>.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>
    /// Optional glob pattern to restrict the set of files searched
    /// (e.g. <c>**/*.cs</c>).
    /// </summary>
    [JsonPropertyName("glob")]
    public string? Glob { get; init; }

    /// <summary>
    /// Output mode. One of:
    /// <list type="bullet">
    ///   <item><c>files_with_matches</c> (default) — one matched file path per line.</item>
    ///   <item><c>content</c> — matched lines with file path and line number prefix.</item>
    ///   <item><c>count</c> — number of matches per file.</item>
    /// </list>
    /// </summary>
    [JsonPropertyName("output_mode")]
    public string? OutputMode { get; init; }

    /// <summary>
    /// Maximum number of output lines to return. Defaults to 250.
    /// In <c>content</c> mode this is forwarded to ripgrep's <c>--max-count</c> per file;
    /// in other modes the output is truncated after this many lines.
    /// </summary>
    [JsonPropertyName("head_limit")]
    public int? HeadLimit { get; init; }
}

/// <summary>
/// Output produced by <see cref="GrepTool"/>.
/// </summary>
/// <param name="Content">
/// The formatted search output as a newline-separated string.
/// </param>
/// <param name="MatchCount">
/// Number of matched lines or files in the output (best-effort; exact when
/// using the fallback path, approximate when using ripgrep).
/// </param>
public record GrepOutput(string Content, int MatchCount);

/// <summary>
/// Tool that searches file contents using a regular expression pattern.
/// Attempts to delegate to <c>rg</c> (ripgrep) for performance; falls back to
/// a built-in <see cref="Regex"/>-based search when ripgrep is not available.
/// </summary>
public sealed class GrepTool : Tool<GrepInput, GrepOutput>, ITool
{
    /// <summary>Default maximum output lines when <see cref="GrepInput.HeadLimit"/> is absent.</summary>
    public const int DefaultHeadLimit = 250;

    /// <summary>Valid values for <see cref="GrepInput.OutputMode"/>.</summary>
    public static readonly IReadOnlySet<string> ValidOutputModes =
        new HashSet<string>(StringComparer.Ordinal) { "files_with_matches", "content", "count" };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "grep";

    /// <inheritdoc/>
    public override string[] Aliases => ["Grep"];

    /// <inheritdoc/>
    public override string? SearchHint => "Search file contents using a regular expression";

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>Grep never writes to disk.</remarks>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    /// <remarks>Grep is stateless — parallel invocations are safe.</remarks>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Prompting / schema
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Searches file contents using a regular expression pattern. " +
            "Delegates to ripgrep (rg) when available for maximum performance; " +
            "falls back to a built-in .NET regex search otherwise. " +
            "Supports output modes: files_with_matches (default), content, count.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use the grep tool to search for text patterns inside files. " +
            "Supply a `pattern` (required, regular expression) and an optional `path` to restrict the search root. " +
            "Use `glob` to restrict the file types searched (e.g. \"**/*.cs\"). " +
            "Use `output_mode` to choose between files_with_matches, content, or count. " +
            "Use `head_limit` to cap the number of output lines (default 250).");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null)
    {
        if (input is null) return Name;
        try
        {
            var pattern = input.Value.GetProperty("pattern").GetString();
            return pattern is not null ? $"Grep({pattern})" : Name;
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
            return pattern is not null ? $"Searching for \"{pattern}\"" : null;
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
                  "description": "Regular expression pattern to search for in file contents."
                },
                "path": {
                  "type": "string",
                  "description": "Directory to search in. Defaults to the current working directory."
                },
                "glob": {
                  "type": "string",
                  "description": "Glob pattern to restrict which files are searched (e.g. \"**/*.cs\")."
                },
                "output_mode": {
                  "type": "string",
                  "enum": ["files_with_matches", "content", "count"],
                  "description": "Output format. Defaults to \"files_with_matches\"."
                },
                "head_limit": {
                  "type": "integer",
                  "description": "Maximum number of output lines to return. Defaults to 250.",
                  "minimum": 1
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
    public override GrepInput DeserializeInput(JsonElement json)
        => json.Deserialize<GrepInput>(_jsonOptions)
           ?? throw new InvalidOperationException("Failed to deserialize GrepInput: result was null.");

    /// <inheritdoc/>
    public override string MapResultToString(GrepOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return string.IsNullOrEmpty(result.Content) ? "No matches found." : result.Content;
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        GrepInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(input.Pattern))
            return Task.FromResult(ValidationResult.Failure("Pattern must not be empty."));

        if (input.OutputMode is not null && !ValidOutputModes.Contains(input.OutputMode))
            return Task.FromResult(ValidationResult.Failure(
                $"Invalid output_mode \"{input.OutputMode}\". Valid values: {string.Join(", ", ValidOutputModes)}."));

        if (input.HeadLimit is < 1)
            return Task.FromResult(ValidationResult.Failure("head_limit must be at least 1."));

        var searchDir = input.Path ?? context.Cwd;
        if (!Directory.Exists(searchDir))
            return Task.FromResult(ValidationResult.Failure($"Search directory does not exist: {searchDir}"));

        // Validate the pattern compiles as a regex (catches obvious mistakes early).
        try
        {
            _ = new Regex(input.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ValidationResult.Failure($"Invalid regex pattern: {ex.Message}"));
        }

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<GrepOutput>> ExecuteAsync(
        GrepInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        var searchPath = input.Path ?? context.Cwd;
        var limit = input.HeadLimit ?? DefaultHeadLimit;

        // Attempt ripgrep first; fall back to .NET regex on failure.
        var (rgOutput, rgExitCode) = await TryRunRipgrepAsync(input, searchPath, limit, ct).ConfigureAwait(false);

        if (rgExitCode is not null)
        {
            // ripgrep exit 0 = matches found, exit 1 = no matches (not an error).
            // Any other exit code is a genuine ripgrep error — fall back.
            if (rgExitCode is 0 or 1)
            {
                var content = ApplyHeadLimit(rgOutput!, limit);
                var matchCount = CountLines(content);
                return new ToolResult<GrepOutput> { Data = new GrepOutput(content, matchCount) };
            }
        }

        // Fallback: built-in .NET regex search.
        var fallbackContent = FallbackRegexSearch(input, searchPath, limit);
        var fallbackMatchCount = CountLines(fallbackContent);
        return new ToolResult<GrepOutput> { Data = new GrepOutput(fallbackContent, fallbackMatchCount) };
    }

    // -----------------------------------------------------------------------
    // ripgrep invocation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Attempts to invoke <c>rg</c>. Returns <see langword="null"/> exit code when
    /// ripgrep is not found on PATH or fails to start.
    /// </summary>
    private static async Task<(string? Output, int? ExitCode)> TryRunRipgrepAsync(
        GrepInput input,
        string searchPath,
        int limit,
        CancellationToken ct)
    {
        var args = BuildRipgrepArguments(input, limit);

        var psi = new ProcessStartInfo("rg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = searchPath,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            // rg is not installed or not on PATH.
            return (null, null);
        }

        if (proc is null)
            return (null, null);

        using (proc)
        {
            var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return (output, proc.ExitCode);
        }
    }

    /// <summary>
    /// Builds the argument list for the <c>rg</c> invocation.
    /// </summary>
    private static List<string> BuildRipgrepArguments(GrepInput input, int limit)
    {
        var args = new List<string>();
        var mode = input.OutputMode ?? "files_with_matches";

        switch (mode)
        {
            case "files_with_matches":
                args.Add("--files-with-matches");
                break;

            case "count":
                args.Add("--count");
                break;

            default: // "content"
                args.Add("-n"); // include line numbers
                // In content mode, --max-count limits matches *per file*.
                // We pass limit here as a reasonable per-file cap.
                args.Add("--max-count");
                args.Add(limit.ToString());
                break;
        }

        if (input.Glob is not null)
        {
            args.Add("--glob");
            args.Add(input.Glob);
        }

        args.Add("--");
        args.Add(input.Pattern);
        args.Add("."); // search from the working directory (set as WorkingDirectory on the process)

        return args;
    }

    // -----------------------------------------------------------------------
    // Fallback regex search
    // -----------------------------------------------------------------------

    /// <summary>
    /// Built-in .NET regex search used when ripgrep is unavailable.
    /// Honours <see cref="GrepInput.Glob"/>, <see cref="GrepInput.OutputMode"/>, and
    /// the <paramref name="limit"/> cap.
    /// </summary>
    private static string FallbackRegexSearch(GrepInput input, string searchPath, int limit)
    {
        var regex = new Regex(
            input.Pattern,
            RegexOptions.Compiled,
            matchTimeout: TimeSpan.FromSeconds(5));

        var mode = input.OutputMode ?? "files_with_matches";

        IEnumerable<string> allFiles = Directory.EnumerateFiles(
            searchPath, "*", SearchOption.AllDirectories);

        // Apply glob filter when specified.
        if (input.Glob is not null)
            allFiles = FilterByGlob(allFiles, input.Glob, searchPath);

        return mode switch
        {
            "count" => FallbackCountMode(regex, allFiles, limit),
            "files_with_matches" => FallbackFilesWithMatchesMode(regex, allFiles, limit),
            _ => FallbackContentMode(regex, allFiles, limit),
        };
    }

    private static IEnumerable<string> FilterByGlob(
        IEnumerable<string> files,
        string globPattern,
        string searchRoot)
    {
        var matcher = new Matcher();
        matcher.AddInclude(globPattern);
        var dirInfo = new DirectoryInfoWrapper(new DirectoryInfo(searchRoot));
        var matchResult = matcher.Execute(dirInfo);
        var matched = new HashSet<string>(
            matchResult.Files.Select(f => Path.GetFullPath(Path.Combine(searchRoot, f.Path))),
            StringComparer.OrdinalIgnoreCase);

        return files.Where(f => matched.Contains(f));
    }

    private static string FallbackFilesWithMatchesMode(
        Regex regex, IEnumerable<string> files, int limit)
    {
        var results = new List<string>();

        foreach (var file in files)
        {
            if (results.Count >= limit) break;

            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (regex.IsMatch(line))
                    {
                        results.Add(file);
                        break; // one match is enough to include this file
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return string.Join('\n', results);
    }

    private static string FallbackContentMode(
        Regex regex, IEnumerable<string> files, int limit)
    {
        var results = new List<string>();

        foreach (var file in files)
        {
            if (results.Count >= limit) break;

            try
            {
                var lineIndex = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineIndex++;
                    if (results.Count >= limit) break;

                    if (regex.IsMatch(line))
                        results.Add($"{file}:{lineIndex}:{line}");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return string.Join('\n', results);
    }

    private static string FallbackCountMode(
        Regex regex, IEnumerable<string> files, int limit)
    {
        var results = new List<string>();

        foreach (var file in files)
        {
            if (results.Count >= limit) break;

            try
            {
                var count = 0;
                foreach (var line in File.ReadLines(file))
                {
                    if (regex.IsMatch(line))
                        count++;
                }

                if (count > 0)
                    results.Add($"{file}:{count}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return string.Join('\n', results);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Truncates <paramref name="output"/> to at most <paramref name="limit"/> lines.
    /// </summary>
    private static string ApplyHeadLimit(string output, int limit)
    {
        if (string.IsNullOrEmpty(output)) return output;

        var sb = new StringBuilder();
        var lineCount = 0;

        foreach (var line in output.AsSpan().EnumerateLines())
        {
            if (lineCount >= limit) break;
            if (lineCount > 0) sb.Append('\n');
            sb.Append(line);
            lineCount++;
        }

        return sb.ToString();
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 0;
        foreach (var _ in text.AsSpan().EnumerateLines())
            count++;
        return count;
    }

    // ExecuteRawAsync is implemented by Tool<TInput, TOutput> and includes
    // validation via ValidateInputAsync before delegating to ExecuteAsync.
}
