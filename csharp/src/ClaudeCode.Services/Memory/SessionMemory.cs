namespace ClaudeCode.Services.Memory;

/// <summary>
/// A single fact extracted or stored during the current REPL session.
/// </summary>
/// <param name="Id">Unique identifier in the format "sf-N".</param>
/// <param name="Content">The human-readable fact text.</param>
/// <param name="Tags">Zero or more classification tags (e.g. "preference", "rule", "identity").</param>
/// <param name="RelevanceScore">0–100 relevance weight; initialized to 50.</param>
/// <param name="CreatedAt">When this fact was first stored.</param>
/// <param name="LastAccessedAt">When this fact was last matched by a query.</param>
public record SessionFact(
    string Id,
    string Content,
    string[] Tags,
    int RelevanceScore,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt
);

/// <summary>
/// In-RAM, session-scoped memory store for facts extracted or explicitly stored during a
/// single REPL session. Unlike <see cref="MemoryStore"/> (file-based, persistent),
/// <see cref="SessionMemory"/> lives only for the duration of the current process.
/// </summary>
public sealed class SessionMemory
{
    private readonly List<SessionFact> _facts = [];
    private int _nextId = 0;

    /// <summary>
    /// Stores a new fact with the given content and optional classification tags.
    /// The new fact is assigned a unique ID and an initial relevance score of 50.
    /// </summary>
    /// <param name="content">The fact text to store. Must not be <see langword="null"/>.</param>
    /// <param name="tags">Zero or more classification tags. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="content"/> or <paramref name="tags"/> is <see langword="null"/>.
    /// </exception>
    public void Store(string content, string[] tags)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(tags);

        var fact = new SessionFact(
            Id: $"sf-{Interlocked.Increment(ref _nextId)}",
            Content: content,
            Tags: tags,
            RelevanceScore: 50,
            CreatedAt: DateTimeOffset.UtcNow,
            LastAccessedAt: DateTimeOffset.UtcNow
        );
        _facts.Add(fact);
    }

    /// <summary>
    /// Returns the top <paramref name="topN"/> facts that match the given query using
    /// simple keyword overlap scoring. Facts with a score of zero are excluded.
    /// </summary>
    /// <param name="query">The query text to score facts against. Must not be <see langword="null"/>.</param>
    /// <param name="topN">Maximum number of results to return. Defaults to 5.</param>
    /// <returns>
    /// A read-only list of matching <see cref="SessionFact"/> instances ordered by descending score.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<SessionFact> GetRelevant(string query, int topN = 5)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Simple keyword relevance: score each fact by how many query words it contains
        var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _facts
            .Select(f => (fact: f, score: Score(f.Content, queryWords)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(topN)
            .Select(x => x.fact)
            .ToList();
    }

    /// <summary>Returns a read-only view of all stored facts in insertion order.</summary>
    public IReadOnlyList<SessionFact> GetAll() => _facts.AsReadOnly();

    /// <summary>
    /// Removes the fact with the given <paramref name="id"/>.
    /// </summary>
    /// <param name="id">The fact ID to remove (e.g. "sf-3"). Must not be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when a fact with that ID was found and removed;
    /// <see langword="false"/> when no matching fact exists.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="id"/> is <see langword="null"/>.</exception>
    public bool Remove(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var i = _facts.FindIndex(f => f.Id == id);
        if (i < 0) return false;
        _facts.RemoveAt(i);
        return true;
    }

    /// <summary>
    /// Builds a Markdown section suitable for injection into the system prompt.
    /// Returns an empty string when no facts are stored.
    /// Only the top 10 facts by relevance score are included.
    /// </summary>
    public string BuildPromptSection()
    {
        if (_facts.Count == 0) return "";
        var sb = new System.Text.StringBuilder("## Session Memory\n");
        foreach (var f in _facts.OrderByDescending(f => f.RelevanceScore).Take(10))
            sb.AppendLine($"- {f.Content}");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the number of <paramref name="queryWords"/> found in <paramref name="content"/>
    /// (case-insensitive). Used as the relevance score for <see cref="GetRelevant"/>.
    /// </summary>
    private static int Score(string content, string[] queryWords)
    {
        var lower = content.ToLowerInvariant();
        return queryWords.Count(w => lower.Contains(w));
    }
}
