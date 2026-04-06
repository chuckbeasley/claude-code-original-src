namespace ClaudeCode.Core.Tasks;

using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>Represents a single tracked task item.</summary>
public record TaskItem
{
    /// <summary>Unique auto-incremented identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Short human-readable title.</summary>
    public required string Subject { get; set; }

    /// <summary>Extended description of the work to be done.</summary>
    public string? Description { get; set; }

    /// <summary>Optional UI form name associated with this task.</summary>
    public string? ActiveForm { get; set; }

    /// <summary>
    /// Lifecycle status. Valid values: <c>"pending"</c>, <c>"in_progress"</c>,
    /// <c>"completed"</c>, <c>"deleted"</c>, <c>"failed"</c>, <c>"cancelled"</c>.
    /// </summary>
    public string Status { get; set; } = "pending";

    /// <summary>Optional owner / assignee identifier.</summary>
    public string? Owner { get; set; }

    /// <summary>IDs of tasks that this task blocks.</summary>
    public List<string> Blocks { get; init; } = [];

    /// <summary>IDs of tasks that this task is blocked by.</summary>
    public List<string> BlockedBy { get; init; } = [];

    /// <summary>Arbitrary caller-supplied metadata.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>
/// Execution status for background tasks created by <c>LocalShellTask</c> and
/// <c>InProcessAgentTask</c>.
/// </summary>
public enum TaskStatus
{
    /// <summary>The task has been started and is actively executing.</summary>
    Running,

    /// <summary>The task finished successfully.</summary>
    Completed,

    /// <summary>The task exited with a non-zero code or threw an unhandled exception.</summary>
    Failed,

    /// <summary>The task was cancelled via a <see cref="CancellationToken"/>.</summary>
    Cancelled,
}

/// <summary>
/// Process-wide in-memory store shared across all task-related tools.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/> and
/// <see cref="Interlocked"/> counter.
/// </summary>
public static class TaskStoreState
{
    private static int _nextId;

    /// <summary>All tasks keyed by their string ID.</summary>
    public static ConcurrentDictionary<string, TaskItem> Tasks { get; } = new();

    /// <summary>
    /// Output text produced by completed tasks, keyed by task ID.
    /// Written by whatever agent or process fulfils a task; read by <c>TaskOutput</c>.
    /// </summary>
    public static ConcurrentDictionary<string, string> TaskOutputs { get; } = new();

    /// <summary>The current todo list, if one has been written by the <c>TodoWrite</c> tool.</summary>
    public static JsonElement? TodoList { get; set; }

    /// <summary>Returns the next unique task ID and advances the counter atomically.</summary>
    public static string NextId() => Interlocked.Increment(ref _nextId).ToString();

    /// <summary>
    /// Appends <paramref name="text"/> to the accumulated output for <paramref name="taskId"/>.
    /// Thread-safe; multiple producers may call this concurrently.
    /// </summary>
    /// <param name="taskId">The task identifier. Must not be <see langword="null"/>.</param>
    /// <param name="text">The text fragment to append. No-op when <see langword="null"/> or empty.</param>
    public static void AppendOutput(string taskId, string text)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        if (string.IsNullOrEmpty(text)) return;

        TaskOutputs.AddOrUpdate(
            taskId,
            text,
            (_, existing) => existing + text);
    }

    /// <summary>
    /// Updates the execution status of the task identified by <paramref name="taskId"/>
    /// and optionally stores a terminal message in <see cref="TaskOutputs"/>.
    /// </summary>
    /// <param name="taskId">The task identifier. Must not be <see langword="null"/>.</param>
    /// <param name="status">The new execution status.</param>
    /// <param name="message">
    /// When non-empty, stored as the task output in <see cref="TaskOutputs"/>.
    /// Pass an empty string to update the status without touching stored output.
    /// </param>
    public static void UpdateTask(string taskId, TaskStatus status, string message)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        if (Tasks.TryGetValue(taskId, out var task))
        {
            task.Status = status switch
            {
                TaskStatus.Running    => "in_progress",
                TaskStatus.Completed  => "completed",
                TaskStatus.Failed     => "failed",
                TaskStatus.Cancelled  => "cancelled",
                _                     => task.Status,
            };
        }

        if (!string.IsNullOrEmpty(message))
            TaskOutputs[taskId] = message;
    }
}
