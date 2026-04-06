namespace ClaudeCode.Services.Tasks;

/// <summary>
/// Manages an in-process sub-agent lifecycle. User messages can be injected
/// into the sub-agent's conversation. Corresponds to InProcessTeammateTask.tsx.
/// </summary>
public sealed class InProcessTeammateTask : IAsyncDisposable
{
    private static readonly List<InProcessTeammateTask> _activeInstances = [];
    private static readonly object _instanceLock = new();

    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;
    private readonly Queue<string> _pendingMessages = new();
    private readonly SemaphoreSlim _messageSemaphore = new(0);
    private readonly object _queueLock = new();

    /// <summary>Gets the unique identifier for this teammate task.</summary>
    public string TaskId { get; }

    /// <summary>Gets the display name of the sub-agent managed by this task.</summary>
    public string AgentName { get; }

    /// <summary>
    /// Gets a value indicating whether the sub-agent is currently running.
    /// Returns <see langword="false"/> if the internal cancellation token has been
    /// requested or no run task has been started.
    /// </summary>
    public bool IsRunning => !_cts.IsCancellationRequested && _runTask is { IsCompleted: false };

    /// <summary>
    /// Initializes a new <see cref="InProcessTeammateTask"/> and registers it in the
    /// process-wide active-instance registry.
    /// </summary>
    /// <param name="taskId">Unique task identifier. Must not be <see langword="null"/>.</param>
    /// <param name="agentName">Display name of the sub-agent. Must not be <see langword="null"/>.</param>
    public InProcessTeammateTask(string taskId, string agentName)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(agentName);
        TaskId = taskId;
        AgentName = agentName;
        lock (_instanceLock) { _activeInstances.Add(this); }
    }

    /// <summary>Injects a user message into the sub-agent's pending queue.</summary>
    /// <param name="message">The message text to inject. Must not be <see langword="null"/>.</param>
    public void InjectUserMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_queueLock) { _pendingMessages.Enqueue(message); }
        _messageSemaphore.Release();
    }

    /// <summary>Cancels the sub-agent and waits for it to stop.</summary>
    public void Kill()
    {
        _cts.Cancel();
        try
        {
            _runTask?.Wait();
        }
        catch (AggregateException ae) when (ae.InnerExceptions.All(static e => e is OperationCanceledException))
        {
            // Expected: task was cancelled.
        }
        catch (OperationCanceledException)
        {
            // Expected: task was cancelled.
        }
    }

    /// <summary>
    /// Returns all running teammate tasks sorted alphabetically by <see cref="AgentName"/>.
    /// Takes a brief snapshot under the instance lock, then sorts outside the lock.
    /// </summary>
    public static IReadOnlyList<InProcessTeammateTask> GetRunningTeammatesSorted()
    {
        InProcessTeammateTask[] snapshot;
        lock (_instanceLock) { snapshot = [.. _activeInstances]; }

        return Array.AsReadOnly(
            snapshot
                .Where(static t => t.IsRunning)
                .OrderBy(static t => t.AgentName, StringComparer.Ordinal)
                .ToArray());
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Kill();
        lock (_instanceLock) { _activeInstances.Remove(this); }
        _cts.Dispose();
        _messageSemaphore.Dispose();
        return ValueTask.CompletedTask;
    }
}
