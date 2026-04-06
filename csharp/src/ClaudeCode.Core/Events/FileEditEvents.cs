namespace ClaudeCode.Core.Events;

/// <summary>
/// Process-wide events raised around file-edit operations.
/// Lives in <c>ClaudeCode.Core</c> so both <c>ClaudeCode.Tools</c> (the producer)
/// and <c>ClaudeCode.Commands</c> (the consumer) can reference it without
/// creating a circular project dependency.
/// </summary>
public static class FileEditEvents
{
    /// <summary>
    /// Raised by <c>FileEditTool</c> immediately before it writes new content to disk.
    /// Subscribers receive the absolute file path and the <em>original</em> file content
    /// so they can store a snapshot for undo / rewind purposes.
    /// </summary>
    /// <remarks>
    /// Parameters: (string absolutePath, string originalContent)
    /// </remarks>
    public static event Action<string, string>? BeforeEdit;

    /// <summary>
    /// Raises the <see cref="BeforeEdit"/> event.
    /// Safe to call when no subscribers are registered.
    /// </summary>
    /// <param name="path">The absolute path of the file about to be edited.</param>
    /// <param name="content">The current (pre-edit) content of the file.</param>
    public static void RaiseBeforeEdit(string path, string content)
        => BeforeEdit?.Invoke(path, content);
}
