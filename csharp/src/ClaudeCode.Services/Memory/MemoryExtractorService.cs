namespace ClaudeCode.Services.Memory;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Services.Api;

/// <summary>
/// DTO representing a single extracted fact from the LLM extraction response.
/// </summary>
file sealed record ExtractedFact
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    /// <summary>When true, the fact is saved to persistent <see cref="MemoryStore"/>; otherwise session-only.</summary>
    [JsonPropertyName("persist")]
    public bool Persist { get; init; }
}

/// <summary>
/// Periodically scans recent assistant messages to extract memorable facts
/// using a fast (haiku) model call. Facts marked as persistent go to
/// <see cref="MemoryStore"/>; session-scoped facts go to <see cref="SessionMemoryService"/>.
/// </summary>
public sealed class MemoryExtractorService
{
    private readonly IAnthropicClient _client;
    private readonly SessionMemoryService _sessionMemory;
    private readonly MemoryStore _persistentMemory;

    private int _turnsSinceLastExtraction;

    private const int ExtractionIntervalTurns = 5;
    private const string ExtractionModel = "claude-haiku-4-5-20251001";
    private const int MaxExcerptChars = 4000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Initialises a new <see cref="MemoryExtractorService"/>.
    /// </summary>
    /// <param name="client">Anthropic API client. Must not be <see langword="null"/>.</param>
    /// <param name="sessionMemory">Session-scoped memory store. Must not be <see langword="null"/>.</param>
    /// <param name="persistentMemory">Persistent file-backed memory store. Must not be <see langword="null"/>.</param>
    public MemoryExtractorService(
        IAnthropicClient client,
        SessionMemoryService sessionMemory,
        MemoryStore persistentMemory)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _sessionMemory = sessionMemory ?? throw new ArgumentNullException(nameof(sessionMemory));
        _persistentMemory = persistentMemory ?? throw new ArgumentNullException(nameof(persistentMemory));
    }

    /// <summary>
    /// Called after each assistant response. Every <see cref="ExtractionIntervalTurns"/> turns,
    /// makes a fast API call to extract memorable facts from <paramref name="recentMessages"/>.
    /// Facts with <c>persist: true</c> are saved to <see cref="MemoryStore"/>;
    /// all other facts are saved to <see cref="SessionMemoryService"/>.
    /// This method is designed to be called fire-and-forget; all exceptions are swallowed.
    /// </summary>
    /// <param name="recentMessages">
    /// The current full conversation history. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task MaybeExtractAsync(
        IReadOnlyList<MessageParam> recentMessages,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recentMessages);

        _turnsSinceLastExtraction++;
        if (_turnsSinceLastExtraction < ExtractionIntervalTurns)
            return;

        _turnsSinceLastExtraction = 0;

        var excerpt = BuildExcerpt(recentMessages);
        if (string.IsNullOrWhiteSpace(excerpt))
            return;

        try
        {
            var prompt =
                "Extract any memorable facts from this conversation excerpt as a JSON array " +
                "of objects with fields \"key\" (string), \"value\" (string), \"persist\" (boolean). " +
                "Only extract concrete facts worth remembering. " +
                "Return [] if nothing notable.\n\n" +
                "Conversation excerpt:\n" + excerpt;

            var request = new MessageRequest
            {
                Model = ExtractionModel,
                MaxTokens = 512,
                Messages =
                [
                    new MessageParam
                    {
                        Role = "user",
                        Content = JsonSerializer.SerializeToElement(prompt),
                    }
                ],
            };

            var textBuilder = new System.Text.StringBuilder();
            await foreach (var evt in _client.StreamMessageAsync(request, ct).ConfigureAwait(false))
            {
                if (evt.EventType != "content_block_delta")
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(evt.Data);
                    if (doc.RootElement.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("type", out var typeEl)
                        && typeEl.GetString() == "text_delta"
                        && delta.TryGetProperty("text", out var textEl))
                    {
                        textBuilder.Append(textEl.GetString());
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed SSE data — extraction is best-effort.
                }
            }

            await ParseAndSaveFactsAsync(textBuilder.ToString(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Extraction is best-effort; never surface errors to the REPL.
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a truncated plaintext excerpt from the most recent conversation messages,
    /// keeping the total under <see cref="MaxExcerptChars"/> characters.
    /// </summary>
    private static string BuildExcerpt(IReadOnlyList<MessageParam> messages)
    {
        var sb = new System.Text.StringBuilder();
        // Iterate newest-first to fill the budget with the most relevant context.
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            var text = ExtractText(msg.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var line = $"[{msg.Role}]: {text}\n";
            if (sb.Length + line.Length > MaxExcerptChars)
                break;

            sb.Insert(0, line);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Extracts plain text from a <see cref="MessageParam.Content"/> element,
    /// which may be a raw string or an array of content blocks.
    /// </summary>
    private static string ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var textEl))
                {
                    var s = textEl.GetString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Locates a JSON array in <paramref name="raw"/> and saves each fact to the
    /// appropriate store based on the <c>persist</c> flag.
    /// </summary>
    private async Task ParseAndSaveFactsAsync(string raw, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;

        // Find the first '[' to locate the JSON array even if the model adds preamble text.
        var startIdx = raw.IndexOf('[');
        var endIdx = raw.LastIndexOf(']');
        if (startIdx < 0 || endIdx <= startIdx)
            return;

        var json = raw[startIdx..(endIdx + 1)];

        List<ExtractedFact>? facts;
        try
        {
            facts = JsonSerializer.Deserialize<List<ExtractedFact>>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return; // Invalid JSON from model — skip silently.
        }

        if (facts is null || facts.Count == 0)
            return;

        foreach (var fact in facts)
        {
            if (string.IsNullOrWhiteSpace(fact.Key) || string.IsNullOrWhiteSpace(fact.Value))
                continue;

            ct.ThrowIfCancellationRequested();

            if (fact.Persist)
            {
                // Best-effort persistent save — IO errors must not propagate.
                try
                {
                    _persistentMemory.Save(
                        name: fact.Key,
                        description: fact.Value,
                        type: "project",
                        content: fact.Value);
                }
                catch
                {
                    // Persist is best-effort.
                }
            }
            else
            {
                await _sessionMemory.AddFactAsync(
                    fact.Key, fact.Value, source: "auto-extraction").ConfigureAwait(false);
            }
        }
    }
}
