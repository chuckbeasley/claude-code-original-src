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

/// <summary>Strongly-typed input for the <see cref="EnterWorktreeTool"/>.</summary>
public record EnterWorktreeInput
{
    /// <summary>
    /// Optional name for the worktree directory. When omitted a timestamped name is
    /// generated automatically.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="EnterWorktreeTool"/>.</summary>
/// <param name="WorktreePath">Absolute path of the created worktree, or <see langword="null"/> on failure.</param>
/// <param name="Success">Whether the worktree was created successfully.</param>
/// <param name="ErrorMessage">Human-readable error detail when <see cref="Success"/> is <see langword="false"/>.</param>
public record EnterWorktreeOutput(string? WorktreePath, bool Success, string? ErrorMessage);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Creates a new Git worktree using <c>git worktree add</c> and returns its path.
/// Returns a descriptive error when Git is unavailable or the current directory is
/// not inside a Git repository.
/// </summary>
public sealed class EnterWorktreeTool : Tool<EnterWorktreeInput, EnterWorktreeOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const int GitTimeoutMs = 30_000;

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", description = "Optional name for the worktree directory" },
        },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "EnterWorktree";

    /// <inheritdoc/>
    public override string? SearchHint => "create a git worktree";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Creates a new Git worktree and returns its path.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `EnterWorktree` to create a new Git worktree in an isolated directory. " +
            "Optionally provide a `name` for the worktree; a timestamped name is generated if omitted. " +
            "The tool returns the absolute path of the new worktree. " +
            "Use `ExitWorktree` to clean up when done.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "EnterWorktree";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null) => "Creating git worktree";

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override EnterWorktreeInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<EnterWorktreeInput>(json.GetRawText(), JsonOpts)
            ?? new EnterWorktreeInput();

    /// <inheritdoc/>
    public override string MapResultToString(EnterWorktreeOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Success)
            return $"Error creating worktree: {result.ErrorMessage}";

        return $"Worktree created at: {result.WorktreePath}";
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<EnterWorktreeOutput>> ExecuteAsync(
        EnterWorktreeInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // Verify git is available before attempting anything.
        if (!await IsGitAvailableAsync(ct).ConfigureAwait(false))
        {
            return new ToolResult<EnterWorktreeOutput>
            {
                Data = new EnterWorktreeOutput(null, false, "Git is not available on this system."),
            };
        }

        // Verify the CWD is inside a git repository.
        var repoRootResult = await RunGitCommandAsync(
            "rev-parse --show-toplevel", context.Cwd, ct).ConfigureAwait(false);

        if (repoRootResult.ExitCode != 0)
        {
            return new ToolResult<EnterWorktreeOutput>
            {
                Data = new EnterWorktreeOutput(null, false,
                    "The current directory is not inside a Git repository."),
            };
        }

        var repoRoot = repoRootResult.Output.Trim();

        // Build a unique worktree directory name.
        var worktreeName = !string.IsNullOrWhiteSpace(input.Name)
            ? input.Name.Trim()
            : $"worktree-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";

        // Place worktrees sibling to the repo root to keep them outside the working tree.
        var worktreePath = Path.Combine(Path.GetDirectoryName(repoRoot) ?? repoRoot, worktreeName);

        var addResult = await RunGitCommandAsync(
            $"worktree add \"{worktreePath}\"", context.Cwd, ct).ConfigureAwait(false);

        if (addResult.ExitCode != 0)
        {
            return new ToolResult<EnterWorktreeOutput>
            {
                Data = new EnterWorktreeOutput(null, false,
                    $"git worktree add failed: {addResult.Output.Trim()}"),
            };
        }

        return new ToolResult<EnterWorktreeOutput>
        {
            Data = new EnterWorktreeOutput(worktreePath, true, null),
        };
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static async Task<bool> IsGitAvailableAsync(CancellationToken ct)
    {
        try
        {
            var result = await RunGitCommandAsync("--version", Environment.CurrentDirectory, ct)
                .ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return false;
        }
    }

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
