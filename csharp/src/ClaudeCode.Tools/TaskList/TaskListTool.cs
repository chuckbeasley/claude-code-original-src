namespace ClaudeCode.Tools.TaskList;

using System.Text;
using System.Text.Json;
using ClaudeCode.Core.Tools;
using ClaudeCode.Tools.TaskStore;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>
/// Empty input record for <see cref="TaskListTool"/>.
/// The tool takes no parameters.
/// </summary>
public record TaskListInput;

/// <summary>Strongly-typed output for the <see cref="TaskListTool"/>.</summary>
/// <param name="Tasks">Snapshot of all tasks at the time of the call.</param>
public record TaskListOutput(IReadOnlyList<TaskItem> Tasks);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Returns a formatted list of all tasks currently held in the shared
/// <see cref="TaskStoreState"/>. This tool is read-only and has no side effects.
/// </summary>
public sealed class TaskListTool : Tool<TaskListInput, TaskListOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "TaskList";

    /// <inheritdoc/>
    public override string? SearchHint => "list all tasks";

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
        => Task.FromResult("Returns all tasks in the task store as a formatted list.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `TaskList` to retrieve all tracked tasks. " +
            "No input parameters are required. " +
            "Each task entry shows its ID, subject, status, and dependency information.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "TaskList";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null) => "Listing all tasks";

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override TaskListInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<TaskListInput>(json.GetRawText(), JsonOpts)
            ?? new TaskListInput();

    /// <inheritdoc/>
    public override string MapResultToString(TaskListOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Tasks.Count == 0)
            return "No tasks found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Tasks ({result.Tasks.Count} total):");
        sb.AppendLine();

        foreach (var task in result.Tasks)
        {
            sb.AppendLine($"#{task.Id} [{task.Status}] {task.Subject}");

            if (!string.IsNullOrEmpty(task.Description))
                sb.AppendLine($"  Description: {task.Description}");

            if (!string.IsNullOrEmpty(task.Owner))
                sb.AppendLine($"  Owner: {task.Owner}");

            if (task.Blocks.Count > 0)
                sb.AppendLine($"  Blocks: {string.Join(", ", task.Blocks)}");

            if (task.BlockedBy.Count > 0)
                sb.AppendLine($"  Blocked by: {string.Join(", ", task.BlockedBy)}");
        }

        return sb.ToString().TrimEnd();
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<TaskListOutput>> ExecuteAsync(
        TaskListInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // Take a consistent snapshot — order by numeric ID for stable output.
        var tasks = TaskStoreState.Tasks.Values
            .OrderBy(t => int.TryParse(t.Id, out var n) ? n : int.MaxValue)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(new ToolResult<TaskListOutput>
        {
            Data = new TaskListOutput(tasks),
        });
    }
}
