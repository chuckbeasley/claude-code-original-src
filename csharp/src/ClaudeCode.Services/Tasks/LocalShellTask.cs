namespace ClaudeCode.Services.Tasks;

using ClaudeCode.Core.Tasks;

/// <summary>
/// A long-running shell task that streams output to the TaskStore.
/// Start the task via <see cref="StartAsync"/> and dispose to stop the process.
/// </summary>
public sealed class LocalShellTask : IAsyncDisposable
{
    private readonly string _taskId;
    private System.Diagnostics.Process? _process;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Initializes a new <see cref="LocalShellTask"/> for the given task ID.
    /// </summary>
    /// <param name="taskId">
    /// The task ID that must already exist in <see cref="TaskStoreState.Tasks"/>.
    /// Must not be <see langword="null"/>.
    /// </param>
    public LocalShellTask(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        _taskId = taskId;
    }

    /// <summary>
    /// Starts the shell command, streams stdout/stderr into <see cref="TaskStoreState"/>,
    /// and updates the task status when the process exits.
    /// </summary>
    /// <param name="command">The shell command to execute. Must not be <see langword="null"/>.</param>
    /// <param name="cwd">Working directory for the process. Must not be <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token; cancellation kills the process.</param>
    public async Task StartAsync(string command, string cwd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(cwd);

        TaskStoreState.UpdateTask(_taskId, TaskStatus.Running, "");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName  = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            Arguments = OperatingSystem.IsWindows()
                ? $"/c {command}"
                : $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        _process = new System.Diagnostics.Process { StartInfo = psi };
        _process.Start();

        // Stream stdout to TaskStore concurrently.
        var outputTask = Task.Run(async () =>
        {
            var sb = new System.Text.StringBuilder();
            while (!_process.StandardOutput.EndOfStream)
            {
                var line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                sb.AppendLine(line);
                TaskStoreState.AppendOutput(_taskId, line + "\n");
            }
        }, CancellationToken.None);

        // Stream stderr to TaskStore concurrently.
        var errorTask = Task.Run(async () =>
        {
            while (!_process.StandardError.EndOfStream)
            {
                var line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                TaskStoreState.AppendOutput(_taskId, $"[stderr] {line}\n");
            }
        }, CancellationToken.None);

        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        await _process.WaitForExitAsync(ct).ConfigureAwait(false);
        var exitCode = _process.ExitCode;

        TaskStoreState.UpdateTask(
            _taskId,
            exitCode == 0 ? TaskStatus.Completed : TaskStatus.Failed,
            $"Exited with code {exitCode}");
    }

    /// <summary>
    /// Kills the running process and cancels the internal token.
    /// Safe to call multiple times.
    /// </summary>
    public void Stop()
    {
        try { _process?.Kill(entireProcessTree: true); }
        catch { /* best-effort kill */ }
        _cts.Cancel();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Stop();
        _cts.Dispose();
        _process?.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
