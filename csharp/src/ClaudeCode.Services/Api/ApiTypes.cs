namespace ClaudeCode.Services.Api;

using System.Text.Json;
using System.Text.Json.Serialization;

// === REQUEST TYPES ===

/// <summary>
/// Top-level request body for POST /v1/messages.
/// </summary>
public record MessageRequest
{
    /// <summary>The model identifier to invoke, e.g. "claude-sonnet-4-6".</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>Ordered list of conversation turns to send to the model.</summary>
    [JsonPropertyName("messages")]
    public required List<MessageParam> Messages { get; init; }

    /// <summary>Optional system prompt blocks, supporting cache control.</summary>
    [JsonPropertyName("system")]
    public List<SystemBlock>? System { get; init; }

    /// <summary>Tool definitions made available to the model.</summary>
    [JsonPropertyName("tools")]
    public List<ToolDefinition>? Tools { get; init; }

    /// <summary>Controls how the model selects tools.</summary>
    [JsonPropertyName("tool_choice")]
    public ToolChoice? ToolChoice { get; init; }

    /// <summary>Maximum number of tokens to generate in the response.</summary>
    [JsonPropertyName("max_tokens")]
    public required int MaxTokens { get; init; }

    /// <summary>Sampling temperature in [0, 1]. Omitted when null.</summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    /// <summary>Whether to stream the response as SSE. Defaults to <see langword="true"/>.</summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; init; } = true;

    /// <summary>Extended thinking configuration. Omitted when null.</summary>
    [JsonPropertyName("thinking")]
    public ThinkingConfig? Thinking { get; init; }

    /// <summary>Optional request metadata such as a user identifier.</summary>
    [JsonPropertyName("metadata")]
    public RequestMetadata? Metadata { get; init; }
}

/// <summary>
/// A single conversation turn, either "user" or "assistant".
/// <see cref="Content"/> is a <see cref="JsonElement"/> because the API accepts
/// either a plain string or an array of content blocks.
/// </summary>
public record MessageParam
{
    /// <summary>"user" or "assistant".</summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    /// <summary>String or array of content-block objects.</summary>
    [JsonPropertyName("content")]
    public required JsonElement Content { get; init; }
}

/// <summary>
/// A typed system-prompt block with optional prompt-caching control.
/// </summary>
public record SystemBlock
{
    /// <summary>Always "text" for system blocks.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    /// <summary>The system prompt text.</summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>Cache-control directive. Omitted when null.</summary>
    [JsonPropertyName("cache_control")]
    public CacheControl? CacheControl { get; init; }
}

/// <summary>
/// Anthropic prompt-caching control directive attached to a content block.
/// </summary>
public record CacheControl
{
    /// <summary>Cache lifetime policy; currently only "ephemeral" is supported.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "ephemeral";
}

/// <summary>
/// Defines a tool the model may invoke, including its JSON Schema input description.
/// </summary>
public record ToolDefinition
{
    /// <summary>Unique tool name used by the model when calling the tool.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Natural-language description of the tool's purpose and behaviour.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>JSON Schema object describing the tool's accepted input parameters.</summary>
    [JsonPropertyName("input_schema")]
    public required JsonElement InputSchema { get; init; }
}

/// <summary>
/// Controls which tool, if any, the model must use for its next turn.
/// </summary>
public record ToolChoice
{
    /// <summary>"auto", "any", or "tool".</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Required when <see cref="Type"/> is "tool"; names the specific tool to call.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>
/// Configures the extended-thinking feature for a request.
/// </summary>
public record ThinkingConfig
{
    /// <summary>"enabled" or "disabled".</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Maximum tokens the model may use for internal reasoning. Omitted when null.</summary>
    [JsonPropertyName("budget_tokens")]
    public int? BudgetTokens { get; init; }
}

/// <summary>
/// Optional per-request metadata forwarded to the API.
/// </summary>
public record RequestMetadata
{
    /// <summary>Caller-supplied user identifier for tracking purposes.</summary>
    [JsonPropertyName("user_id")]
    public string? UserId { get; init; }
}

// === RESPONSE TYPES (non-streaming) ===

/// <summary>
/// Top-level response body returned by POST /v1/messages (non-streaming).
/// </summary>
public record MessageResponse
{
    /// <summary>API-assigned message identifier, e.g. "msg_01XFDUDYJgAACTJQHd4xFMqz".</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Always "message".</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "message";

    /// <summary>Always "assistant" for responses.</summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = "assistant";

    /// <summary>
    /// Raw content blocks as <see cref="JsonElement"/> to preserve wire fidelity
    /// before domain deserialization.
    /// </summary>
    [JsonPropertyName("content")]
    public required List<JsonElement> Content { get; init; }

    /// <summary>Model identifier that produced this response.</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>
    /// Reason the model stopped: "end_turn", "tool_use", "max_tokens", or null
    /// when the message is still being streamed.
    /// </summary>
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    /// <summary>Token usage for this response. May be null for partial stream events.</summary>
    [JsonPropertyName("usage")]
    public ApiUsage? Usage { get; init; }
}

/// <summary>
/// Token usage counters returned directly by the API wire format.
/// Structurally mirrors <c>ClaudeCode.Core.Messages.UsageInfo</c> but lives in the
/// Services layer as an API DTO, keeping the transport contract decoupled from the
/// domain model.
/// </summary>
public record ApiUsage
{
    /// <summary>Tokens in the prompt sent to the model.</summary>
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    /// <summary>Tokens generated by the model.</summary>
    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }

    /// <summary>Tokens served from the prompt cache at the cache-read rate.</summary>
    [JsonPropertyName("cache_read_input_tokens")]
    public int CacheReadInputTokens { get; init; }

    /// <summary>Tokens written into the prompt cache at the cache-write rate.</summary>
    [JsonPropertyName("cache_creation_input_tokens")]
    public int CacheCreationInputTokens { get; init; }
}

// === SSE STREAM EVENT TYPES ===

/// <summary>
/// A parsed server-sent event line pair: the <c>event:</c> type tag and its
/// raw <c>data:</c> JSON payload, before further deserialization.
/// </summary>
public record SseEvent
{
    /// <summary>
    /// The SSE event type, e.g. "message_start", "content_block_delta",
    /// "message_stop", "error", etc.
    /// </summary>
    public required string EventType { get; init; }

    /// <summary>Raw JSON string from the SSE <c>data:</c> field.</summary>
    public required string Data { get; init; }
}

// Parsed stream event payloads

/// <summary>
/// Payload for the "message_start" SSE event; carries the initial message skeleton.
/// </summary>
public record MessageStartPayload
{
    /// <summary>Always "message_start".</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "message_start";

    /// <summary>Initial state of the assistant message with empty content.</summary>
    [JsonPropertyName("message")]
    public required MessageResponse Message { get; init; }
}

/// <summary>
/// Payload for the "content_block_start" SSE event; signals the opening of a new block.
/// </summary>
public record ContentBlockStartPayload
{
    /// <summary>Always "content_block_start".</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "content_block_start";

    /// <summary>Zero-based index of the content block in the response.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The initial content block skeleton (type + empty fields) as raw JSON.</summary>
    [JsonPropertyName("content_block")]
    public required JsonElement ContentBlock { get; init; }
}

/// <summary>
/// Payload for the "content_block_delta" SSE event; carries an incremental chunk.
/// </summary>
public record ContentBlockDeltaPayload
{
    /// <summary>Always "content_block_delta".</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "content_block_delta";

    /// <summary>Zero-based index of the content block this delta belongs to.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The incremental delta (text_delta, input_json_delta, etc.) as raw JSON.</summary>
    [JsonPropertyName("delta")]
    public required JsonElement Delta { get; init; }
}

/// <summary>
/// Payload for the "content_block_stop" SSE event; signals a block is complete.
/// </summary>
public record ContentBlockStopPayload
{
    /// <summary>Always "content_block_stop".</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "content_block_stop";

    /// <summary>Zero-based index of the content block that has finished.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }
}

/// <summary>
/// Payload for the "message_delta" SSE event; carries message-level changes
/// (e.g. stop_reason) and cumulative token usage at stream close.
/// </summary>
public record MessageDeltaPayload
{
    /// <summary>Always "message_delta".</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "message_delta";

    /// <summary>Message-level delta fields (e.g. stop_reason) as raw JSON.</summary>
    [JsonPropertyName("delta")]
    public required JsonElement Delta { get; init; }

    /// <summary>Cumulative token usage at the time of emission. May be null.</summary>
    [JsonPropertyName("usage")]
    public ApiUsage? Usage { get; init; }
}

/// <summary>
/// Payload for the "message_stop" SSE event; signals the stream has ended normally.
/// </summary>
public record MessageStopPayload
{
    /// <summary>Always "message_stop".</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "message_stop";
}

/// <summary>
/// Payload for the "error" SSE event; carries an API error emitted mid-stream.
/// </summary>
public record StreamErrorPayload
{
    /// <summary>Always "error".</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "error";

    /// <summary>Structured error detail returned by the API.</summary>
    [JsonPropertyName("error")]
    public required ApiErrorDetail Error { get; init; }
}

/// <summary>
/// Structured error detail included in API error responses and stream error events.
/// </summary>
public record ApiErrorDetail
{
    /// <summary>Machine-readable error category, e.g. "overloaded_error", "invalid_request_error".</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Human-readable description of the error.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
