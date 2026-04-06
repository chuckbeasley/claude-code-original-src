namespace ClaudeCode.Tools.TaskGet;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;
using ClaudeCode.Tools.TaskStore;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="TaskGetTool"/>.</summary>
public record TaskGetInput
{
    /// <summary>The ID of the task to retrieve (required).</summary>
    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="TaskGetTool"/>.</summary>
/// <param name="Task">The full task item, or <see langword="null"/> when not found.</param>
public record TaskGetOutput(TaskItem? Task);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Retrieves the full details of a single task from the shared
/// <see cref="TaskStoreState"/> by its ID. This tool is read-only.
/// </summary>
public sealed class TaskGetTool : Tool<TaskGetInput, TaskGetOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            taskId = new { type = "string", description = "The ID of the task to retrieve" },
        },
        required = new[] { "taskId" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "TaskGet";

    /// <inheritdoc/>
    public override string? SearchHint => "retrieve details of a specific task";

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
        => Task.FromResult("Returns the full details of a single task identified by its ID.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `TaskGet` to inspect a specific task in detail. " +
            "Provide the `taskId` returned by TaskCreate or visible in TaskList output. " +
            "Returns all fields including description, status, owner, and dependency lists.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "TaskGet";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;

        if (input.Value.TryGetProperty("taskId", out var id) &&
            id.ValueKind == JsonValueKind.String)
        {
            return $"Getting task {id.GetString()}";
        }

        return "Getting task";
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override TaskGetInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<TaskGetInput>(json.GetRawText(), JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize TaskGetInput: result was null.");

    /// <inheritdoc/>
    public override string MapResultToString(TaskGetOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Task is null)
            return "Task not found.";

        var task = result.Task;
        var sb = new StringBuilder();

        sb.AppendLine($"Task #{task.Id}");
        sb.AppendLine($"  Subject:     {task.Subject}");
        sb.AppendLine($"  Status:      {task.Status}");

        if (!string.IsNullOrEmpty(task.Description))
            sb.AppendLine($"  Description: {task.Description}");

        if (!string.IsNullOrEmpty(task.ActiveForm))
            sb.AppendLine($"  ActiveForm:  {task.ActiveForm}");

        if (!string.IsNullOrEmpty(task.Owner))
            sb.AppendLine($"  Owner:       {task.Owner}");

        if (task.Blocks.Count > 0)
            sb.AppendLine($"  Blocks:      {string.Join(", ", task.Blocks)}");

        if (task.BlockedBy.Count > 0)
            sb.AppendLine($"  Blocked by:  {string.Join(", ", task.BlockedBy)}");

        if (task.Metadata is { Count: > 0 })
            sb.AppendLine($"  Metadata:    {JsonSerializer.Serialize(task.Metadata)}");

        return sb.ToString().TrimEnd();
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        TaskGetInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.TaskId))
            return Task.FromResult(ValidationResult.Failure("taskId must not be empty or whitespace."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<TaskGetOutput>> ExecuteAsync(
        TaskGetInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        TaskStoreState.Tasks.TryGetValue(input.TaskId, out var task);

        return Task.FromResult(new ToolResult<TaskGetOutput>
        {
            Data = new TaskGetOutput(task),
        });
    }
}
