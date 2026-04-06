namespace ClaudeCode.Cli.Repl;

/// <summary>
/// Maintains a bounded in-memory list of REPL input entries for the current session.
/// </summary>
public sealed class InputHistory
{
    private readonly List<string> _entries = [];
    private const int MaxEntries = 100;

    /// <summary>
    /// Adds an entry to the history, evicting the oldest entry when the limit is reached.
    /// </summary>
    /// <param name="entry">The input line to record. Must not be <see langword="null"/>.</param>
    public void Add(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_entries.Count >= MaxEntries)
            _entries.RemoveAt(0);
        _entries.Add(entry);
    }

    /// <summary>
    /// Returns all history entries in order from oldest to newest.
    /// </summary>
    public IReadOnlyList<string> GetAll() => _entries.AsReadOnly();

    /// <summary>
    /// The accumulated input entries in order from oldest to newest.
    /// </summary>
    public IReadOnlyList<string> Entries => _entries.AsReadOnly();

    /// <summary>
    /// Returns the most recent history entry that contains <paramref name="term"/>,
    /// or <see langword="null"/> when no match is found.
    /// </summary>
    /// <param name="term">The substring to search for (case-insensitive). Must not be null.</param>
    public string? FindReverse(string term)
    {
        if (string.IsNullOrEmpty(term)) return null;
        return _entries.LastOrDefault(h =>
            h.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
