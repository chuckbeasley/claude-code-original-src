namespace ClaudeCode.Tools.TaskOutput;

using ClaudeCode.Core.Tools;
using System.Text.Json;
using System.Text.Json.Serialization;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="TaskOutputTool"/>.</summary>
public record TaskOutputInput
{
    /// <summary>The ID of the task whose output to retrieve (required).</summary>
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    /// <summary>
    /// When <see langword="true"/> (default), block until the task has output available.
    /// </summary>
    [JsonPropertyName("block")]
    public bool Block { get; init; } = true;

    /// <summary>
    /// Maximum number of milliseconds to wait for output when blocking.
    /// Defaults to 30,000 ms (30 seconds).
    /// </summary>
    [JsonPropertyName("timeout")]
    public int Timeout { get; init; } = 30_000;
}

/// <summary>Strongly-typed output for the <see cref="TaskOutputTool"/>.</summary>
/// <param name="TaskId">The task ID that was queried.</param>
/// <param name="Output">The task output text, or a placeholder when not available.</param>
public record TaskOutputOutput(string TaskId, string Output);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Retrieves stored output from a background task. Supports both non-blocking (immediate)
/// and blocking (poll-until-available) modes. When blocking, polls
/// <see cref="TaskStoreState.TaskOutputs"/> every 250 ms until output appears, the task
/// reaches a terminal state (<c>completed</c> or <c>deleted</c>), or the configured
/// timeout elapses.
/// </summary>
public sealed class TaskOutputTool : Tool<TaskOutputInput, TaskOutputOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string NotAvailableMessage = "Task output not available.";

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            task_id = new { type = "string", description = "The ID of the task whose output to retrieve" },
            block = new { type = "boolean", description = "Block until output is available (default true)", @default = true },
            timeout = new { type = "integer", description = "Timeout in milliseconds when blocking (default 30000)", @default = 30_000 },
        },
        required = new[] { "task_id" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "TaskOutput";

    /// <inheritdoc/>
    public override string? SearchHint => "retrieve output from a running task";

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Returns the stored output from a task, optionally blocking until output is available.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `TaskOutput` to read output that a background task has produced. " +
            "Provide `task_id`. Set `block` to false to return immediately if no output is available. " +
            "Use `timeout` to control how long to wait when blocking.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "TaskOutput";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null)
        {
            return null;
        }

        return input.Value.TryGetProperty("task_id", out var id) &&
            id.ValueKind == JsonValueKind.String
            ? $"Reading output of task {id.GetString()}"
            : "Reading task output";
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override TaskOutputInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<TaskOutputInput>(json.GetRawText(), JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize TaskOutputInput: result was null.");

    /// <inheritdoc/>
    public override string MapResultToString(TaskOutputOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Output;
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        TaskOutputInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.TaskId))
        {
            return Task.FromResult(ValidationResult.Failure("task_id must not be empty or whitespace."));
        }

        return input.Timeout <= 0
            ? Task.FromResult(ValidationResult.Failure("timeout must be a positive value in milliseconds."))
            : Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<TaskOutputOutput>> ExecuteAsync(
        TaskOutputInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        var taskId = input.TaskId;

        // Check if the task exists at all.
        if (!TaskStoreState.Tasks.TryGetValue(taskId, out var task))
        {
            return new ToolResult<TaskOutputOutput>
            {
                Data = new TaskOutputOutput(taskId, $"Task '{taskId}' not found."),
            };
        }

        // Output already available — return immediately.
        if (TaskStoreState.TaskOutputs.TryGetValue(taskId, out var output))
        {
            return new ToolResult<TaskOutputOutput>
            {
                Data = new TaskOutputOutput(taskId, output),
            };
        }

        // If blocking, poll until output appears, task reaches a terminal state, or timeout.
        if (input.Block)
        {
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(input.Timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (TaskStoreState.TaskOutputs.TryGetValue(taskId, out output))
                {
                    return new ToolResult<TaskOutputOutput>
                    {
                        Data = new TaskOutputOutput(taskId, output),
                    };
                }

                // No output will arrive for a terminal task — stop waiting.
                if (task?.Status is "completed" or "deleted")
                {
                    break;
                }

                await Task.Delay(250, ct).ConfigureAwait(false);

                // Refresh task status before the next iteration.
                TaskStoreState.Tasks.TryGetValue(taskId, out task);
            }
        }

        // Return whatever is available (may still be nothing).
        var finalOutput = TaskStoreState.TaskOutputs.TryGetValue(taskId, out var latestOutput)
            ? latestOutput
            : $"No output available for task '{taskId}' (status: {task?.Status ?? "unknown"}).";

        return new ToolResult<TaskOutputOutput>
        {
            Data = new TaskOutputOutput(taskId, finalOutput),
        };
    }
}
