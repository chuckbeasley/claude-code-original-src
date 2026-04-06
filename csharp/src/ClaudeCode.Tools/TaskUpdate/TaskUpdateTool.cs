namespace ClaudeCode.Tools.TaskUpdate;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;
using ClaudeCode.Tools.TaskStore;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="TaskUpdateTool"/>.</summary>
public record TaskUpdateInput
{
    /// <summary>The ID of the task to update (required).</summary>
    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }

    /// <summary>If set, replaces the task's subject.</summary>
    [JsonPropertyName("subject")]
    public string? Subject { get; init; }

    /// <summary>If set, replaces the task's description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>If set, replaces the task's active form.</summary>
    [JsonPropertyName("activeForm")]
    public string? ActiveForm { get; init; }

    /// <summary>
    /// If set, transitions the task to this status.
    /// Valid values: <c>"pending"</c>, <c>"in_progress"</c>, <c>"completed"</c>, <c>"deleted"</c>.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>If set, assigns the task to this owner.</summary>
    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    /// <summary>Task IDs to append to the Blocks list.</summary>
    [JsonPropertyName("addBlocks")]
    public string[]? AddBlocks { get; init; }

    /// <summary>Task IDs to append to the BlockedBy list.</summary>
    [JsonPropertyName("addBlockedBy")]
    public string[]? AddBlockedBy { get; init; }

    /// <summary>If set, merges these entries into the task's metadata dictionary.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="TaskUpdateTool"/>.</summary>
/// <param name="TaskId">The ID of the updated task.</param>
/// <param name="Subject">The subject after the update.</param>
/// <param name="Status">The status after the update.</param>
public record TaskUpdateOutput(string TaskId, string Subject, string Status);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Updates an existing task in the shared in-memory <see cref="TaskStoreState"/>.
/// Applies only the fields that are non-null in the input; all others are left unchanged.
/// </summary>
public sealed class TaskUpdateTool : Tool<TaskUpdateInput, TaskUpdateOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly HashSet<string> ValidStatuses =
        ["pending", "in_progress", "completed", "deleted"];

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            taskId = new { type = "string", description = "The ID of the task to update" },
            subject = new { type = "string", description = "New subject / title" },
            description = new { type = "string", description = "New description" },
            activeForm = new { type = "string", description = "New active form name" },
            status = new
            {
                type = "string",
                description = "New lifecycle status",
                @enum = new[] { "pending", "in_progress", "completed", "deleted" },
            },
            owner = new { type = "string", description = "Owner / assignee identifier" },
            addBlocks = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Task IDs to add to the Blocks list",
            },
            addBlockedBy = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Task IDs to add to the BlockedBy list",
            },
            metadata = new
            {
                type = "object",
                description = "Metadata entries to merge",
                additionalProperties = true,
            },
        },
        required = new[] { "taskId" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "TaskUpdate";

    /// <inheritdoc/>
    public override string? SearchHint => "update an existing task";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Updates fields on an existing task identified by taskId.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `TaskUpdate` to modify an existing task. " +
            "Supply `taskId` and any combination of optional fields to update. " +
            "Valid status values are: pending, in_progress, completed, deleted. " +
            "Use `addBlocks` and `addBlockedBy` to record task dependencies.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "TaskUpdate";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;

        if (input.Value.TryGetProperty("taskId", out var id) &&
            id.ValueKind == JsonValueKind.String)
        {
            return $"Updating task {id.GetString()}";
        }

        return "Updating task";
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override TaskUpdateInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<TaskUpdateInput>(json.GetRawText(), JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize TaskUpdateInput: result was null.");

    /// <inheritdoc/>
    public override string MapResultToString(TaskUpdateOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"Task {result.TaskId} updated. Subject: {result.Subject}, Status: {result.Status}";
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        TaskUpdateInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.TaskId))
            return Task.FromResult(ValidationResult.Failure("taskId must not be empty or whitespace."));

        if (input.Status is not null && !ValidStatuses.Contains(input.Status))
            return Task.FromResult(ValidationResult.Failure(
                $"Invalid status '{input.Status}'. Valid values: {string.Join(", ", ValidStatuses)}."));

        if (!TaskStoreState.Tasks.ContainsKey(input.TaskId))
            return Task.FromResult(ValidationResult.Failure($"Task '{input.TaskId}' not found."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<TaskUpdateOutput>> ExecuteAsync(
        TaskUpdateInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        if (!TaskStoreState.Tasks.TryGetValue(input.TaskId, out var task))
            throw new InvalidOperationException($"Task '{input.TaskId}' not found (should have been caught in validation).");

        // Apply partial updates — only touch fields explicitly supplied.
        if (input.Subject is not null)
            task.Subject = input.Subject;

        if (input.Description is not null)
            task.Description = input.Description;

        if (input.ActiveForm is not null)
            task.ActiveForm = input.ActiveForm;

        if (input.Status is not null)
            task.Status = input.Status;

        if (input.Owner is not null)
            task.Owner = input.Owner;

        if (input.AddBlocks is { Length: > 0 })
        {
            foreach (var dep in input.AddBlocks)
            {
                if (!task.Blocks.Contains(dep, StringComparer.Ordinal))
                    task.Blocks.Add(dep);
            }
        }

        if (input.AddBlockedBy is { Length: > 0 })
        {
            foreach (var dep in input.AddBlockedBy)
            {
                if (!task.BlockedBy.Contains(dep, StringComparer.Ordinal))
                    task.BlockedBy.Add(dep);
            }
        }

        if (input.Metadata is not null)
        {
            task.Metadata ??= [];
            foreach (var kv in input.Metadata)
                task.Metadata[kv.Key] = kv.Value;
        }

        return Task.FromResult(new ToolResult<TaskUpdateOutput>
        {
            Data = new TaskUpdateOutput(task.Id, task.Subject, task.Status),
        });
    }
}
