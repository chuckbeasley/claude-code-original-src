namespace ClaudeCode.Tools.FileWrite;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="FileWriteTool"/>.</summary>
public record FileWriteInput
{
    /// <summary>Absolute or relative path of the file to write.</summary>
    [JsonPropertyName("file_path")]
    public required string FilePath { get; init; }

    /// <summary>Complete file content to write. Existing content is replaced entirely.</summary>
    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="FileWriteTool"/>.</summary>
/// <param name="FilePath">The absolute path of the file that was written.</param>
/// <param name="Created">
/// <see langword="true"/> when the file was newly created;
/// <see langword="false"/> when an existing file was overwritten.
/// </param>
public record FileWriteOutput(string FilePath, bool Created);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Writes complete file content to disk, auto-creating any missing parent directories.
/// When overwriting an existing file the original line-ending style (CRLF vs LF) is
/// preserved. Updates the <see cref="FileStateCache"/> after each successful write.
/// </summary>
public sealed class FileWriteTool : Tool<FileWriteInput, FileWriteOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            file_path = new { type = "string", description = "Absolute or relative path of the file to write" },
            content = new { type = "string", description = "Complete file content to write" },
        },
        required = new[] { "file_path", "content" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "write_file";

    /// <inheritdoc/>
    public override string[] Aliases => ["Write", "file_write"];

    /// <inheritdoc/>
    public override string? SearchHint => "write or overwrite a file";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Writes complete content to a file, creating the file and any missing " +
            "parent directories as needed. Overwrites existing files entirely.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use the `write_file` tool to create or overwrite a file. " +
            "Provide `file_path` and the full `content` to write. " +
            "Parent directories are created automatically. " +
            "You MUST read the file first with `read_file` before overwriting it " +
            "to avoid losing existing content.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "Write";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;

        if (input.Value.TryGetProperty("file_path", out var fp) &&
            fp.ValueKind == JsonValueKind.String)
        {
            return $"Writing {fp.GetString()}";
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>Write operations mutate the filesystem.</remarks>
    public override bool IsReadOnly(JsonElement input) => false;

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override FileWriteInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<FileWriteInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize FileWriteInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(FileWriteOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Created
            ? $"Created file: {result.FilePath}"
            : $"Updated file: {result.FilePath}";
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        FileWriteInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(input.FilePath))
            return Task.FromResult(ValidationResult.Failure("file_path must not be empty or whitespace."));

        // Content is allowed to be empty — writing an empty file is a valid operation.

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<FileWriteOutput>> ExecuteAsync(
        FileWriteInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // Resolve path: if relative, anchor to cwd.
        var absolutePath = Path.IsPathRooted(input.FilePath)
            ? input.FilePath
            : Path.GetFullPath(input.FilePath, context.Cwd);

        bool fileExists = File.Exists(absolutePath);

        // Auto-create parent directories if needed.
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        // Determine content to write, normalising line endings to match the
        // existing file when overwriting, or defaulting to the platform style.
        string finalContent = fileExists
            ? NormaliseLineEndings(input.Content, await DetectLineEndingAsync(absolutePath, ct).ConfigureAwait(false))
            : input.Content;

        await File.WriteAllTextAsync(absolutePath, finalContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
            .ConfigureAwait(false);

        // Update the cache so the read-state reflects the new content.
        context.ReadFileState.Set(absolutePath, new FileReadState(
            Content: finalContent,
            Timestamp: DateTimeOffset.UtcNow,
            Offset: null,
            Limit: null,
            IsPartialView: false));

        return new ToolResult<FileWriteOutput>
        {
            Data = new FileWriteOutput(absolutePath, Created: !fileExists),
        };
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the first few KB of an existing file to infer whether it uses CRLF or LF
    /// line endings. Returns <c>"\r\n"</c> for CRLF or <c>"\n"</c> for LF.
    /// </summary>
    private static async Task<string> DetectLineEndingAsync(string path, CancellationToken ct)
    {
        const int sampleBytes = 8_192;
        var buffer = new byte[sampleBytes];

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: sampleBytes);
        int read = await fs.ReadAsync(buffer.AsMemory(0, sampleBytes), ct).ConfigureAwait(false);

        for (int i = 0; i < read - 1; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n')
                return "\r\n";
        }

        return "\n";
    }

    /// <summary>
    /// Rewrites all line endings in <paramref name="content"/> to <paramref name="lineEnding"/>.
    /// </summary>
    private static string NormaliseLineEndings(string content, string lineEnding)
    {
        // Normalise to LF first, then convert to the target style — avoids double CRLF
        // if the incoming content already contains mixed or CRLF endings.
        var withLf = content.Replace("\r\n", "\n", StringComparison.Ordinal);

        return lineEnding == "\n"
            ? withLf
            : withLf.Replace("\n", "\r\n", StringComparison.Ordinal);
    }
}
