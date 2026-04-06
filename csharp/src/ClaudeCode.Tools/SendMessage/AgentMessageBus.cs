namespace ClaudeCode.Tools.SendMessage;

using System.Threading.Channels;
using System.Collections.Concurrent;

/// <summary>
/// In-process message bus for agent-to-agent communication.
/// Each named agent has an unbounded channel of pending messages.
/// </summary>
public static class AgentMessageBus
{
    private static readonly ConcurrentDictionary<string, Channel<string>> _queues = new();

    /// <summary>Gets or creates the message queue for <paramref name="agentId"/>.</summary>
    /// <param name="agentId">The agent identifier. Must not be <see langword="null"/> or whitespace.</param>
    /// <returns>The <see cref="Channel{T}"/> for the named agent.</returns>
    public static Channel<string> GetQueue(string agentId) =>
        _queues.GetOrAdd(agentId, _ => Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false }));

    /// <summary>Posts a message to the named agent's queue. Non-blocking.</summary>
    /// <param name="agentId">The recipient agent identifier.</param>
    /// <param name="message">The message body to post.</param>
    public static void Post(string agentId, string message) =>
        GetQueue(agentId).Writer.TryWrite(message);

    /// <summary>
    /// Tries to read all pending messages for <paramref name="agentId"/> without waiting.
    /// Returns an empty list if no messages are queued.
    /// </summary>
    /// <param name="agentId">The agent identifier whose queue to drain.</param>
    /// <returns>All messages currently available in the queue; never <see langword="null"/>.</returns>
    public static IReadOnlyList<string> DrainMessages(string agentId)
    {
        var q = GetQueue(agentId);
        var msgs = new List<string>();
        while (q.Reader.TryRead(out var msg))
            msgs.Add(msg);
        return msgs;
    }

    /// <summary>Waits asynchronously for the next message for <paramref name="agentId"/>.</summary>
    /// <param name="agentId">The agent identifier whose queue to read from.</param>
    /// <param name="ct">Cancellation token; cancelled when a timeout or session shutdown occurs.</param>
    /// <returns>The next message from the queue.</returns>
    public static ValueTask<string> WaitNextAsync(string agentId, CancellationToken ct) =>
        GetQueue(agentId).Reader.ReadAsync(ct);
}
