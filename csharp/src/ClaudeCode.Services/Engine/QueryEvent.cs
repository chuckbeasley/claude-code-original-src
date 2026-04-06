namespace ClaudeCode.Services.Engine;

using ClaudeCode.Core.Messages;
using ClaudeCode.Services.Api;

/// <summary>
/// Discriminated union base for all events emitted by <see cref="QueryEngine"/> during streaming.
/// </summary>
public abstract record QueryEvent;

/// <summary>
/// Emitted for each incremental text content chunk as the model generates its response.
/// </summary>
/// <param name="Text">The text fragment streamed in this delta.</param>
public sealed record TextDeltaEvent(string Text) : QueryEvent;

/// <summary>
/// Emitted for each incremental thinking (extended reasoning) chunk.
/// </summary>
/// <param name="Text">The thinking fragment streamed in this delta.</param>
public sealed record ThinkingDeltaEvent(string Text) : QueryEvent;

/// <summary>
/// Emitted when the model begins invoking a tool.
/// </summary>
/// <param name="Id">The server-assigned unique identifier for this tool-use block.</param>
/// <param name="Name">The name of the tool being invoked.</param>
public sealed record ToolUseStartEvent(string Id, string Name) : QueryEvent;

/// <summary>
/// Emitted for each incremental JSON fragment streamed for a tool's input parameters.
/// Consumers should accumulate these fragments to reconstruct the full input JSON.
/// </summary>
/// <param name="PartialJson">A JSON fragment to be accumulated by the consumer.</param>
public sealed record ToolUseInputDeltaEvent(string PartialJson) : QueryEvent;

/// <summary>
/// Emitted once when the streaming response has completed successfully.
/// </summary>
/// <param name="Usage">Cumulative token usage for the message, or <see langword="null"/> if unavailable.</param>
/// <param name="StopReason">
/// The reason the model stopped generating, e.g. "end_turn" or "tool_use".
/// May be <see langword="null"/> if the API did not provide one.
/// </param>
public sealed record MessageCompleteEvent(UsageInfo? Usage, string? StopReason) : QueryEvent;

/// <summary>
/// Emitted when a stream-level error is received from the API.
/// </summary>
/// <param name="Message">Human-readable description of the error.</param>
public sealed record ErrorEvent(string Message) : QueryEvent;

/// <summary>
/// Emitted after a tool has been executed and its result is ready to be fed back to the model.
/// </summary>
/// <param name="ToolUseId">The server-assigned identifier from the originating <c>tool_use</c> block.</param>
/// <param name="ToolName">The canonical name of the tool that was executed.</param>
/// <param name="Result">The string result returned by the tool.</param>
/// <param name="IsError">
/// <see langword="true"/> when the tool execution failed and <paramref name="Result"/> contains
/// an error description rather than a successful output.
/// </param>
public sealed record ToolResultEvent(string ToolUseId, string ToolName, string Result, bool IsError) : QueryEvent;

/// <summary>
/// Emitted when a compaction operation starts (either auto-triggered or user-invoked via /compact),
/// so the renderer can display a status message before the API call completes.
/// </summary>
/// <param name="Message">Human-readable description of the compaction action in progress.</param>
public sealed record CompactEvent(string Message) : QueryEvent;

/// <summary>
/// Emitted once per API response when rate-limit response headers are present.
/// </summary>
public sealed record RateLimitHeadersEvent(RateLimitInfo Info) : QueryEvent;
