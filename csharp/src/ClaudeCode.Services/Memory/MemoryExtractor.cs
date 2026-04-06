namespace ClaudeCode.Services.Memory;

/// <summary>
/// Scans assistant messages for sentences that contain heuristic trigger phrases
/// and automatically stores them as facts in a <see cref="SessionMemory"/> instance.
/// </summary>
/// <remarks>
/// The extraction is intentionally lightweight and heuristic-based. Sentences shorter than
/// 20 or longer than 200 characters are skipped to filter noise and overly verbose text.
/// </remarks>
public sealed class MemoryExtractor
{
    private readonly SessionMemory _memory;

    private static readonly string[] Triggers =
    [
        "remember", "note that", "you prefer", "you said", "you want",
        "you asked", "always", "never", "your name", "you like", "prefer"
    ];

    /// <summary>
    /// Initializes a new <see cref="MemoryExtractor"/> that stores discovered facts into
    /// <paramref name="memory"/>.
    /// </summary>
    /// <param name="memory">The target session memory. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="memory"/> is <see langword="null"/>.</exception>
    public MemoryExtractor(SessionMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    /// <summary>
    /// Scans the last assistant message for facts worth remembering.
    /// Heuristic: sentences containing "you said", "note that", "remember",
    /// "preference", "always", "never", "you prefer", "your name", etc.
    /// Matching sentences are stored into the session memory with extracted tags.
    /// </summary>
    /// <param name="assistantMessage">
    /// The full text of the assistant's response. Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="assistantMessage"/> is <see langword="null"/>.
    /// </exception>
    public void ExtractFromMessage(string assistantMessage)
    {
        ArgumentNullException.ThrowIfNull(assistantMessage);

        var sentences = assistantMessage.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var sentence in sentences)
        {
            var lower = sentence.ToLowerInvariant().Trim();
            if (lower.Length < 20 || lower.Length > 200) continue;
            if (Triggers.Any(t => lower.Contains(t)))
            {
                var tags = ExtractTags(lower);
                _memory.Store(sentence.Trim(), tags);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts zero or more classification tags from a lower-cased sentence fragment.
    /// </summary>
    private static string[] ExtractTags(string text)
    {
        var tags = new List<string>();
        if (text.Contains("prefer")) tags.Add("preference");
        if (text.Contains("always") || text.Contains("never")) tags.Add("rule");
        if (text.Contains("name")) tags.Add("identity");
        return [.. tags];
    }
}
