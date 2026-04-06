namespace ClaudeCode.Tools.Bash;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="BashTool"/>.</summary>
public record BashInput
{
    /// <summary>The shell command to execute.</summary>
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    /// <summary>
    /// Optional execution timeout in milliseconds.
    /// When omitted the tool's <see cref="BashTool.DefaultTimeoutMs"/> is used.
    /// </summary>
    [JsonPropertyName("timeout")]
    public int? Timeout { get; init; }

    /// <summary>Optional human-readable description of what the command does.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="BashTool"/>.</summary>
/// <param name="ExitCode">The process exit code; 0 indicates success.</param>
/// <param name="Stdout">Combined stdout + stderr output from the process.</param>
/// <param name="Stderr">
/// Always empty string — stderr is merged into <see cref="Stdout"/> at the pipe level.
/// Kept in the record for forward-compatibility with callers that inspect both fields.
/// </param>
public record BashOutput(int ExitCode, string Stdout, string Stderr);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Executes arbitrary shell commands and returns the combined output and exit code.
/// Uses <c>bash -c</c> on Unix and <c>cmd /c</c> on Windows.
/// Output is truncated to <see cref="MaxResultSizeChars"/> characters (100 K by default).
/// </summary>
public sealed class BashTool : Tool<BashInput, BashOutput>
{
    /// <summary>Default process timeout when <see cref="BashInput.Timeout"/> is not set.</summary>
    public const int DefaultTimeoutMs = 120_000;

    private const string TruncationSuffix = "\n... [output truncated]";

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            command = new { type = "string", description = "The bash command to execute" },
            timeout = new { type = "integer", description = "Timeout in milliseconds" },
            description = new { type = "string", description = "Description of what this command does" },
        },
        required = new[] { "command" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "bash";

    /// <inheritdoc/>
    public override string[] Aliases => ["shell", "run_command"];

    /// <inheritdoc/>
    public override string? SearchHint => "execute shell commands";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Executes a shell command and returns the combined output and exit code.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use the `bash` tool to run shell commands. " +
            "Provide a `command` string and an optional `timeout` in milliseconds (default 120 s). " +
            "stdout and stderr are merged. Output exceeding 100,000 characters is truncated.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "Bash";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null)
            return null;

        if (input.Value.TryGetProperty("description", out var desc) &&
            desc.ValueKind == JsonValueKind.String)
        {
            return desc.GetString();
        }

        if (input.Value.TryGetProperty("command", out var cmd) &&
            cmd.ValueKind == JsonValueKind.String)
        {
            var raw = cmd.GetString() ?? string.Empty;
            return raw.Length <= 80 ? raw : string.Concat(raw.AsSpan(0, 77), "...");
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>Bash commands mutate state by default.</remarks>
    public override bool IsReadOnly(JsonElement input) => false;

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override BashInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<BashInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize BashInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(BashOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.Append("Exit code: ").AppendLine(result.ExitCode.ToString());

        if (!string.IsNullOrEmpty(result.Stdout))
            sb.AppendLine(result.Stdout);

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<BashOutput>> ExecuteAsync(
        BashInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(input.Command))
            throw new ArgumentException("Command must not be empty or whitespace.", nameof(input));

        int timeoutMs = input.Timeout ?? DefaultTimeoutMs;
        if (timeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(input), "Timeout must be a positive value in milliseconds.");

        var output = await RunProcessAsync(input.Command, context.Cwd, timeoutMs, ct).ConfigureAwait(false);
        return new ToolResult<BashOutput> { Data = output };
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private async Task<BashOutput> RunProcessAsync(
        string command,
        string workingDirectory,
        int timeoutMs,
        CancellationToken ct)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // Determine whether sandbox mode is active and build the effective
        // process to launch accordingly.
        var (exe, effectiveArgs) = BuildProcessArgs(command, isWindows);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = effectiveArgs,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var outputBuilder = new StringBuilder();
        var outputLock = new object();

        // Both stdout and stderr funnel into a single builder so they are interleaved
        // in arrival order (best-effort) and the caller sees a single combined stream.
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

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            KillProcessSafely(process);
            var timedOutOutput = TruncateOutput(outputBuilder.ToString());
            return new BashOutput(-1, $"Process timed out after {timeoutMs} ms.\n{timedOutOutput}", string.Empty);
        }
        catch (OperationCanceledException)
        {
            KillProcessSafely(process);
            throw;
        }

        // Allow async read handlers to flush
        await Task.Yield();

        var stdout = TruncateOutput(outputBuilder.ToString());
        return new BashOutput(process.ExitCode, stdout, string.Empty);
    }

    private string TruncateOutput(string output)
    {
        if (output.Length <= MaxResultSizeChars)
            return output;

        int cutPoint = MaxResultSizeChars - TruncationSuffix.Length;
        return string.Concat(output.AsSpan(0, cutPoint), TruncationSuffix);
    }

    private static void KillProcessSafely(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the check and the kill — safe to ignore.
        }
    }

    /// <summary>
    /// Wraps <paramref name="command"/> in single quotes so the entire string is passed
    /// as a single argument to <c>bash -c</c>, escaping any embedded single quotes.
    /// </summary>
    private static string EscapeForBash(string command)
        => $"'{command.Replace("'", "'\\''")}'";

    // -----------------------------------------------------------------------
    // Sandbox helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the (executable, arguments) pair that will be launched by
    /// <see cref="RunProcessAsync"/>. When <see cref="ClaudeCode.Core.State.SandboxModeState.IsEnabled"/>
    /// is <see langword="true"/>, the command is prefixed with a platform-specific sandbox wrapper:
    /// <list type="bullet">
    ///   <item>Windows — no external wrapper; <c>cmd /c</c> is used as-is</item>
    ///   <item>Linux — <c>unshare --user --pid --net --mount -r --</c></item>
    ///   <item>macOS — <c>sandbox-exec -p '(version 1)(deny default)(allow file-read*)' --</c></item>
    /// </list>
    /// </summary>
    private static (string Exe, string Args) BuildProcessArgs(string command, bool isWindows)
    {
        if (isWindows)
        {
            // Windows: no external sandbox wrapper available; cmd /c is used unconditionally.
            return ("cmd", $"/c {command}");
        }

        var bashArgs = $"-c {EscapeForBash(command)}";

        // Delegate sandbox decision to the shared SandboxModeState so that
        // both BashTool and SandboxToggleCommand share a single source of truth
        // without introducing a circular project dependency.
        var prefix = ClaudeCode.Core.State.SandboxModeState.GetCommandPrefix();

        if (prefix is null || prefix.Length == 0)
            return ("bash", bashArgs);

        // prefix[0] is the sandbox executable; prefix[1..] are its arguments ending with "--".
        // Append "bash <bashArgs>" after the prefix to form the complete invocation.
        var exe = prefix[0];
        var prefixTail = prefix.Length > 1 ? string.Join(" ", prefix[1..]) : string.Empty;
        var fullArgs = string.IsNullOrEmpty(prefixTail)
            ? $"bash {bashArgs}"
            : $"{prefixTail} bash {bashArgs}";

        return (exe, fullArgs);
    }
}
