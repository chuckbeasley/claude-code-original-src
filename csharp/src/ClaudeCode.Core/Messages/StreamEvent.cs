namespace ClaudeCode.Core.Messages;

using System.Text.Json.Serialization;

/// <summary>
/// Discriminated union for server-sent events emitted during a streaming API response.
/// The <c>type</c> field on the wire drives polymorphic deserialization.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MessageStartEvent),       "message_start")]
[JsonDerivedType(typeof(ContentBlockStartEvent),  "content_block_start")]
[JsonDerivedType(typeof(ContentBlockDeltaEvent),  "content_block_delta")]
[JsonDerivedType(typeof(ContentBlockStopEvent),   "content_block_stop")]
[JsonDerivedType(typeof(MessageDeltaEvent),       "message_delta")]
[JsonDerivedType(typeof(MessageStopEvent),        "message_stop")]
public abstract record StreamEvent;

/// <summary>First event in a stream; carries the initial (empty) assistant message skeleton.</summary>
public record MessageStartEvent(
    [property: JsonPropertyName("message")] AssistantMessage Message
) : StreamEvent;

/// <summary>Signals that a new content block at <see cref="Index"/> has begun.</summary>
public record ContentBlockStartEvent(
    [property: JsonPropertyName("index")]         int Index,
    [property: JsonPropertyName("content_block")] ContentBlock ContentBlock
) : StreamEvent;

/// <summary>Carries an incremental delta for the content block at <see cref="Index"/>.</summary>
public record ContentBlockDeltaEvent(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] ContentBlockDelta Delta
) : StreamEvent;

/// <summary>Signals that the content block at <see cref="Index"/> is complete.</summary>
public record ContentBlockStopEvent(
    [property: JsonPropertyName("index")] int Index
) : StreamEvent;

/// <summary>
/// Carries a message-level delta (e.g. <c>stop_reason</c>) and cumulative
/// <see cref="Usage"/> statistics at the point of emission.
/// </summary>
public record MessageDeltaEvent(
    [property: JsonPropertyName("delta")] MessageDelta Delta,
    [property: JsonPropertyName("usage")] UsageInfo? Usage
) : StreamEvent;

/// <summary>Final event in a stream; signals that the full message has been received.</summary>
public record MessageStopEvent : StreamEvent;

// ---------------------------------------------------------------------------
// Content block delta types
// ---------------------------------------------------------------------------

/// <summary>
/// Discriminated union for incremental updates to a single content block.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextDelta),      "text_delta")]
[JsonDerivedType(typeof(InputJsonDelta), "input_json_delta")]
[JsonDerivedType(typeof(ThinkingDelta),  "thinking_delta")]
public abstract record ContentBlockDelta;

/// <summary>Incremental text appended to a <see cref="TextBlock"/>.</summary>
public record TextDelta(
    [property: JsonPropertyName("text")] string Text
) : ContentBlockDelta;

/// <summary>Incremental JSON fragment for a <see cref="ToolUseBlock"/> input being streamed.</summary>
public record InputJsonDelta(
    [property: JsonPropertyName("partial_json")] string PartialJson
) : ContentBlockDelta;

/// <summary>Incremental thinking text appended to a <see cref="ThinkingBlock"/>.</summary>
public record ThinkingDelta(
    [property: JsonPropertyName("thinking")] string Thinking
) : ContentBlockDelta;

// ---------------------------------------------------------------------------
// Message-level delta
// ---------------------------------------------------------------------------

/// <summary>Message-level changes emitted before the stream closes.</summary>
public record MessageDelta
{
    /// <summary>
    /// Reason the model finished generating, e.g. <c>"end_turn"</c>,
    /// <c>"tool_use"</c>, or <c>"max_tokens"</c>.
    /// </summary>
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }
}
