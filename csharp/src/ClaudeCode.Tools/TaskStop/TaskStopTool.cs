namespace ClaudeCode.Tools.TaskStop;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;
using ClaudeCode.Tools.TaskStore;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="TaskStopTool"/>.</summary>
public record TaskStopInput
{
    /// <summary>The task ID to stop. At least one of <see cref="TaskId"/> or <see cref="ShellId"/> is required.</summary>
    [JsonPropertyName("task_id")]
    public string? TaskId { get; init; }

    /// <summary>The shell ID to stop. At least one of <see cref="TaskId"/> or <see cref="ShellId"/> is required.</summary>
    [JsonPropertyName("shell_id")]
    public string? ShellId { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="TaskStopTool"/>.</summary>
/// <param name="StoppedTaskId">The task ID that was stopped, if one was found and updated.</param>
/// <param name="Message">Human-readable confirmation or error description.</param>
public record TaskStopOutput(string? StoppedTaskId, string Message);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Marks a task as stopped (status: <c>"deleted"</c>) in the shared
/// <see cref="TaskStoreState"/>. Either <c>task_id</c> or <c>shell_id</c> must be provided;
/// if both are given, <c>task_id</c> takes precedence.
/// </summary>
public sealed class TaskStopTool : Tool<TaskStopInput, TaskStopOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            task_id = new { type = "string", description = "The task ID to stop" },
            shell_id = new { type = "string", description = "The shell ID associated with the task to stop" },
        },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "TaskStop";

    /// <inheritdoc/>
    public override string? SearchHint => "stop or cancel a running task";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Marks a task as stopped by setting its status to 'deleted'.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `TaskStop` to cancel or stop a task. " +
            "Provide either `task_id` or `shell_id` (or both — task_id takes precedence). " +
            "The task's status will be set to 'deleted'.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "TaskStop";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;

        if (input.Value.TryGetProperty("task_id", out var id) &&
            id.ValueKind == JsonValueKind.String)
        {
            return $"Stopping task {id.GetString()}";
        }

        return "Stopping task";
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override TaskStopInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<TaskStopInput>(json.GetRawText(), JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize TaskStopInput: result was null.");

    /// <inheritdoc/>
    public override string MapResultToString(TaskStopOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Message;
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        TaskStopInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.TaskId) && string.IsNullOrWhiteSpace(input.ShellId))
            return Task.FromResult(
                ValidationResult.Failure("At least one of task_id or shell_id must be provided."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<TaskStopOutput>> ExecuteAsync(
        TaskStopInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // task_id takes precedence over shell_id.
        var lookupId = !string.IsNullOrWhiteSpace(input.TaskId) ? input.TaskId : input.ShellId!;

        if (!TaskStoreState.Tasks.TryGetValue(lookupId, out var task))
        {
            // No matching task — report gracefully without throwing.
            var notFoundMsg = !string.IsNullOrWhiteSpace(input.TaskId)
                ? $"Task '{input.TaskId}' not found; nothing to stop."
                : $"No task found for shell_id '{input.ShellId}'; nothing to stop.";

            return Task.FromResult(new ToolResult<TaskStopOutput>
            {
                Data = new TaskStopOutput(null, notFoundMsg),
            });
        }

        task.Status = "deleted";

        return Task.FromResult(new ToolResult<TaskStopOutput>
        {
            Data = new TaskStopOutput(task.Id, $"Task {task.Id} stopped (status set to 'deleted')."),
        });
    }
}
