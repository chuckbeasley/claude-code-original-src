namespace ClaudeCode.Services.Lsp;

/// <summary>
/// Singleton manager for all active LSP servers. Routes file URIs to servers by extension,
/// manages server lifecycle, and supports hot reload (stop-all, clear, re-register).
/// </summary>
/// <remarks>
/// <para>
/// Register servers with <see cref="RegisterServerAsync"/>. A server is matched to a file
/// by comparing the file's extension (case-insensitive) against
/// <see cref="LspServerConfig.Extensions"/> in registration order.
/// </para>
/// <para>
/// <see cref="ReloadAsync"/> stops every running server, clears all diagnostics, increments
/// an internal generation counter, then re-registers and restarts every server that was
/// previously registered — providing a clean hot-reload without losing configuration.
/// </para>
/// </remarks>
public sealed class LspServerManager : IAsyncDisposable
{
    private readonly LspDiagnosticRegistry _registry;

    // Mutable lists protected by _lock.
    private readonly List<LspServerInstance> _servers = [];
    private readonly List<LspServerConfig>   _configs = []; // remembered for ReloadAsync

    private int _generation;
    private bool _disposed;

    // SemaphoreSlim(1,1) used as an async-compatible mutex for _servers and _configs.
    private readonly SemaphoreSlim _lock = new(1, 1);

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Initialises a new <see cref="LspServerManager"/> backed by the supplied
    /// diagnostic registry.
    /// </summary>
    /// <param name="registry">
    ///     Registry that receives <c>textDocument/publishDiagnostics</c> events from every
    ///     managed server.
    /// </param>
    public LspServerManager(LspDiagnosticRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    // -----------------------------------------------------------------------
    // Registration
    // -----------------------------------------------------------------------

    /// <summary>
    /// Registers a new server configuration, creates a <see cref="LspServerInstance"/>,
    /// starts it, and adds it to the active set.
    /// </summary>
    /// <param name="config">Configuration for the server to register.</param>
    /// <param name="ct">Cancellation token for the startup phase.</param>
    /// <exception cref="InvalidOperationException">
    ///     Propagated from <see cref="LspServerInstance.StartAsync"/> if the process
    ///     cannot be started.
    /// </exception>
    public async Task RegisterServerAsync(
        LspServerConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var instance = new LspServerInstance(config, _registry);
            await instance.StartAsync(ct).ConfigureAwait(false);
            _servers.Add(instance);
            _configs.Add(config);
        }
        finally { _lock.Release(); }
    }

    // -----------------------------------------------------------------------
    // Routing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the <see cref="LspServerInstance"/> that handles the given file URI,
    /// or <see langword="null"/> if no registered server declares the file's extension.
    /// </summary>
    /// <remarks>
    /// The extension is extracted from the URI path component before any query string
    /// (e.g. <c>file:///src/Foo.cs?v=2</c> → <c>.cs</c>).
    /// Matching is case-insensitive.
    /// </remarks>
    /// <param name="fileUri">The <c>file:///</c> URI (or any URI) of the file.</param>
    public LspServerInstance? GetServerForFile(string fileUri)
    {
        ArgumentNullException.ThrowIfNull(fileUri);

        // Strip query string, then extract extension.
        var path = fileUri.TrimEnd('/');
        var queryStart = path.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0) path = path[..queryStart];

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;

        // Take a snapshot under lock so callers outside the lock see a consistent list.
        LspServerInstance[] snapshot;
        _lock.Wait();
        try { snapshot = [.. _servers]; }
        finally { _lock.Release(); }

        return Array.Find(snapshot, s =>
            Array.Exists(s.Config.Extensions,
                e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)));
    }

    // -----------------------------------------------------------------------
    // Hot reload
    // -----------------------------------------------------------------------

    /// <summary>
    /// Stops all running servers, clears all diagnostics, increments the internal
    /// generation counter, then re-registers and restarts every previously known server.
    /// </summary>
    /// <remarks>
    /// Servers that fail to restart are logged to <see cref="Console.Error"/> and skipped;
    /// other servers are still started.
    /// </remarks>
    /// <param name="ct">Cancellation token for the re-registration phase.</param>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // --- Tear down ---
            foreach (var server in _servers)
            {
                try { await server.StopAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
            foreach (var server in _servers)
            {
                try { await server.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
            _servers.Clear();

            _registry.Clear();

            _generation++;

            // --- Re-register ---
            // Work off a copy so failures don't partially corrupt _configs.
            var configsToRestore = _configs.ToList();
            _configs.Clear();

            foreach (var config in configsToRestore)
            {
                try
                {
                    var instance = new LspServerInstance(config, _registry);
                    await instance.StartAsync(ct).ConfigureAwait(false);
                    _servers.Add(instance);
                    _configs.Add(config);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                            $"[LspServerManager] Reload: failed to restart '{config.Name}': {ex.Message}")
                        .ConfigureAwait(false);
                }
            }
        }
        finally { _lock.Release(); }
    }

    // -----------------------------------------------------------------------
    // File notification forwarding
    // -----------------------------------------------------------------------

    /// <summary>
    /// Forwards a <c>textDocument/didOpen</c> notification to the server that handles
    /// <paramref name="fileUri"/>, if one is registered.
    /// </summary>
    /// <param name="fileUri">The <c>file:///</c> URI of the opened file.</param>
    /// <param name="languageId">LSP language identifier (e.g. <c>csharp</c>).</param>
    /// <param name="content">Full text content of the file.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task NotifyFileOpenedAsync(
        string fileUri, string languageId, string content,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileUri);
        ArgumentNullException.ThrowIfNull(languageId);
        ArgumentNullException.ThrowIfNull(content);

        var server = GetServerForFile(fileUri);
        if (server is not null)
            await server.NotifyDidOpenAsync(fileUri, languageId, content, ct)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Forwards a <c>textDocument/didChange</c> notification to the server that handles
    /// <paramref name="fileUri"/>, if one is registered.
    /// </summary>
    /// <param name="fileUri">The <c>file:///</c> URI of the changed file.</param>
    /// <param name="content">New full text content.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task NotifyFileChangedAsync(
        string fileUri, string content,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileUri);
        ArgumentNullException.ThrowIfNull(content);

        var server = GetServerForFile(fileUri);
        if (server is not null)
            await server.NotifyDidChangeAsync(fileUri, content, ct)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Forwards a <c>textDocument/didClose</c> notification to the server that handles
    /// <paramref name="fileUri"/>, if one is registered.
    /// </summary>
    /// <param name="fileUri">The <c>file:///</c> URI of the closed file.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task NotifyFileClosedAsync(
        string fileUri,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileUri);

        var server = GetServerForFile(fileUri);
        if (server is not null)
            await server.NotifyDidCloseAsync(fileUri, ct)
                .ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Take a snapshot of servers to dispose; release the lock before
        // the (potentially slow) async dispose calls.
        List<LspServerInstance> toDispose;
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            toDispose = [.. _servers];
            _servers.Clear();
        }
        finally { _lock.Release(); }

        _lock.Dispose();

        foreach (var server in toDispose)
        {
            try { await server.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
    }
}
