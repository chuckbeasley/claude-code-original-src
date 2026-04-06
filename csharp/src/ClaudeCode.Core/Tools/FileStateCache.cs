namespace ClaudeCode.Core.Tools;

/// <summary>
/// Records a single cached read of a file, including what portion was loaded and when.
/// </summary>
/// <param name="Content">The file content (or partial content) at the time of the read.</param>
/// <param name="Timestamp">UTC instant at which the read occurred.</param>
/// <param name="Offset">
/// Zero-based line offset at which the read started, or <see langword="null"/> when the file
/// was read from the beginning.
/// </param>
/// <param name="Limit">
/// Maximum number of lines that were read, or <see langword="null"/> when the entire
/// remaining content was returned.
/// </param>
/// <param name="IsPartialView">
/// <see langword="true"/> when <paramref name="Offset"/> or <paramref name="Limit"/> restricted
/// what was returned, meaning the cache does not represent the full file.
/// </param>
public record FileReadState(
    string Content,
    DateTimeOffset Timestamp,
    int? Offset = null,
    int? Limit = null,
    bool IsPartialView = false);

/// <summary>
/// Thread-unsafe, in-memory cache that tracks which files have been read by the current
/// tool execution pipeline and what state they were in at read time.
/// One instance is created per conversation turn and cloned when a snapshot is needed.
/// </summary>
public sealed class FileStateCache
{
    private readonly Dictionary<string, FileReadState> _cache;

    /// <summary>Initialises an empty cache.</summary>
    public FileStateCache() => _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initialises a cache pre-populated from <paramref name="source"/>.</summary>
    private FileStateCache(Dictionary<string, FileReadState> source)
        => _cache = new(source, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the cached <see cref="FileReadState"/> for <paramref name="filePath"/>,
    /// or <see langword="null"/> when the file has not been read in this session.
    /// </summary>
    /// <param name="filePath">Absolute or relative file path (case-insensitive key).</param>
    public FileReadState? Get(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return _cache.GetValueOrDefault(filePath);
    }

    /// <summary>
    /// Stores or replaces the cached state for <paramref name="filePath"/>.
    /// </summary>
    /// <param name="filePath">Absolute or relative file path (case-insensitive key).</param>
    /// <param name="state">The read state to cache.</param>
    public void Set(string filePath, FileReadState state)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(state);
        _cache[filePath] = state;
    }

    /// <summary>
    /// Removes the cached entry for <paramref name="filePath"/> if one exists.
    /// A no-op when the path is not in the cache.
    /// </summary>
    /// <param name="filePath">Absolute or relative file path (case-insensitive key).</param>
    public void Remove(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        _cache.Remove(filePath);
    }

    /// <summary>
    /// Returns a deep copy of this cache. The clone is independent — mutations to
    /// either instance do not affect the other.
    /// </summary>
    public FileStateCache Clone() => new(_cache);
}
