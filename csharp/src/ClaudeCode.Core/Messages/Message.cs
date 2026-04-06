namespace ClaudeCode.Core.Messages;

using System.Text.Json.Serialization;

/// <summary>
/// Abstract base for all messages in a conversation turn.
/// Every message carries a stable <see cref="Id"/> and a <see cref="Timestamp"/>
/// assigned at construction time.
/// </summary>
public abstract record Message
{
    /// <summary>Stable, unique identifier for this message instance.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>UTC instant at which this message was created.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A message authored by the human turn of the conversation.</summary>
public record UserMessage(
    [property: JsonPropertyName("content")] IReadOnlyList<ContentBlock> Content
) : Message;

/// <summary>
/// A message produced by the assistant, including its content blocks and
/// optional metadata returned by the API.
/// </summary>
public record AssistantMessage : Message
{
    /// <summary>Ordered list of content blocks that make up the assistant's response.</summary>
    [JsonPropertyName("content")]
    public required IReadOnlyList<ContentBlock> Content { get; init; }

    /// <summary>Token usage reported by the API for this response, if available.</summary>
    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; init; }

    /// <summary>
    /// Reason the model stopped generating, e.g. <c>"end_turn"</c>,
    /// <c>"tool_use"</c>, or <c>"max_tokens"</c>.
    /// </summary>
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    /// <summary>Model identifier that produced this message, e.g. <c>"claude-opus-4-5"</c>.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

/// <summary>A system-level prompt injected before the conversation.</summary>
public record SystemMessage(
    [property: JsonPropertyName("content")] string Content
) : Message;

/// <summary>
/// Synthetic boundary message inserted into the conversation history when the
/// context window is compacted. It replaces the elided messages with a prose
/// summary and records the token savings for telemetry.
/// </summary>
public record CompactBoundaryMessage(
    [property: JsonPropertyName("summary")]               string Summary,
    [property: JsonPropertyName("original_token_count")]  int OriginalTokenCount,
    [property: JsonPropertyName("compacted_token_count")] int CompactedTokenCount
) : Message;
