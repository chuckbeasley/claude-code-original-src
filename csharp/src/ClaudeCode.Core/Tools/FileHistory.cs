namespace ClaudeCode.Core.Tools;

/// <summary>
/// Tracks file edit history for undo support. Each file gets a stack of snapshots.
/// </summary>
public class FileHistory
{
    private readonly Dictionary<string, Stack<FileSnapshot>> _history = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Saves a snapshot of the file's current content before an edit.
    /// </summary>
    /// <param name="filePath">Absolute or relative path of the file being edited.</param>
    /// <param name="content">The file content at the time the snapshot is taken.</param>
    public void SaveSnapshot(string filePath, string content)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(content);

        var key = Path.GetFullPath(filePath);
        if (!_history.TryGetValue(key, out var stack))
        {
            stack = new Stack<FileSnapshot>();
            _history[key] = stack;
        }
        stack.Push(new FileSnapshot(content, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Gets the most recent snapshot for a file without removing it.
    /// Returns <see langword="null"/> when no snapshots exist for the file.
    /// </summary>
    /// <param name="filePath">Absolute or relative path of the file.</param>
    public FileSnapshot? Peek(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var key = Path.GetFullPath(filePath);
        return _history.TryGetValue(key, out var stack) && stack.Count > 0
            ? stack.Peek()
            : null;
    }

    /// <summary>
    /// Pops the most recent snapshot for undo.
    /// Returns <see langword="null"/> when no snapshots exist for the file.
    /// </summary>
    /// <param name="filePath">Absolute or relative path of the file.</param>
    public FileSnapshot? Pop(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var key = Path.GetFullPath(filePath);
        return _history.TryGetValue(key, out var stack) && stack.Count > 0
            ? stack.Pop()
            : null;
    }

    /// <summary>
    /// Returns the number of snapshots for a file.
    /// </summary>
    /// <param name="filePath">Absolute or relative path of the file.</param>
    public int Count(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var key = Path.GetFullPath(filePath);
        return _history.TryGetValue(key, out var stack) ? stack.Count : 0;
    }
}

/// <summary>
/// An immutable point-in-time snapshot of a file's content.
/// </summary>
/// <param name="Content">The file content at the time the snapshot was taken.</param>
/// <param name="Timestamp">UTC instant at which the snapshot was saved.</param>
public record FileSnapshot(string Content, DateTimeOffset Timestamp);
