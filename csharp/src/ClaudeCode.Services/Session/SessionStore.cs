namespace ClaudeCode.Services.Session;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Services.Api;

/// <summary>
/// Metadata describing a saved session, stored in every session JSON file and
/// returned by <see cref="SessionStore.ListRecentAsync"/> without loading full message history.
/// </summary>
public record SessionMetadata
{
    /// <summary>Unique session identifier (12-character hex string).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>UTC timestamp of session creation.</summary>
    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC timestamp of the last save operation.</summary>
    [JsonPropertyName("updated_at")]
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Anthropic model identifier used during this session.</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>Working directory at the time the session was saved.</summary>
    [JsonPropertyName("cwd")]
    public required string Cwd { get; init; }

    /// <summary>Total number of messages (user + assistant) in the session.</summary>
    [JsonPropertyName("message_count")]
    public int MessageCount { get; init; }

    /// <summary>Accumulated session cost in USD.</summary>
    [JsonPropertyName("cost_usd")]
    public double CostUsd { get; init; }

    /// <summary>
    /// Short summary extracted from the first user message.
    /// <see langword="null"/> when the session contained no user messages.
    /// </summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>
    /// Session tags assigned with the /tag command. <see langword="null"/> when no tags were added.
    /// </summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; init; }
}

/// <summary>
/// Complete on-disk representation of a saved session, pairing <see cref="SessionMetadata"/>
/// with the full <see cref="MessageParam"/> conversation history.
/// </summary>
public record SavedSession
{
    /// <summary>Session metadata (header information).</summary>
    [JsonPropertyName("metadata")]
    public required SessionMetadata Metadata { get; init; }

    /// <summary>Full ordered list of conversation messages.</summary>
    [JsonPropertyName("messages")]
    public required List<MessageParam> Messages { get; init; }
}

/// <summary>
/// Persists and retrieves Claude conversation sessions as JSON files under
/// <c>~/.claude/sessions/</c> (or a caller-supplied directory).
/// Each session is stored as a single <c>{sessionId}.json</c> file.
/// </summary>
public sealed class SessionStore
{
    private readonly string _sessionsDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Initializes a new <see cref="SessionStore"/>.
    /// </summary>
    /// <param name="sessionsDir">
    /// Directory where session files are stored. When <see langword="null"/>, defaults to
    /// <c>~/.claude/sessions/</c>. The directory is created if it does not exist.
    /// </param>
    public SessionStore(string? sessionsDir = null)
    {
        _sessionsDir = sessionsDir ?? GetDefaultSessionsDir();
        Directory.CreateDirectory(_sessionsDir);
    }

    /// <summary>
    /// Persists the current conversation to disk, creating or overwriting
    /// <c>{sessionsDir}/{sessionId}.json</c>.
    /// </summary>
    /// <param name="sessionId">The unique session identifier used as the file name.</param>
    /// <param name="messages">The full conversation history to save. Must not be <see langword="null"/>.</param>
    /// <param name="model">The model identifier used during the session. Must not be <see langword="null"/>.</param>
    /// <param name="cwd">The working directory of the session. Must not be <see langword="null"/>.</param>
    /// <param name="costUsd">Total cost in USD accumulated during the session.</param>
    /// <param name="tags">Optional list of user-defined session tags. May be <see langword="null"/> or empty.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The absolute path of the written session file.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="sessionId"/>, <paramref name="messages"/>,
    /// <paramref name="model"/>, or <paramref name="cwd"/> is <see langword="null"/>.
    /// </exception>
    public async Task<string> SaveAsync(
        string sessionId,
        List<MessageParam> messages,
        string model,
        string cwd,
        double costUsd,
        List<string>? tags = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(cwd);

        // Derive a short summary from the first user message for display in the picker.
        string? summary = null;
        foreach (var msg in messages)
        {
            if (msg.Role != "user")
                continue;

            var text = ExtractText(msg.Content);
            if (text.Length > 0)
            {
                summary = text.Length > 100 ? string.Concat(text.AsSpan(0, 100), "...") : text;
                break;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var session = new SavedSession
        {
            Metadata = new SessionMetadata
            {
                Id = sessionId,
                CreatedAt = now,
                UpdatedAt = now,
                Model = model,
                Cwd = cwd,
                MessageCount = messages.Count,
                CostUsd = costUsd,
                Summary = summary,
                Tags = tags is { Count: > 0 } ? tags : null,
            },
            Messages = messages,
        };

        var path = GetSessionPath(sessionId);
        var json = JsonSerializer.Serialize(session, JsonOpts);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Loads a previously saved session by its identifier.
    /// </summary>
    /// <param name="sessionId">The session identifier to load. Must not be <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The deserialized <see cref="SavedSession"/>, or <see langword="null"/> when no session
    /// file exists for <paramref name="sessionId"/> or the file is corrupt.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sessionId"/> is <see langword="null"/>.</exception>
    public async Task<SavedSession?> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);

        var path = GetSessionPath(sessionId);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SavedSession>(json, JsonOpts);
        }
        catch (JsonException)
        {
            // Corrupt session file — treat as absent rather than crashing.
            return null;
        }
    }

    /// <summary>
    /// Returns metadata for the most recently modified session files, ordered newest first.
    /// Corrupt or unreadable files are silently skipped.
    /// </summary>
    /// <param name="maxCount">Maximum number of sessions to return. Defaults to 10.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="SessionMetadata"/> records, newest first.</returns>
    public async Task<List<SessionMetadata>> ListRecentAsync(int maxCount = 10, CancellationToken ct = default)
    {
        if (maxCount <= 0)
            return [];

        var results = new List<SessionMetadata>();

        if (!Directory.Exists(_sessionsDir))
            return results;

        var files = Directory.GetFiles(_sessionsDir, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(maxCount);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var session = JsonSerializer.Deserialize<SavedSession>(json, JsonOpts);
                if (session?.Metadata is not null)
                    results.Add(session.Metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Skip corrupt or inaccessible session files — best-effort listing.
            }
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private string GetSessionPath(string sessionId) =>
        Path.Combine(_sessionsDir, $"{sessionId}.json");

    private static string GetDefaultSessionsDir() =>
        Path.Combine(ClaudeCode.Configuration.ConfigPaths.ClaudeHomeDir, "sessions");

    /// <summary>
    /// Extracts a plain-text string from a <see cref="MessageParam.Content"/> element,
    /// which may be either a raw string or an array of typed content blocks.
    /// Returns an empty string when no text block is found.
    /// </summary>
    private static string ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var typeEl)
                    && typeEl.GetString() == "text"
                    && block.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                        return text;
                }
            }
        }

        return string.Empty;
    }
}
