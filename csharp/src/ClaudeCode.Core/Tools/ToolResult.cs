namespace ClaudeCode.Core.Tools;

using ClaudeCode.Core.Messages;

/// <summary>
/// The outcome of a successful tool execution.
/// <typeparam name="T">The strongly-typed result produced by the tool.</typeparam>
/// </summary>
/// <remarks>
/// <see cref="NewMessages"/> allows a tool to inject additional conversation messages
/// (e.g. follow-up user messages or synthetic tool results) into the main loop after
/// execution completes.
/// </remarks>
public record ToolResult<T>
{
    /// <summary>The typed data produced by the tool execution.</summary>
    public required T Data { get; init; }

    /// <summary>
    /// Optional set of messages the tool wishes to append to the conversation history
    /// immediately after this result is processed.
    /// <see langword="null"/> when no follow-up messages are needed.
    /// </summary>
    public IReadOnlyList<Message>? NewMessages { get; init; }
}
