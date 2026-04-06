namespace ClaudeCode.Tools.Brief;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for <see cref="BriefTool"/>.</summary>
public record BriefInput
{
    /// <summary>The message body to deliver to the user.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// The status/mode of the message. Valid values are <c>"normal"</c> and <c>"proactive"</c>.
    /// <list type="bullet">
    ///   <item><description><c>"normal"</c> — a direct response to user action.</description></item>
    ///   <item><description><c>"proactive"</c> — unsolicited information surfaced by the agent.</description></item>
    /// </list>
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>
    /// Optional list of file paths or resource URIs to attach to the message.
    /// The UI layer is responsible for resolving and rendering these attachments.
    /// </summary>
    [JsonPropertyName("attachments")]
    public string[]? Attachments { get; init; }
}

/// <summary>Strongly-typed output for <see cref="BriefTool"/>.</summary>
/// <param name="Message">The message content that was sent.</param>
/// <param name="Status">The status/mode used for delivery.</param>
/// <param name="AttachmentCount">The number of attachments included.</param>
public record BriefOutput(string Message, string Status, int AttachmentCount);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Sends a message to the user from the agent.
/// In the current implementation the message content is returned directly so
/// that the orchestration layer can render it. Actual UI rendering (status
/// badges, attachment previews, etc.) is handled at the CLI/UI layer.
/// </summary>
public sealed class BriefTool : Tool<BriefInput, BriefOutput>
{
    private static readonly string[] ValidStatuses = ["normal", "proactive"];

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            message = new { type = "string", description = "The message body to display to the user." },
            status  = new
            {
                type = "string",
                @enum = new[] { "normal", "proactive" },
                description = "Message delivery mode. 'normal' for direct responses; 'proactive' for unsolicited updates.",
            },
            attachments = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Optional list of file paths or resource URIs to attach.",
            },
        },
        required = new[] { "message", "status" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "SendUserMessage";

    /// <inheritdoc/>
    public override string? SearchHint => "send a message to the user from the agent";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Sends a message to the user. Requires `message` (the content) and `status` " +
            "(`\"normal\"` for direct responses or `\"proactive\"` for unsolicited updates). " +
            "Optional `attachments` is a list of file paths or URIs.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `SendUserMessage` to communicate information to the user. " +
            "Set `status` to `\"normal\"` for responses to user requests, or `\"proactive\"` when " +
            "surfacing information the user did not explicitly request. " +
            "Optionally include `attachments` (file paths or URIs) for supporting context.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "SendUserMessage";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null)
            return null;

        if (input.Value.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String &&
            status.GetString() == "proactive")
        {
            return "Sending proactive message to user";
        }

        return "Sending message to user";
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override BriefInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<BriefInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialise BriefInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(BriefOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.AttachmentCount > 0)
            return $"[{result.Status}] {result.Message}\n({result.AttachmentCount} attachment(s) included)";

        return $"[{result.Status}] {result.Message}";
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        BriefInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Message))
            return Task.FromResult(ValidationResult.Failure("The 'message' field must not be empty or whitespace."));

        if (string.IsNullOrWhiteSpace(input.Status))
            return Task.FromResult(ValidationResult.Failure("The 'status' field must not be empty or whitespace."));

        if (!ValidStatuses.Contains(input.Status, StringComparer.OrdinalIgnoreCase))
            return Task.FromResult(
                ValidationResult.Failure($"The 'status' field must be one of: {string.Join(", ", ValidStatuses)}."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<BriefOutput>> ExecuteAsync(
        BriefInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        var attachmentCount = input.Attachments?.Length ?? 0;
        var output = new BriefOutput(input.Message, input.Status, attachmentCount);

        return Task.FromResult(new ToolResult<BriefOutput> { Data = output });
    }
}
