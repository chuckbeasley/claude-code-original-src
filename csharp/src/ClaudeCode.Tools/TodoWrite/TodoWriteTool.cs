namespace ClaudeCode.Tools.TodoWrite;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;
using ClaudeCode.Tools.TaskStore;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="TodoWriteTool"/>.</summary>
public record TodoWriteInput
{
    /// <summary>
    /// The todo list to store. Expected to be a JSON array of todo items,
    /// but accepted as a raw <see cref="JsonElement"/> to remain schema-agnostic.
    /// </summary>
    [JsonPropertyName("todos")]
    public required JsonElement Todos { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="TodoWriteTool"/>.</summary>
/// <param name="ItemCount">
/// Number of items in the stored list, or -1 when the value is not a JSON array.
/// </param>
public record TodoWriteOutput(int ItemCount);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Stores a todo list in the shared <see cref="TaskStoreState.TodoList"/> field.
/// Accepts any JSON array of todo items and overwrites any previously stored list.
/// </summary>
public sealed class TodoWriteTool : Tool<TodoWriteInput, TodoWriteOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            todos = new
            {
                type = "array",
                description = "An array of todo items to store",
                items = new { type = "object" },
            },
        },
        required = new[] { "todos" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "TodoWrite";

    /// <inheritdoc/>
    public override string? SearchHint => "write or replace the current todo list";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Stores a todo list, replacing any previously stored list.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `TodoWrite` to persist a todo list for the current session. " +
            "Pass a JSON array of todo items in the `todos` field. " +
            "Each subsequent call overwrites the previous list.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "TodoWrite";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null) => "Writing todo list";

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override TodoWriteInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<TodoWriteInput>(json.GetRawText(), JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize TodoWriteInput: result was null.");

    /// <inheritdoc/>
    public override string MapResultToString(TodoWriteOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.ItemCount >= 0
            ? $"Todo list stored successfully ({result.ItemCount} item(s))."
            : "Todo list stored successfully.";
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        TodoWriteInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The todos field must be present; null kind indicates it was missing entirely.
        if (input.Todos.ValueKind == JsonValueKind.Undefined)
            return Task.FromResult(ValidationResult.Failure("todos field is required."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<TodoWriteOutput>> ExecuteAsync(
        TodoWriteInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // Store the raw JsonElement. Cloning ensures the value remains valid after
        // the originating JsonDocument is disposed.
        TaskStoreState.TodoList = input.Todos.Clone();

        int itemCount = input.Todos.ValueKind == JsonValueKind.Array
            ? input.Todos.GetArrayLength()
            : -1;

        return Task.FromResult(new ToolResult<TodoWriteOutput>
        {
            Data = new TodoWriteOutput(itemCount),
        });
    }
}
