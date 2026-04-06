namespace ClaudeCode.Services.Tasks;

using System.Text;
using System.Text.RegularExpressions;
using ClaudeCode.Core.Tasks;
using ClaudeCode.Services.Engine;

/// <summary>
/// Background session task that streams QueryEvents to a TaskStore state and
/// raises <see cref="OnNotification"/> events when XML notification blocks appear in output.
/// Corresponds to LocalMainSessionTask.ts.
/// </summary>
public sealed class LocalMainSessionTask : IAsyncDisposable
{
    private static readonly Regex NotificationPattern = new(
        @"<task_notification>(.*?)</task_notification>",
        RegexOptions.Singleline | RegexOptions.Compiled,
        matchTimeout: TimeSpan.FromSeconds(5));

    private CancellationTokenSource? _linkedCts;

    /// <summary>Gets the task identifier supplied at construction time.</summary>
    public string TaskId { get; }

    /// <summary>Raised when an XML task_notification block is detected in output.</summary>
    public event Action<string>? OnNotification;

    /// <summary>
    /// Initializes a new <see cref="LocalMainSessionTask"/> bound to <paramref name="taskId"/>.
    /// </summary>
    /// <param name="taskId">
    /// The task ID that must already exist in <see cref="TaskStoreState.Tasks"/>.
    /// Must not be <see langword="null"/>.
    /// </param>
    public LocalMainSessionTask(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        TaskId = taskId;
    }

    /// <summary>
    /// Runs the task: iterates the engine's async stream, accumulates output,
    /// detects XML notification blocks, and updates TaskStore status.
    /// </summary>
    /// <param name="prompt">The user prompt to send. Must not be <see langword="null"/>.</param>
    /// <param name="engine">
    /// A delegate that accepts a prompt and returns an async event stream.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunAsync(
        string prompt,
        Func<string, IAsyncEnumerable<QueryEvent>> engine,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(engine);

        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _linkedCts.Token;

        var sb = new StringBuilder();
        var raisedNotifications = new HashSet<string>(StringComparer.Ordinal);
        int inputTokens = 0, outputTokens = 0;

        TaskStoreState.UpdateTask(TaskId, TaskStatus.Running, "");

        try
        {
            await foreach (var evt in engine(prompt).WithCancellation(token).ConfigureAwait(false))
            {
                if (evt is TextDeltaEvent td)
                {
                    sb.Append(td.Text);
                    TaskStoreState.AppendOutput(TaskId, td.Text);

                    // Scan accumulated text for XML notification blocks; raise each unique match once.
                    foreach (Match m in NotificationPattern.Matches(sb.ToString()))
                    {
                        var content = m.Groups[1].Value;
                        if (raisedNotifications.Add(content))
                            OnNotification?.Invoke(content);
                    }
                }
                else if (evt is MessageCompleteEvent complete && complete.Usage is { } usage)
                {
                    inputTokens += usage.InputTokens;
                    outputTokens += usage.OutputTokens;
                }
            }

            TaskStoreState.UpdateTask(TaskId, TaskStatus.Completed, sb.ToString());
            await Console.Error.WriteLineAsync(
                    $"[LocalMainSessionTask:{TaskId}] Completed. Input tokens: {inputTokens}, Output tokens: {outputTokens}")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TaskStoreState.UpdateTask(TaskId, TaskStatus.Cancelled, "Cancelled");
            await Console.Error.WriteLineAsync($"[LocalMainSessionTask:{TaskId}] Cancelled.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TaskStoreState.UpdateTask(TaskId, TaskStatus.Failed, ex.Message);
            await Console.Error.WriteLineAsync($"[LocalMainSessionTask:{TaskId}] Failed: {ex.Message}")
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _linkedCts?.Cancel();
        _linkedCts?.Dispose();
        _linkedCts = null;
        return ValueTask.CompletedTask;
    }
}
