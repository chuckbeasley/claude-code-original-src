namespace ClaudeCode.Services.Lsp;

/// <summary>
/// A single diagnostic reported by an LSP server via <c>textDocument/publishDiagnostics</c>.
/// </summary>
/// <param name="FileUri">The <c>file:///</c> URI of the file that owns this diagnostic.</param>
/// <param name="StartLine">Zero-based start line of the diagnostic range.</param>
/// <param name="StartCharacter">Zero-based start character offset within <paramref name="StartLine"/>.</param>
/// <param name="EndLine">Zero-based end line of the diagnostic range.</param>
/// <param name="EndCharacter">Zero-based end character offset within <paramref name="EndLine"/>.</param>
/// <param name="Severity">
///     LSP severity code: 1 = Error, 2 = Warning, 3 = Information, 4 = Hint.
/// </param>
/// <param name="Message">Human-readable diagnostic message.</param>
/// <param name="Source">Optional identifier for the tool or linter that produced the diagnostic.</param>
public sealed record LspDiagnostic(
    string FileUri,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter,
    int Severity,
    string Message,
    string? Source);

/// <summary>
/// LRU cache for LSP diagnostics, capped at <c>10</c> diagnostics per file and <c>30</c> total.
/// </summary>
/// <remarks>
/// Eviction order is least-recently-updated: when the total count would exceed 30 after
/// adding diagnostics for a new or existing file, the file whose diagnostics were updated
/// least recently is removed first.
/// All public members are thread-safe via an internal lock.
/// </remarks>
public sealed class LspDiagnosticRegistry
{
    private const int MaxPerFile = 10;
    private const int MaxTotal   = 30;

    // byFile stores the current diagnostics for each URI.
    private readonly Dictionary<string, List<LspDiagnostic>> _byFile
        = new(StringComparer.OrdinalIgnoreCase);

    // _lruOrder tracks update order: front = oldest (evicted first), back = most recent.
    private readonly LinkedList<string> _lruOrder = new();

    // _nodeMap allows O(1) removal of a file's LRU node when it is updated or cleared.
    private readonly Dictionary<string, LinkedListNode<string>> _nodeMap
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();

    // -----------------------------------------------------------------------
    // Mutation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Replaces all diagnostics for <paramref name="fileUri"/> with <paramref name="diagnostics"/>,
    /// truncating to 10 per file and evicting oldest files to maintain the 30-total cap.
    /// </summary>
    /// <param name="fileUri">The <c>file:///</c> URI whose diagnostics are being replaced.</param>
    /// <param name="diagnostics">
    ///     The new complete set of diagnostics from the LSP server.
    ///     An empty list clears diagnostics for the file without removing its LRU entry.
    /// </param>
    public void AddDiagnostics(string fileUri, IReadOnlyList<LspDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(fileUri);
        ArgumentNullException.ThrowIfNull(diagnostics);

        lock (_lock)
        {
            // Remove existing LRU entry for this file so we can re-insert at the MRU end.
            if (_nodeMap.TryGetValue(fileUri, out var existingNode))
            {
                _lruOrder.Remove(existingNode);
                _nodeMap.Remove(fileUri);
            }

            // Truncate to MaxPerFile; ToList materialises a private copy.
            var capped = diagnostics.Count > MaxPerFile
                ? diagnostics.Take(MaxPerFile).ToList()
                : diagnostics.ToList();

            _byFile[fileUri] = capped;

            // Insert at MRU (back of list).
            var node = _lruOrder.AddLast(fileUri);
            _nodeMap[fileUri] = node;

            // Evict oldest files until total diagnostics fits within the cap.
            while (TotalCount() > MaxTotal)
            {
                var oldest = _lruOrder.First;
                if (oldest is null) break;

                var oldestUri = oldest.Value;
                _lruOrder.RemoveFirst();
                _nodeMap.Remove(oldestUri);
                _byFile.Remove(oldestUri);
            }
        }
    }

    /// <summary>
    /// Clears diagnostics for the given <paramref name="fileUri"/>, or clears all
    /// diagnostics when <paramref name="fileUri"/> is <see langword="null"/>.
    /// </summary>
    /// <param name="fileUri">
    ///     URI of the file to clear, or <see langword="null"/> to clear everything.
    /// </param>
    public void Clear(string? fileUri = null)
    {
        lock (_lock)
        {
            if (fileUri is null)
            {
                _byFile.Clear();
                _lruOrder.Clear();
                _nodeMap.Clear();
            }
            else
            {
                if (_nodeMap.TryGetValue(fileUri, out var node))
                {
                    _lruOrder.Remove(node);
                    _nodeMap.Remove(fileUri);
                }
                _byFile.Remove(fileUri);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Queries
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns all current diagnostics for <paramref name="fileUri"/>,
    /// or an empty list if none are stored.
    /// </summary>
    /// <param name="fileUri">The <c>file:///</c> URI to query.</param>
    public IReadOnlyList<LspDiagnostic> GetDiagnostics(string fileUri)
    {
        ArgumentNullException.ThrowIfNull(fileUri);

        lock (_lock)
        {
            return _byFile.TryGetValue(fileUri, out var list)
                ? list.AsReadOnly()
                : [];
        }
    }

    /// <summary>
    /// Returns all diagnostics across every tracked file as a single flat list.
    /// </summary>
    public IReadOnlyList<LspDiagnostic> GetAllDiagnostics()
    {
        lock (_lock)
        {
            return _byFile.Values
                .SelectMany(static list => list)
                .ToList()
                .AsReadOnly();
        }
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    // Must be called under _lock.
    private int TotalCount()
    {
        var total = 0;
        foreach (var list in _byFile.Values)
            total += list.Count;
        return total;
    }
}
