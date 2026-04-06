namespace ClaudeCode.Tools.PowerShell;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="PowerShellTool"/>.</summary>
public record PowerShellInput
{
    /// <summary>The PowerShell command or script to execute.</summary>
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    /// <summary>
    /// Optional execution timeout in milliseconds.
    /// When omitted the tool's <see cref="PowerShellTool.DefaultTimeoutMs"/> is used.
    /// </summary>
    [JsonPropertyName("timeout")]
    public int? Timeout { get; init; }

    /// <summary>Optional human-readable description of what the command does.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="PowerShellTool"/>.</summary>
/// <param name="ExitCode">The process exit code; 0 indicates success.</param>
/// <param name="Stdout">Combined stdout + stderr output from the process.</param>
/// <param name="Stderr">
/// Always empty string — stderr is merged into <see cref="Stdout"/> at the pipe level.
/// Kept in the record for forward-compatibility with callers that inspect both fields.
/// </param>
public record PowerShellOutput(int ExitCode, string Stdout, string Stderr);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Executes PowerShell commands and returns the combined output and exit code.
/// Prefers <c>pwsh</c> (PowerShell 7+) and falls back to <c>powershell</c> (Windows PowerShell 5).
/// Only enabled on Windows.
/// Output is truncated to <see cref="MaxResultSizeChars"/> characters (100 K by default).
/// </summary>
public sealed class PowerShellTool : Tool<PowerShellInput, PowerShellOutput>
{
    /// <summary>Default process timeout when <see cref="PowerShellInput.Timeout"/> is not set.</summary>
    public const int DefaultTimeoutMs = 120_000;

    private const string TruncationSuffix = "\n... [output truncated]";

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            command = new { type = "string", description = "The PowerShell command to execute" },
            timeout = new { type = "integer", description = "Timeout in milliseconds" },
            description = new { type = "string", description = "Description of what this command does" },
        },
        required = new[] { "command" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "PowerShell";

    /// <inheritdoc/>
    public override string[] Aliases => ["pwsh", "powershell"];

    /// <inheritdoc/>
    public override string? SearchHint => "execute PowerShell commands on Windows";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Executes a PowerShell command and returns the combined output and exit code. Only available on Windows.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use the `PowerShell` tool to run PowerShell commands on Windows. " +
            "Provide a `command` string and an optional `timeout` in milliseconds (default 120 s). " +
            "stdout and stderr are merged. Output exceeding 100,000 characters is truncated. " +
            "This tool is only available on Windows.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "PowerShell";

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
    /// <remarks>PowerShell commands mutate state by default.</remarks>
    public override bool IsReadOnly(JsonElement input) => false;

    /// <inheritdoc/>
    /// <remarks>Only enabled on Windows.</remarks>
    public override bool IsEnabled() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override PowerShellInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<PowerShellInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize PowerShellInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(PowerShellOutput result, string toolUseId)
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
    public override async Task<ToolResult<PowerShellOutput>> ExecuteAsync(
        PowerShellInput input,
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
        return new ToolResult<PowerShellOutput> { Data = output };
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private async Task<PowerShellOutput> RunProcessAsync(
        string command,
        string workingDirectory,
        int timeoutMs,
        CancellationToken ct)
    {
        // Prefer pwsh (PowerShell 7+); fall back to powershell (Windows PowerShell 5).
        string executable = ResolvePowerShellExecutable();

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            // -NoProfile speeds up startup; -NonInteractive prevents prompts;
            // -Command passes the script text.
            Arguments = $"-NoProfile -NonInteractive -Command {EscapeForPowerShell(command)}",
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
            return new PowerShellOutput(-1, $"Process timed out after {timeoutMs} ms.\n{timedOutOutput}", string.Empty);
        }
        catch (OperationCanceledException)
        {
            KillProcessSafely(process);
            throw;
        }

        // Allow async read handlers to flush.
        await Task.Yield();

        var stdout = TruncateOutput(outputBuilder.ToString());
        return new PowerShellOutput(process.ExitCode, stdout, string.Empty);
    }

    /// <summary>
    /// Returns <c>pwsh</c> if it is on the PATH, otherwise falls back to <c>powershell</c>.
    /// </summary>
    private static string ResolvePowerShellExecutable()
    {
        try
        {
            using var probe = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pwsh",
                    Arguments = "-NoProfile -NonInteractive -Command exit 0",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            probe.Start();
            probe.WaitForExit(2_000);
            return "pwsh";
        }
        catch
        {
            return "powershell";
        }
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
    /// Wraps <paramref name="command"/> in a PowerShell string literal so the text is
    /// passed verbatim to <c>-Command</c> without shell interpretation.
    /// </summary>
    private static string EscapeForPowerShell(string command)
    {
        // Wrap in single quotes and escape any embedded single quotes by doubling them.
        var escaped = command.Replace("'", "''");
        return $"& {{'{escaped}'}}";
    }
}
