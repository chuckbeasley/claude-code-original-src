namespace ClaudeCode.Tools.RemoteTrigger;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Trigger store — in-process state shared across all RemoteTriggerTool calls
// ---------------------------------------------------------------------------

/// <summary>Represents a stored remote trigger entry.</summary>
/// <param name="Id">Unique identifier assigned at creation.</param>
/// <param name="Body">Arbitrary JSON payload attached to the trigger.</param>
/// <param name="CreatedAt">UTC timestamp when the trigger was created.</param>
/// <param name="UpdatedAt">UTC timestamp of the most recent modification or execution.</param>
public record TriggerInfo(string Id, JsonElement? Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Process-wide in-memory store shared across all <see cref="RemoteTriggerTool"/> calls.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/> and
/// <see cref="Interlocked"/> counter.
/// </summary>
public static class TriggerStore
{
    private static int _nextId;

    /// <summary>All triggers keyed by their string ID.</summary>
    public static ConcurrentDictionary<string, TriggerInfo> Triggers { get; } = new();

    /// <summary>Returns the next unique trigger ID and advances the counter atomically.</summary>
    public static string NextId() => $"trigger-{Interlocked.Increment(ref _nextId)}";
}

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="RemoteTriggerTool"/>.</summary>
public record RemoteTriggerInput
{
    /// <summary>
    /// The action to perform.
    /// Valid values: <c>"list"</c>, <c>"get"</c>, <c>"create"</c>, <c>"update"</c>, <c>"run"</c>.
    /// </summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>Identifier of the remote trigger (required for get/update/run; omit for list/create).</summary>
    [JsonPropertyName("trigger_id")]
    public string? TriggerId { get; init; }

    /// <summary>Optional payload for create or update operations.</summary>
    [JsonPropertyName("body")]
    public JsonElement? Body { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="RemoteTriggerTool"/>.</summary>
/// <param name="Message">Status or result message.</param>
public record RemoteTriggerOutput(string Message);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Manages and invokes local in-memory remote triggers.
/// Supports list, get, create, update, and run actions.
/// Trigger state is stored in <see cref="TriggerStore"/> for the lifetime of the process.
/// </summary>
public sealed class RemoteTriggerTool : Tool<RemoteTriggerInput, RemoteTriggerOutput>
{
    private static readonly string[] ValidActions = ["list", "get", "create", "update", "run"];

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            action     = new { type = "string", @enum = new[] { "list", "get", "create", "update", "run" }, description = "Operation to perform" },
            trigger_id = new { type = "string", description = "Trigger identifier (required for get/update/run)" },
            body       = new { description = "Optional JSON payload for create or update" },
        },
        required = new[] { "action" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "RemoteTrigger";

    /// <inheritdoc/>
    public override string[] Aliases => ["remote_trigger", "trigger"];

    /// <inheritdoc/>
    public override string? SearchHint => "manage and invoke remote automation triggers";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Manages and invokes in-memory remote triggers (list, get, create, update, run). " +
            "Trigger state persists for the lifetime of the current process.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `RemoteTrigger` to interact with in-memory automation triggers. " +
            "Provide `action` (`list`, `get`, `create`, `update`, or `run`) " +
            "and optionally `trigger_id` and a `body` payload.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "RemoteTrigger";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;
        if (input.Value.TryGetProperty("action", out var action) &&
            action.ValueKind == JsonValueKind.String)
        {
            var actionStr = action.GetString() ?? "unknown";
            if (input.Value.TryGetProperty("trigger_id", out var tid) &&
                tid.ValueKind == JsonValueKind.String)
            {
                return $"Remote trigger {actionStr} '{tid.GetString()}'";
            }
            return $"Remote trigger {actionStr}";
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input)
    {
        // "list" and "get" are read-only; others mutate trigger state.
        if (input.TryGetProperty("action", out var action) &&
            action.ValueKind == JsonValueKind.String)
        {
            var actionStr = action.GetString();
            return string.Equals(actionStr, "list", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(actionStr, "get", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        RemoteTriggerInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Action))
            return Task.FromResult(ValidationResult.Failure("action must not be empty."));

        if (!Array.Exists(ValidActions, a => string.Equals(a, input.Action, StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult(ValidationResult.Failure(
                $"action must be one of: {string.Join(", ", ValidActions)}."));

        // trigger_id required for get, update, run
        var action = input.Action.ToLowerInvariant();
        if ((action is "get" or "update" or "run") && string.IsNullOrWhiteSpace(input.TriggerId))
            return Task.FromResult(ValidationResult.Failure(
                $"trigger_id is required for action '{action}'."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override RemoteTriggerInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<RemoteTriggerInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize RemoteTriggerInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(RemoteTriggerOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Message;
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<RemoteTriggerOutput>> ExecuteAsync(
        RemoteTriggerInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        return input.Action.ToLowerInvariant() switch
        {
            "list"   => ListTriggers(),
            "get"    => GetTrigger(input.TriggerId!),
            "create" => CreateTrigger(input.Body),
            "update" => UpdateTrigger(input.TriggerId!, input.Body),
            "run"    => await RunTriggerAsync(input.TriggerId!, ct).ConfigureAwait(false),
            _        => new ToolResult<RemoteTriggerOutput>
                        {
                            Data = new RemoteTriggerOutput($"Unknown action: {input.Action}"),
                        },
        };
    }

    // -----------------------------------------------------------------------
    // Private action handlers
    // -----------------------------------------------------------------------

    /// <summary>Lists all triggers ordered by creation time.</summary>
    private static ToolResult<RemoteTriggerOutput> ListTriggers()
    {
        var triggers = TriggerStore.Triggers.Values
            .OrderBy(t => t.CreatedAt)
            .ToList();

        if (triggers.Count == 0)
            return new ToolResult<RemoteTriggerOutput> { Data = new RemoteTriggerOutput("No triggers configured.") };

        var sb = new StringBuilder();
        foreach (var t in triggers)
            sb.AppendLine($"- {t.Id} (created: {t.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC)");

        return new ToolResult<RemoteTriggerOutput> { Data = new RemoteTriggerOutput(sb.ToString().TrimEnd()) };
    }

    /// <summary>Returns the details of a single trigger by ID.</summary>
    private static ToolResult<RemoteTriggerOutput> GetTrigger(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (!TriggerStore.Triggers.TryGetValue(id, out var trigger))
            return new ToolResult<RemoteTriggerOutput> { Data = new RemoteTriggerOutput($"Trigger '{id}' not found.") };

        var bodyText = trigger.Body.HasValue ? trigger.Body.Value.GetRawText() : "null";
        var message = $"Id:      {trigger.Id}\n" +
                      $"Created: {trigger.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC\n" +
                      $"Updated: {trigger.UpdatedAt:yyyy-MM-dd HH:mm:ss} UTC\n" +
                      $"Body:    {bodyText}";

        return new ToolResult<RemoteTriggerOutput> { Data = new RemoteTriggerOutput(message) };
    }

    /// <summary>Creates a new trigger with an auto-assigned ID.</summary>
    private static ToolResult<RemoteTriggerOutput> CreateTrigger(JsonElement? body)
    {
        var id = TriggerStore.NextId();
        var now = DateTimeOffset.UtcNow;
        TriggerStore.Triggers[id] = new TriggerInfo(id, body, now, now);
        return new ToolResult<RemoteTriggerOutput> { Data = new RemoteTriggerOutput($"Trigger created: {id}") };
    }

    /// <summary>Updates the body of an existing trigger.</summary>
    private static ToolResult<RemoteTriggerOutput> UpdateTrigger(string id, JsonElement? body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (!TriggerStore.Triggers.TryGetValue(id, out var existing))
            return new ToolResult<RemoteTriggerOutput> { Data = new RemoteTriggerOutput($"Trigger '{id}' not found.") };

        TriggerStore.Triggers[id] = existing with { Body = body, UpdatedAt = DateTimeOffset.UtcNow };
        return new ToolResult<RemoteTriggerOutput> { Data = new RemoteTriggerOutput($"Trigger '{id}' updated.") };
    }

    /// <summary>
    /// Executes a trigger by recording its last-run timestamp.
    /// Returns the trigger ID and its body payload as confirmation.
    /// </summary>
    private static Task<ToolResult<RemoteTriggerOutput>> RunTriggerAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ct.ThrowIfCancellationRequested();

        if (!TriggerStore.Triggers.TryGetValue(id, out var trigger))
        {
            return Task.FromResult(new ToolResult<RemoteTriggerOutput>
            {
                Data = new RemoteTriggerOutput($"Trigger '{id}' not found."),
            });
        }

        // Record execution by updating the timestamp.
        TriggerStore.Triggers[id] = trigger with { UpdatedAt = DateTimeOffset.UtcNow };

        var bodyText = trigger.Body.HasValue ? trigger.Body.Value.GetRawText() : "null";
        return Task.FromResult(new ToolResult<RemoteTriggerOutput>
        {
            Data = new RemoteTriggerOutput($"Trigger '{id}' executed. Body: {bodyText}"),
        });
    }
}
