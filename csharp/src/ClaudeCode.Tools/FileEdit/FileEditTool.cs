namespace ClaudeCode.Tools.FileEdit;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Events;
using ClaudeCode.Core.Tools;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="FileEditTool"/>.</summary>
public record FileEditInput
{
    /// <summary>Absolute or relative path of the file to edit.</summary>
    [JsonPropertyName("file_path")]
    public required string FilePath { get; init; }

    /// <summary>
    /// The exact text to search for and replace.
    /// An empty string combined with a non-existent file creates a new file.
    /// </summary>
    [JsonPropertyName("old_string")]
    public required string OldString { get; init; }

    /// <summary>The text to substitute in place of <see cref="OldString"/>.</summary>
    [JsonPropertyName("new_string")]
    public required string NewString { get; init; }

    /// <summary>
    /// When <see langword="true"/> all occurrences of <see cref="OldString"/> are replaced.
    /// When <see langword="false"/> (default) the edit fails if more than one match exists.
    /// </summary>
    [JsonPropertyName("replace_all")]
    public bool ReplaceAll { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="FileEditTool"/>.</summary>
/// <param name="FilePath">The path of the file that was edited or created.</param>
/// <param name="Diff">A unified-style diff of the changes applied.</param>
/// <param name="Created"><see langword="true"/> when a new file was created by this edit.</param>
public record FileEditOutput(string FilePath, string Diff, bool Created);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Edits a file in place by replacing an exact string with new content.
/// Supports quote normalisation (curly ↔ straight), staleness detection,
/// multi-match guards, new-file creation, and unified-style diff output.
/// </summary>
public class FileEditTool : Tool<FileEditInput, FileEditOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            file_path = new { type = "string", description = "The absolute path to the file to modify" },
            old_string = new { type = "string", description = "The text to replace" },
            new_string = new { type = "string", description = "The text to replace it with" },
            replace_all = new { type = "boolean", description = "Replace all occurrences (default false)", @default = false },
        },
        required = new[] { "file_path", "old_string", "new_string" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "Edit";

    /// <inheritdoc/>
    public override string? SearchHint => "modify file contents in place";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Edit files by replacing text strings");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Performs exact string replacements in files. " +
            "The old_string must match exactly (unique in the file unless replace_all is true). " +
            "You MUST read the file first before editing it. " +
            "Use replace_all=true to replace every occurrence of a string.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "Edit";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;

        if (input.Value.TryGetProperty("file_path", out var fp) &&
            fp.ValueKind == JsonValueKind.String)
        {
            return $"Editing {fp.GetString()}";
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>File edits mutate the filesystem.</remarks>
    public override bool IsReadOnly(JsonElement input) => false;

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override FileEditInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<FileEditInput>(json.GetRawText(), JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize FileEditInput: result was null.");

    /// <inheritdoc/>
    public override string MapResultToString(FileEditOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Created)
            return $"Created new file {result.FilePath} successfully.";
        if (result.Diff.Length > 0)
            return $"The file {result.FilePath} has been updated successfully.";
        return $"The file {result.FilePath} was not changed.";
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ValidationResult> ValidateInputAsync(
        FileEditInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(input.FilePath))
            return ValidationResult.Failure("file_path must not be empty or whitespace.");

        var fullPath = Path.GetFullPath(input.FilePath, context.Cwd);

        // No-op guard: identical strings produce no change.
        if (input.OldString == input.NewString)
            return ValidationResult.Failure("No changes: old_string and new_string are identical.", 1);

        // File does not exist.
        if (!File.Exists(fullPath))
        {
            // An empty old_string on a non-existent file signals file creation.
            if (input.OldString == "")
                return ValidationResult.Success;

            return ValidationResult.Failure($"File does not exist: {fullPath}", 4);
        }

        // Must have been read first (staleness guard requires a baseline).
        var readState = context.ReadFileState.Get(fullPath);
        if (readState is null)
            return ValidationResult.Failure(
                "File has not been read yet. Read it first with the read_file tool.", 6);

        // Staleness check: reject if the file changed on disk since last read.
        var lastWrite = File.GetLastWriteTimeUtc(fullPath);
        if (lastWrite > readState.Timestamp.UtcDateTime)
        {
            // Fallback: compare content — cloud-sync tools can bump timestamps
            // without actually modifying the file.
            var currentContent = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
            if (currentContent != readState.Content)
                return ValidationResult.Failure(
                    "File has been modified since last read. Read it again before editing.", 7);
        }

        // Read full file content for match analysis.
        var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
        content = content.ReplaceLineEndings("\n");

        // Empty old_string on an existing non-empty file is not allowed.
        if (input.OldString == "" && content.Trim().Length > 0)
            return ValidationResult.Failure(
                "Cannot create new file — file already exists with content.", 3);

        // Locate old_string (with quote-normalisation fallback).
        var actualOldString = FindActualString(content, input.OldString);
        if (actualOldString is null)
            return ValidationResult.Failure(
                $"String to replace not found in file.\nString: {input.OldString}", 8);

        // Guard against ambiguous replacements when replace_all is false.
        var matchCount = CountOccurrences(content, actualOldString);
        if (matchCount > 1 && !input.ReplaceAll)
            return ValidationResult.Failure(
                $"Found {matchCount} matches but replace_all is false. " +
                "Provide more surrounding context to uniquely identify the instance, " +
                "or set replace_all=true to replace every occurrence.", 9);

        return ValidationResult.Success;
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<FileEditOutput>> ExecuteAsync(
        FileEditInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        var fullPath = Path.GetFullPath(input.FilePath, context.Cwd);

        // Auto-create parent directories.
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string originalContent;
        bool created;

        if (File.Exists(fullPath))
        {
            originalContent = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
            originalContent = originalContent.ReplaceLineEndings("\n");
            created = false;

            // Save a snapshot before mutating so undo is possible.
            context.FileHistory.SaveSnapshot(fullPath, originalContent);
        }
        else
        {
            originalContent = "";
            created = true;
        }

        // Resolve actual string with quote-normalisation fallback.
        var actualOldString = FindActualString(originalContent, input.OldString) ?? input.OldString;

        // Apply the edit.
        string newContent;
        if (input.OldString == "" && created)
        {
            // New-file creation path.
            newContent = input.NewString;
        }
        else if (input.ReplaceAll)
        {
            newContent = originalContent.Replace(actualOldString, input.NewString, StringComparison.Ordinal);
        }
        else
        {
            // Replace the first (and, after validation, only) occurrence.
            var idx = originalContent.IndexOf(actualOldString, StringComparison.Ordinal);
            newContent = idx < 0
                ? originalContent // Should not reach here after validation.
                : string.Concat(
                    originalContent.AsSpan(0, idx),
                    input.NewString,
                    originalContent.AsSpan(idx + actualOldString.Length));
        }

        // Persist to disk.
        // Notify subscribers (e.g. RewindCommand) that a file is about to change
        // so they can capture a snapshot of the original content for undo.
        FileEditEvents.RaiseBeforeEdit(fullPath, originalContent);

        await File.WriteAllTextAsync(fullPath, newContent, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
            .ConfigureAwait(false);

        // Refresh the read-state cache so subsequent reads and edits see the new content.
        context.ReadFileState.Set(fullPath, new FileReadState(
            Content: newContent,
            Timestamp: new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero)));

        var diff = GenerateDiff(originalContent, newContent, fullPath);

        return new ToolResult<FileEditOutput>
        {
            Data = new FileEditOutput(input.FilePath, diff, created),
        };
    }

    // -----------------------------------------------------------------------
    // Quote normalisation helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Attempts to locate <paramref name="searchString"/> in <paramref name="fileContent"/>.
    /// First tries an exact match; on failure retries after normalising curly quotes to
    /// their straight equivalents in both strings.
    /// Returns the slice of <paramref name="fileContent"/> that matched (preserving the
    /// original characters), or <see langword="null"/> when no match is found.
    /// </summary>
    private static string? FindActualString(string fileContent, string searchString)
    {
        // Fast path: exact match.
        if (fileContent.Contains(searchString, StringComparison.Ordinal))
            return searchString;

        // Slow path: normalise both strings and retry.
        // NormalizeQuotes is a 1-to-1 character mapping so string lengths are preserved,
        // meaning the index found in the normalised content is valid in the original.
        var normalizedSearch = NormalizeQuotes(searchString);
        var normalizedFile = NormalizeQuotes(fileContent);

        var idx = normalizedFile.IndexOf(normalizedSearch, StringComparison.Ordinal);
        if (idx >= 0)
            return fileContent.Substring(idx, searchString.Length);

        return null;
    }

    /// <summary>
    /// Replaces curly (typographic) quote characters with their straight ASCII equivalents.
    /// The replacement is strictly 1-to-1 so string length is preserved.
    /// </summary>
    private static string NormalizeQuotes(string s)
        => s
            .Replace('\u2018', '\'')  // LEFT SINGLE QUOTATION MARK
            .Replace('\u2019', '\'')  // RIGHT SINGLE QUOTATION MARK
            .Replace('\u201C', '"')   // LEFT DOUBLE QUOTATION MARK
            .Replace('\u201D', '"');  // RIGHT DOUBLE QUOTATION MARK

    // -----------------------------------------------------------------------
    // Match-counting helper
    // -----------------------------------------------------------------------

    private static int CountOccurrences(string text, string search)
    {
        if (search.Length == 0) return 0;

        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(search, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += search.Length;
        }
        return count;
    }

    // -----------------------------------------------------------------------
    // Diff generation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Generates a unified-style diff string using DiffPlex.
    /// Unchanged lines are omitted; inserted lines are prefixed with <c>+</c>,
    /// deleted lines with <c>-</c>, and modified lines with <c>~</c>.
    /// </summary>
    private static string GenerateDiff(string oldContent, string newContent, string filePath)
    {
        var diffBuilder = new InlineDiffBuilder(new Differ());
        var diff = diffBuilder.BuildDiffModel(oldContent, newContent);

        var sb = new StringBuilder();
        sb.AppendLine($"--- {filePath}");
        sb.AppendLine($"+++ {filePath}");

        foreach (var line in diff.Lines)
        {
            switch (line.Type)
            {
                case ChangeType.Inserted:
                    sb.AppendLine($"+{line.Text}");
                    break;
                case ChangeType.Deleted:
                    sb.AppendLine($"-{line.Text}");
                    break;
                case ChangeType.Modified:
                    sb.AppendLine($"~{line.Text}");
                    break;
                case ChangeType.Unchanged:
                    // Omit unchanged context lines for brevity.
                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }
}
