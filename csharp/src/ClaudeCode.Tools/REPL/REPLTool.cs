namespace ClaudeCode.Tools.REPL;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="REPLTool"/>.</summary>
public record REPLInput
{
    /// <summary>The programming language whose REPL should execute the code.</summary>
    [JsonPropertyName("language")]
    public required string Language { get; init; }

    /// <summary>The code snippet to run in the REPL.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="REPLTool"/>.</summary>
/// <param name="Message">Result or status message from the REPL.</param>
public record REPLOutput(string Message);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Executes code snippets by spawning the appropriate language interpreter.
/// Supports Python, Node.js, Ruby, Bash, PowerShell, and Perl.
/// Output is truncated to <see cref="MaxResultSizeChars"/> characters (100 K by default).
/// </summary>
public sealed class REPLTool : Tool<REPLInput, REPLOutput>
{
    private const int ExecutionTimeoutSeconds = 30;
    private const string TruncationSuffix = "\n...[truncated]";

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            language = new { type = "string", description = "Programming language of the REPL (e.g. 'python', 'node')" },
            code     = new { type = "string", description = "Code to execute in the REPL" },
        },
        required = new[] { "language", "code" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "REPL";

    /// <inheritdoc/>
    public override string[] Aliases => ["repl", "run_code"];

    /// <inheritdoc/>
    public override string? SearchHint => "run code interactively in a REPL";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Runs a code snippet by spawning the appropriate language interpreter. " +
            "Supported languages: python, node, ruby, bash, powershell, perl.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `REPL` to run short code snippets. " +
            "Provide `language` (e.g. `python`, `node`, `ruby`, `bash`, `pwsh`, `perl`) and `code`. " +
            "Execution times out after 30 seconds. Output exceeding 100,000 characters is truncated.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "REPL";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;
        if (input.Value.TryGetProperty("language", out var lang) &&
            lang.ValueKind == JsonValueKind.String)
        {
            return $"Running {lang.GetString()} code in REPL";
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>Code execution can mutate state on disk or in the environment.</remarks>
    public override bool IsReadOnly(JsonElement input) => false;

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        REPLInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Language))
            return Task.FromResult(ValidationResult.Failure("language must not be empty."));

        if (string.IsNullOrWhiteSpace(input.Code))
            return Task.FromResult(ValidationResult.Failure("code must not be empty."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override REPLInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<REPLInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize REPLInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(REPLOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Message;
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<REPLOutput>> ExecuteAsync(
        REPLInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        var (command, args) = ResolveInterpreter(input.Language, input.Code);
        if (command is null)
        {
            return new ToolResult<REPLOutput>
            {
                Data = new REPLOutput(
                    $"Unsupported language: {input.Language}. " +
                    "Supported: python, node, ruby, bash, powershell, perl."),
            };
        }

        try
        {
            var psi = new ProcessStartInfo(command)
            {
                WorkingDirectory = context.Cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new ToolResult<REPLOutput>
                {
                    Data = new REPLOutput($"Failed to start '{command}'. Is it installed?"),
                };
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(ExecutionTimeoutSeconds));

            string stdout;
            string stderr;
            try
            {
                stdout = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                stderr = await proc.StandardError.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                KillProcessSafely(proc);
                return new ToolResult<REPLOutput>
                {
                    Data = new REPLOutput($"Execution timed out after {ExecutionTimeoutSeconds} seconds."),
                };
            }

            var output = BuildOutput(stdout, stderr, proc.ExitCode);
            var result = TruncateOutput(output);
            return new ToolResult<REPLOutput> { Data = new REPLOutput(result) };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ToolResult<REPLOutput> { Data = new REPLOutput($"Execution error: {ex.Message}") };
        }
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Maps a language name to the interpreter command and arguments that pass
    /// <paramref name="code"/> directly on the command line.
    /// Returns <c>(null, [])</c> for unsupported languages.
    /// </summary>
    private static (string? Command, string[] Args) ResolveInterpreter(string language, string code)
    {
        return language.ToLowerInvariant() switch
        {
            "python" or "python3" or "py" => ("python3", ["-c", code]),
            "node"   or "javascript" or "js" => ("node", ["-e", code]),
            "ruby"   or "rb"                  => ("ruby", ["-e", code]),
            "bash"   or "sh"                  => ("bash", ["-c", code]),
            "powershell" or "pwsh" or "ps"    => ("pwsh", ["-Command", code]),
            "perl"                            => ("perl", ["-e", code]),
            _ => (null, []),
        };
    }

    /// <summary>
    /// Combines stdout, stderr, and exit code into a single result string.
    /// Returns <c>"(no output)"</c> when all streams are empty and the exit code is 0.
    /// </summary>
    private static string BuildOutput(string stdout, string stderr, int exitCode)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(stdout))
            sb.AppendLine(stdout.TrimEnd());

        if (!string.IsNullOrEmpty(stderr))
        {
            if (sb.Length > 0)
                sb.AppendLine();
            sb.AppendLine("[stderr]");
            sb.AppendLine(stderr.TrimEnd());
        }

        if (exitCode != 0)
            sb.Append($"\n[exit code: {exitCode}]");

        return sb.Length > 0 ? sb.ToString().TrimEnd() : "(no output)";
    }

    /// <summary>
    /// Truncates <paramref name="output"/> to <see cref="MaxResultSizeChars"/> characters,
    /// appending <see cref="TruncationSuffix"/> when truncation occurs.
    /// </summary>
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
}
