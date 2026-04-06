namespace ClaudeCode.Tools.SendMessage;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for <see cref="SendMessageTool"/>.</summary>
public record SendMessageInput
{
    /// <summary>The recipient agent or channel identifier.</summary>
    [JsonPropertyName("to")]
    public required string To { get; init; }

    /// <summary>The message body to deliver.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>Optional short summary of the message, suitable for logging.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>
    /// Optional number of milliseconds to block waiting for a reply on a per-message reply channel.
    /// When greater than zero, the tool waits up to this duration for a reply posted to the
    /// channel identified by <c>reply:{messageId}</c> before returning. Zero or absent means
    /// fire-and-forget.
    /// </summary>
    [JsonPropertyName("waitMs")]
    public int? WaitMs { get; init; }
}

/// <summary>Strongly-typed output for <see cref="SendMessageTool"/>.</summary>
/// <param name="MessageId">A unique identifier assigned to the stored message.</param>
/// <param name="Confirmation">Human-readable confirmation text.</param>
public record SendMessageOutput(string MessageId, string Confirmation);

/// <summary>
/// Represents a stored inter-agent message.
/// </summary>
/// <param name="Id">Unique message identifier.</param>
/// <param name="To">The recipient address.</param>
/// <param name="Message">The message body.</param>
/// <param name="Summary">Optional summary text.</param>
/// <param name="SentAt">UTC timestamp of when the message was stored.</param>
public record StoredMessage(string Id, string To, string Message, string? Summary, DateTimeOffset SentAt);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Sends a message to another agent or channel.
/// Messages are dispatched via <see cref="AgentMessageBus"/> for real in-process delivery,
/// and also recorded in <see cref="SentMessages"/> as a history log.
/// </summary>
public sealed class SendMessageTool : Tool<SendMessageInput, SendMessageOutput>
{
    /// <summary>
    /// In-process message history log. Thread-safe via <see cref="ConcurrentBag{T}"/>.
    /// </summary>
    public static ConcurrentBag<StoredMessage> SentMessages { get; } = [];

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            to      = new { type = "string", description = "The recipient agent or channel identifier." },
            message = new { type = "string", description = "The message body to deliver." },
            summary = new { type = "string", description = "Optional short summary for logging purposes." },
            waitMs  = new { type = "integer", description = "Optional milliseconds to wait for a reply before returning. Zero means fire-and-forget." },
        },
        required = new[] { "to", "message" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "SendMessage";

    /// <inheritdoc/>
    public override string? SearchHint => "send a message to another agent";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Sends a message to another agent or channel. " +
            "Requires a recipient (`to`) and a `message` body. " +
            "An optional `summary` can be supplied for log aggregation. " +
            "Set `waitMs` to block until a reply arrives on the per-message reply channel.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `SendMessage` to deliver a message to another agent or channel. " +
            "Provide `to` (recipient identifier) and `message` (content). " +
            "Optionally include `summary` for a short log-friendly description. " +
            "Set `waitMs` to wait up to N milliseconds for a reply on the `reply:{messageId}` channel. " +
            "The tool returns a `messageId` and a confirmation string.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "SendMessage";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null)
            return null;

        if (input.Value.TryGetProperty("to", out var to) &&
            to.ValueKind == JsonValueKind.String)
        {
            return $"Sending message to {to.GetString()}";
        }

        return "Sending message";
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override SendMessageInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<SendMessageInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialise SendMessageInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(SendMessageOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"Message sent. ID: {result.MessageId}\n{result.Confirmation}";
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        SendMessageInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.To))
            return Task.FromResult(ValidationResult.Failure("The 'to' field must not be empty or whitespace."));

        if (string.IsNullOrWhiteSpace(input.Message))
            return Task.FromResult(ValidationResult.Failure("The 'message' field must not be empty or whitespace."));

        if (input.WaitMs.HasValue && input.WaitMs.Value < 0)
            return Task.FromResult(ValidationResult.Failure("The 'waitMs' field must be zero or positive."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<SendMessageOutput>> ExecuteAsync(
        SendMessageInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        var id = Guid.NewGuid().ToString("N");
        var stored = new StoredMessage(id, input.To, input.Message, input.Summary, DateTimeOffset.UtcNow);

        // Record in the in-session history log.
        SentMessages.Add(stored);

        // Dispatch to the named agent's channel for real inter-agent delivery.
        AgentMessageBus.Post(input.To, input.Message);

        string confirmation;
        if (input.WaitMs is int waitMs && waitMs > 0)
        {
            // Wait for a reply on the per-message reply channel.
            // The recipient posts a reply to "reply:{id}" to signal completion.
            var replyChannelId = $"reply:{id}";
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(waitMs);
            try
            {
                var reply = await AgentMessageBus.WaitNextAsync(replyChannelId, timeoutCts.Token)
                    .ConfigureAwait(false);
                confirmation = $"Message sent to '{input.To}'. Reply received: {reply}";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timed out — no reply arrived within the wait window.
                confirmation = $"Message sent to '{input.To}'. No reply within {waitMs}ms.";
            }
        }
        else
        {
            confirmation = $"Message sent to '{input.To}'.";
        }

        var output = new SendMessageOutput(id, confirmation);
        return new ToolResult<SendMessageOutput> { Data = output };
    }

    // -----------------------------------------------------------------------
    // Message inspection helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns all messages currently pending in <paramref name="agentId"/>'s queue
    /// without blocking. The messages are consumed (drained) from the queue.
    /// Returns an empty list when no messages are queued.
    /// </summary>
    /// <param name="agentId">The agent identifier whose queue to drain.</param>
    /// <returns>All immediately available messages; never <see langword="null"/>.</returns>
    public static IReadOnlyList<string> GetPendingMessages(string agentId) =>
        AgentMessageBus.DrainMessages(agentId);
}
