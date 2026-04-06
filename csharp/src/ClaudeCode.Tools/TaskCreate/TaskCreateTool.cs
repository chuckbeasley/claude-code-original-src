namespace ClaudeCode.Tools.TaskCreate;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;
using ClaudeCode.Tools.TaskStore;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="TaskCreateTool"/>.</summary>
public record TaskCreateInput
{
    /// <summary>Short human-readable title for the task (required).</summary>
    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    /// <summary>Extended description of the work to be done (required).</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>Optional UI form name to associate with the task.</summary>
    [JsonPropertyName("activeForm")]
    public string? ActiveForm { get; init; }

    /// <summary>Arbitrary caller-supplied metadata.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="TaskCreateTool"/>.</summary>
/// <param name="TaskId">The auto-generated identifier of the newly created task.</param>
/// <param name="Subject">The subject that was stored.</param>
public record TaskCreateOutput(string TaskId, string Subject);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Creates a new task in the shared in-memory <see cref="TaskStoreState"/> and returns
/// the assigned ID and subject.
/// </summary>
public sealed class TaskCreateTool : Tool<TaskCreateInput, TaskCreateOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            subject = new { type = "string", description = "Short human-readable title for the task" },
            description = new { type = "string", description = "Extended description of the work to be done" },
            activeForm = new { type = "string", description = "Optional UI form name to associate with the task" },
            metadata = new
            {
                type = "object",
                description = "Arbitrary key-value metadata",
                additionalProperties = true,
            },
        },
        required = new[] { "subject", "description" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "TaskCreate";

    /// <inheritdoc/>
    public override string? SearchHint => "create a new tracked task";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Creates a new task in the task store and returns its ID and subject.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `TaskCreate` to create a new tracked task. " +
            "Provide a `subject` (short title) and `description` (detailed body). " +
            "Optionally supply `activeForm` and `metadata`. " +
            "The tool returns the assigned task ID which you can use with TaskUpdate and TaskGet.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "TaskCreate";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;

        if (input.Value.TryGetProperty("subject", out var subj) &&
            subj.ValueKind == JsonValueKind.String)
        {
            return $"Creating task: {subj.GetString()}";
        }

        return "Creating task";
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override TaskCreateInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<TaskCreateInput>(json.GetRawText(), JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize TaskCreateInput: result was null.");

    /// <inheritdoc/>
    public override string MapResultToString(TaskCreateOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"Task created. ID: {result.TaskId}, Subject: {result.Subject}";
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        TaskCreateInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Subject))
            return Task.FromResult(ValidationResult.Failure("subject must not be empty or whitespace."));

        if (string.IsNullOrWhiteSpace(input.Description))
            return Task.FromResult(ValidationResult.Failure("description must not be empty or whitespace."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<TaskCreateOutput>> ExecuteAsync(
        TaskCreateInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        var id = TaskStoreState.NextId();

        var item = new TaskItem
        {
            Id = id,
            Subject = input.Subject,
            Description = input.Description,
            ActiveForm = input.ActiveForm,
            Metadata = input.Metadata is not null
                ? new Dictionary<string, JsonElement>(input.Metadata)
                : null,
        };

        TaskStoreState.Tasks[id] = item;

        return Task.FromResult(new ToolResult<TaskCreateOutput>
        {
            Data = new TaskCreateOutput(id, input.Subject),
        });
    }
}
