namespace ClaudeCode.Tools.FileRead;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="FileReadTool"/>.</summary>
public record FileReadInput
{
    /// <summary>Absolute or relative path of the file to read.</summary>
    [JsonPropertyName("file_path")]
    public required string FilePath { get; init; }

    /// <summary>
    /// Zero-based line number at which to start reading.
    /// When omitted the file is read from the first line.
    /// </summary>
    [JsonPropertyName("offset")]
    public int? Offset { get; init; }

    /// <summary>
    /// Maximum number of lines to return.
    /// When omitted up to <see cref="FileReadTool.DefaultLineLimit"/> lines are returned.
    /// </summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="FileReadTool"/>.</summary>
/// <param name="Content">
/// File content with 1-based line numbers prefixed in the format <c>{lineNum}\t{line}</c>.
/// </param>
/// <param name="TotalLines">Total number of lines in the file (before any offset/limit is applied).</param>
public record FileReadOutput(string Content, int TotalLines);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Reads a file from disk and returns its content with 1-based line numbers.
/// Supports optional offset and limit to return a window into large files.
/// Detects UTF-8 and UTF-16 LE encodings via BOM and updates the
/// <see cref="FileStateCache"/> after each successful read.
/// </summary>
public sealed class FileReadTool : Tool<FileReadInput, FileReadOutput>
{
    /// <summary>Maximum number of lines returned when <see cref="FileReadInput.Limit"/> is not set.</summary>
    public const int DefaultLineLimit = 2_000;

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            file_path = new { type = "string", description = "Absolute or relative path of the file to read" },
            offset = new { type = "integer", description = "Zero-based line number at which to start reading" },
            limit = new { type = "integer", description = "Maximum number of lines to return" },
        },
        required = new[] { "file_path" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "read_file";

    /// <inheritdoc/>
    public override string[] Aliases => ["Read", "file_read"];

    /// <inheritdoc/>
    public override string? SearchHint => "read file contents with line numbers";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Reads a file from disk and returns its content with 1-based line numbers. " +
            "Supports optional offset (zero-based) and limit to read a window into large files.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use the `read_file` tool to read a file. " +
            "Provide `file_path` and optionally `offset` (zero-based first line) and `limit` (max lines). " +
            "Content is returned with `{lineNum}\\t{content}` formatting. " +
            "Default limit is 2,000 lines. Always use this tool before editing a file.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "Read";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;

        if (input.Value.TryGetProperty("file_path", out var fp) &&
            fp.ValueKind == JsonValueKind.String)
        {
            return $"Reading {fp.GetString()}";
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>File reads are non-mutating.</remarks>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    /// <remarks>Concurrent reads of the same file are safe.</remarks>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override FileReadInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<FileReadInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize FileReadInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(FileReadOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Content;
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        FileReadInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(input.FilePath))
            return Task.FromResult(ValidationResult.Failure("file_path must not be empty or whitespace."));

        if (input.Offset is < 0)
            return Task.FromResult(ValidationResult.Failure($"offset must be >= 0, got {input.Offset}."));

        if (input.Limit is <= 0)
            return Task.FromResult(ValidationResult.Failure($"limit must be a positive integer, got {input.Limit}."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<FileReadOutput>> ExecuteAsync(
        FileReadInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // Resolve the path: if relative, anchor to cwd.
        var absolutePath = Path.IsPathRooted(input.FilePath)
            ? input.FilePath
            : Path.GetFullPath(input.FilePath, context.Cwd);

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException(
                $"File not found: '{absolutePath}'. " +
                "Verify the path is correct and the file exists before calling read_file.",
                absolutePath);
        }

        int offset = input.Offset ?? 0;
        int limit = input.Limit ?? DefaultLineLimit;

        var (content, totalLines) = await ReadWithLineNumbersAsync(absolutePath, offset, limit, ct).ConfigureAwait(false);

        bool isPartial = offset > 0 || totalLines > offset + limit;
        context.ReadFileState.Set(absolutePath, new FileReadState(
            Content: content,
            Timestamp: DateTimeOffset.UtcNow,
            Offset: offset > 0 ? offset : null,
            Limit: input.Limit,
            IsPartialView: isPartial));

        return new ToolResult<FileReadOutput>
        {
            Data = new FileReadOutput(content, totalLines),
        };
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static async Task<(string Content, int TotalLines)> ReadWithLineNumbersAsync(
        string path,
        int offset,
        int limit,
        CancellationToken ct)
    {
        var encoding = DetectEncoding(path);

        // Read all lines so we can report TotalLines and apply offset accurately.
        var lines = await File.ReadAllLinesAsync(path, encoding, ct).ConfigureAwait(false);
        int totalLines = lines.Length;

        // Clamp offset to valid range.
        int start = Math.Min(offset, totalLines);
        int available = totalLines - start;
        int count = Math.Min(limit, available);

        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            int lineNumber = start + i + 1; // 1-based
            sb.Append(lineNumber).Append('\t').AppendLine(lines[start + i]);
        }

        return (sb.ToString(), totalLines);
    }

    /// <summary>
    /// Detects file encoding by inspecting the BOM (byte-order mark).
    /// Supports UTF-16 LE (0xFF 0xFE) and UTF-8 with BOM (0xEF 0xBB 0xBF).
    /// Falls back to UTF-8 without BOM for all other files.
    /// </summary>
    private static Encoding DetectEncoding(string path)
    {
        Span<byte> bom = stackalloc byte[3];
        int read;

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4))
            read = fs.Read(bom);

        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            return Encoding.Unicode; // UTF-16 LE

        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}
