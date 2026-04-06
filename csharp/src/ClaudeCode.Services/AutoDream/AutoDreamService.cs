namespace ClaudeCode.Services.AutoDream;

/// <summary>
/// Shared file-path index populated by <see cref="AutoDreamService"/> during idle periods.
/// Consumed by the REPL's Tab-completion engine for @-mention file suggestions.
/// </summary>
public static class FileIndexCache
{
    private static IReadOnlyList<string> _paths = [];

    /// <summary>Most-recently indexed list of file paths.</summary>
    public static IReadOnlyList<string> Paths => _paths;

    /// <summary>Atomically replaces the current index with <paramref name="paths"/>.</summary>
    /// <param name="paths">New path list. Must not be <see langword="null"/>.</param>
    public static void Update(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Volatile.Write(ref _paths!, paths);
    }
}

/// <summary>
/// Runs lightweight background tasks during REPL idle periods ("dreams").
/// Results are accumulated in <see cref="CompletedTasks"/> for the main thread to read
/// after each user interaction.
/// </summary>
/// <remarks>
/// <para>
/// The service starts a single background loop that wakes after an idle period
/// (default 5 minutes, resettable via <see cref="NotifyActivity"/>) and executes
/// one randomly selected dream task (e.g., scanning for TODO comments, indexing files).
/// Results are appended to <see cref="CompletedTasks"/> and are consumed by the REPL
/// loop to display dim-grey notices after each turn.
/// </para>
/// <para>
/// Call <see cref="Start"/> once after construction to begin the background loop,
/// and <see cref="DisposeAsync"/> to cancel and clean up.
/// </para>
/// </remarks>
public sealed class AutoDreamService : IAsyncDisposable
{
    private const int IdleThresholdSeconds = 30;

    private readonly System.Threading.CancellationTokenSource _cts = new();
    private Task? _dreamTask;
    private readonly string _cwd;
    private readonly List<string> _completedTasks = [];

    // Replaced each time NotifyActivity() fires to restart the idle countdown.
    private System.Threading.CancellationTokenSource _idleCts = new();

    /// <summary>All background task results accumulated since <see cref="Start"/> was called.</summary>
    public IReadOnlyList<string> CompletedTasks => _completedTasks.AsReadOnly();

    /// <summary>
    /// Initializes a new <see cref="AutoDreamService"/> rooted at <paramref name="cwd"/>.
    /// </summary>
    /// <param name="cwd">Working directory used when running dream sub-processes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="cwd"/> is null or whitespace.</exception>
    public AutoDreamService(string cwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        _cwd = cwd;
    }

    /// <summary>Launches the background dream loop. Safe to call only once.</summary>
    public void Start()
    {
        _dreamTask = Task.Run(RunDreamsAsync);
    }

    /// <summary>
    /// Called when the user starts typing or submits a turn. Resets the idle countdown so
    /// that dreams only run when the REPL has been idle for at least
    /// <see cref="IdleThresholdSeconds"/> seconds.
    /// </summary>
    public void NotifyActivity()
    {
        var previous = System.Threading.Interlocked.Exchange(
            ref _idleCts, new System.Threading.CancellationTokenSource());
        try { previous.Cancel(); } catch { }
        previous.Dispose();
    }

    // -------------------------------------------------------------------------
    // Private
    // -------------------------------------------------------------------------

    private async Task RunDreamsAsync()
    {
        var outerCt = _cts.Token;

        while (!outerCt.IsCancellationRequested)
        {
            // Wait for idle threshold, resetting when NotifyActivity() fires.
            bool idleElapsed = false;
            while (!outerCt.IsCancellationRequested && !idleElapsed)
            {
                // Capture the current idle token before creating the linked source.
                // If NotifyActivity() fires between capture and delay start the captured
                // token is cancelled, the delay immediately throws, and we loop to capture
                // the fresh token on the next iteration.
                var idleCt = _idleCts.Token;
                try
                {
                    using var linked =
                        System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
                            outerCt, idleCt);

                    await Task.Delay(
                        TimeSpan.FromSeconds(IdleThresholdSeconds),
                        linked.Token).ConfigureAwait(false);

                    idleElapsed = true;
                }
                catch (OperationCanceledException)
                {
                    if (outerCt.IsCancellationRequested) return;
                    // Activity detected — restart the idle countdown.
                }
            }

            if (outerCt.IsCancellationRequested) break;
            await RunOneDreamAsync(outerCt).ConfigureAwait(false);
        }
    }

    private async Task RunOneDreamAsync(CancellationToken ct)
    {
        var dreams = new Func<CancellationToken, Task<string?>>[]
        {
            // Dream 1: check for TODO/FIXME comments
            async c => {
                try {
                    var psi = new System.Diagnostics.ProcessStartInfo(
                        "grep", "-r \"TODO\\|FIXME\" --include=\"*.cs\" --include=\"*.ts\" -l")
                    {
                        WorkingDirectory = _cwd,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                    };
                    var p = System.Diagnostics.Process.Start(psi)!;
                    var output = await p.StandardOutput.ReadToEndAsync(c).ConfigureAwait(false);
                    await p.WaitForExitAsync(c).ConfigureAwait(false);
                    var files = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (files.Length > 0)
                        return $"Found TODO/FIXME in {files.Length} file(s): {string.Join(", ", files.Take(3))}";
                } catch { }
                return null;
            },

            // Dream 2: check git status summary
            async c => {
                try {
                    var psi = new System.Diagnostics.ProcessStartInfo("git", "status --short")
                    {
                        WorkingDirectory = _cwd,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                    };
                    var p = System.Diagnostics.Process.Start(psi)!;
                    var output = await p.StandardOutput.ReadToEndAsync(c).ConfigureAwait(false);
                    await p.WaitForExitAsync(c).ConfigureAwait(false);
                    var lineCount = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                    if (lineCount > 0)
                        return $"Git: {lineCount} uncommitted change(s) in working tree";
                } catch { }
                return null;
            },

            // Dream 3: index source files for @-mention autocomplete
            async c => {
                try {
                    await Task.Delay(100, c).ConfigureAwait(false);
                    var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { ".cs", ".ts", ".md" };

                    var paths = new List<string>();
                    foreach (var file in Directory.EnumerateFiles(
                        _cwd, "*", SearchOption.AllDirectories))
                    {
                        c.ThrowIfCancellationRequested();
                        if (extensions.Contains(Path.GetExtension(file)))
                            paths.Add(file);
                        if (paths.Count % 100 == 0)
                            await Task.Delay(100, c).ConfigureAwait(false);
                    }

                    FileIndexCache.Update(paths);
                    return $"Indexed {paths.Count} source file(s) for autocomplete";
                } catch (OperationCanceledException) { throw; }
                  catch { }
                return null;
            },
        };

        // Pick a random dream and run it.
        var dream = dreams[Random.Shared.Next(dreams.Length)];
        try
        {
            var result = await dream(ct).ConfigureAwait(false);
            if (result is not null)
                _completedTasks.Add($"[{DateTime.Now:HH:mm}] {result}");
        }
        catch (OperationCanceledException) { throw; }
        catch { /* dream failed silently */ }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_dreamTask is not null)
            await _dreamTask.ContinueWith(_ => { }).ConfigureAwait(false);
        _cts.Dispose();
        _idleCts.Dispose();
    }
}
