namespace ClaudeCode.Tools.Worktree;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="ExitWorktreeTool"/>.</summary>
public record ExitWorktreeInput
{
    /// <summary>
    /// What to do with the worktree on exit. Must be <c>"keep"</c> or <c>"remove"</c>.
    /// </summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>
    /// When <see langword="true"/> and <see cref="Action"/> is <c>"remove"</c>,
    /// passes <c>--force</c> to <c>git worktree remove</c> to discard uncommitted changes.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    [JsonPropertyName("discard_changes")]
    public bool DiscardChanges { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="ExitWorktreeTool"/>.</summary>
/// <param name="Action">The action that was requested.</param>
/// <param name="Success">Whether the operation completed successfully.</param>
/// <param name="Message">Human-readable confirmation or error message.</param>
public record ExitWorktreeOutput(string Action, bool Success, string Message);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Exits the current Git worktree context. When <c>action</c> is <c>"remove"</c>
/// the worktree directory is removed using <c>git worktree remove</c>. When
/// <c>action</c> is <c>"keep"</c> the directory is left on disk.
/// </summary>
public sealed class ExitWorktreeTool : Tool<ExitWorktreeInput, ExitWorktreeOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly HashSet<string> ValidActions = ["keep", "remove"];

    private const int GitTimeoutMs = 30_000;

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type = "string",
                description = "What to do with the worktree: 'keep' leaves it on disk, 'remove' deletes it",
                @enum = new[] { "keep", "remove" },
            },
            discard_changes = new
            {
                type = "boolean",
                description = "When true and action is 'remove', discards uncommitted changes (--force)",
                @default = false,
            },
        },
        required = new[] { "action" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "ExitWorktree";

    /// <inheritdoc/>
    public override string? SearchHint => "remove or detach from a git worktree";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Exits a Git worktree, optionally removing it from disk.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `ExitWorktree` when you are done working in a Git worktree. " +
            "Set `action` to 'remove' to delete the worktree directory, or 'keep' to leave it. " +
            "Set `discard_changes` to true to force removal even when there are uncommitted changes. " +
            "The command must be run from within the worktree directory.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "ExitWorktree";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;

        if (input.Value.TryGetProperty("action", out var action) &&
            action.ValueKind == JsonValueKind.String)
        {
            return action.GetString() == "remove"
                ? "Removing git worktree"
                : "Exiting git worktree";
        }

        return "Exiting git worktree";
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override ExitWorktreeInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<ExitWorktreeInput>(json.GetRawText(), JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize ExitWorktreeInput: result was null.");

    /// <inheritdoc/>
    public override string MapResultToString(ExitWorktreeOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Message;
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        ExitWorktreeInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Action))
            return Task.FromResult(ValidationResult.Failure("action must not be empty or whitespace."));

        if (!ValidActions.Contains(input.Action))
            return Task.FromResult(ValidationResult.Failure(
                $"Invalid action '{input.Action}'. Valid values: keep, remove."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<ExitWorktreeOutput>> ExecuteAsync(
        ExitWorktreeInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        if (string.Equals(input.Action, "keep", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolResult<ExitWorktreeOutput>
            {
                Data = new ExitWorktreeOutput("keep", true,
                    "Worktree kept on disk. No changes were made."),
            };
        }

        // action == "remove"
        var forceFlag = input.DiscardChanges ? " --force" : string.Empty;
        var removeArgs = $"worktree remove{forceFlag} \"{context.Cwd}\"";

        var (exitCode, output) = await RunGitCommandAsync(removeArgs, context.Cwd, ct)
            .ConfigureAwait(false);

        if (exitCode != 0)
        {
            return new ToolResult<ExitWorktreeOutput>
            {
                Data = new ExitWorktreeOutput("remove", false,
                    $"git worktree remove failed: {output.Trim()}"),
            };
        }

        return new ToolResult<ExitWorktreeOutput>
        {
            Data = new ExitWorktreeOutput("remove", true, "Worktree removed successfully."),
        };
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static async Task<(int ExitCode, string Output)> RunGitCommandAsync(
        string arguments,
        string workingDirectory,
        CancellationToken ct)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "git.exe" : "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        var outputBuilder = new StringBuilder();
        var outputLock = new object();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (outputLock) { outputBuilder.AppendLine(e.Data); }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (outputLock) { outputBuilder.AppendLine(e.Data); }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(GitTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
            }

            throw;
        }

        await Task.Yield(); // Allow async read handlers to flush.
        return (process.ExitCode, outputBuilder.ToString());
    }
}
