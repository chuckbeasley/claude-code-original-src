namespace ClaudeCode.Services.Memory;

/// <summary>
/// A single key/value fact extracted or explicitly stored during the current REPL session.
/// </summary>
/// <param name="Key">The fact key used for upsert lookups (case-insensitive).</param>
/// <param name="Value">The fact value text.</param>
/// <param name="Source">Optional source annotation (e.g. the message that surfaced the fact).</param>
/// <param name="AddedAt">UTC timestamp when this fact was last upserted.</param>
public sealed record SessionMemoryEntry(
    string Key,
    string Value,
    string? Source,
    DateTimeOffset AddedAt);

/// <summary>
/// In-session key/value fact store. Facts are extracted from conversation turns
/// and made available as a system prompt addition for subsequent turns.
/// Thread-safe: all mutations are serialised through a <see cref="SemaphoreSlim"/>.
/// </summary>
public sealed class SessionMemoryService
{
    private readonly List<SessionMemoryEntry> _facts = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Upserts a fact by key (case-insensitive). If an entry with the same key already
    /// exists it is replaced in-place; otherwise a new entry is appended.
    /// </summary>
    /// <param name="key">The fact key. Must not be null or whitespace.</param>
    /// <param name="value">The fact value. Must not be null.</param>
    /// <param name="source">Optional annotation recording where the fact came from.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    public async Task AddFactAsync(string key, string value, string? source = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var idx = _facts.FindIndex(
                f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

            var fact = new SessionMemoryEntry(key, value, source, DateTimeOffset.UtcNow);
            if (idx >= 0)
                _facts[idx] = fact;
            else
                _facts.Add(fact);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns a read-only snapshot of all currently stored facts in insertion order.
    /// </summary>
    public async Task<IReadOnlyList<SessionMemoryEntry>> GetAllAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return _facts.ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns a Markdown-formatted section for injection into the system prompt.
    /// Returns an empty string when no facts are stored.
    /// Format: "## Session Memory\n- {key}: {value}\n..."
    /// </summary>
    public string BuildPromptSection()
    {
        // Synchronous lock is safe here: the critical section is trivially short.
        _lock.Wait();
        try
        {
            if (_facts.Count == 0)
                return string.Empty;

            var sb = new System.Text.StringBuilder("## Session Memory\n");
            foreach (var f in _facts)
                sb.AppendLine($"- {f.Key}: {f.Value}");

            return sb.ToString();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes all stored facts.</summary>
    public async Task ClearAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _facts.Clear();
        }
        finally
        {
            _lock.Release();
        }
    }
}
