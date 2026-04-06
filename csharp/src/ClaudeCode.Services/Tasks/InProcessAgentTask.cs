namespace ClaudeCode.Services.Tasks;

using ClaudeCode.Core.Tasks;
using ClaudeCode.Services.Engine;

/// <summary>
/// Runs a sub-agent query as an in-process task, streaming text output to
/// <see cref="TaskStoreState"/> via <see cref="TaskStoreState.AppendOutput"/>.
/// </summary>
public sealed class InProcessAgentTask
{
    private readonly string _taskId;

    /// <summary>
    /// Initializes a new <see cref="InProcessAgentTask"/> bound to <paramref name="taskId"/>.
    /// </summary>
    /// <param name="taskId">
    /// The task ID that must already exist in <see cref="TaskStoreState.Tasks"/>.
    /// Must not be <see langword="null"/>.
    /// </param>
    public InProcessAgentTask(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        _taskId = taskId;
    }

    /// <summary>
    /// Submits <paramref name="prompt"/> via <paramref name="submitFunc"/>,
    /// appends each <see cref="TextDeltaEvent"/> to <see cref="TaskStoreState"/>,
    /// and finalises the task status when the stream ends.
    /// </summary>
    /// <param name="prompt">The user prompt to send. Must not be <see langword="null"/>.</param>
    /// <param name="submitFunc">
    /// A delegate that accepts a prompt and returns an async event stream.
    /// Typically <c>engine.SubmitAsync</c> partially applied over a session.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunAsync(
        string prompt,
        Func<string, IAsyncEnumerable<QueryEvent>> submitFunc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(submitFunc);

        TaskStoreState.UpdateTask(_taskId, TaskStatus.Running, "");
        var sb = new System.Text.StringBuilder();

        try
        {
            await foreach (var evt in submitFunc(prompt).WithCancellation(ct).ConfigureAwait(false))
            {
                if (evt is TextDeltaEvent td)
                {
                    sb.Append(td.Text);
                    TaskStoreState.AppendOutput(_taskId, td.Text);
                }
            }

            TaskStoreState.UpdateTask(_taskId, TaskStatus.Completed, sb.ToString());
        }
        catch (OperationCanceledException)
        {
            TaskStoreState.UpdateTask(_taskId, TaskStatus.Cancelled, "Cancelled");
        }
        catch (Exception ex)
        {
            TaskStoreState.UpdateTask(_taskId, TaskStatus.Failed, ex.Message);
        }
    }
}
