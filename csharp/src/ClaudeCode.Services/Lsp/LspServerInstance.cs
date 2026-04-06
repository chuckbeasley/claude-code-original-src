namespace ClaudeCode.Services.Lsp;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Lifecycle state of a single LSP server process.</summary>
public enum LspServerState
{
    /// <summary>The server has not been started, or was cleanly stopped.</summary>
    Stopped,
    /// <summary>The server is starting up and performing the initialize handshake.</summary>
    Starting,
    /// <summary>The server is running and accepting notifications.</summary>
    Running,
    /// <summary>The server has failed and exhausted its restart budget.</summary>
    Error,
    /// <summary>The server crashed and a restart is in progress.</summary>
    Restarting,
}

/// <summary>
/// Manages a single LSP server: process lifetime, crash recovery, and notification routing.
/// </summary>
/// <remarks>
/// <para>
/// This class owns a self-contained JSON-RPC 2.0 pipeline over the server's stdin/stdout.
/// It intentionally does not use <c>ClaudeCode.Tools.LSP.LspServerProcess</c> to avoid a
/// circular project reference (Tools already references Services).
/// </para>
/// <para>
/// Crash recovery: if the process exits unexpectedly, the instance waits for the next
/// backoff delay and retries <see cref="StartAsync"/> up to <see cref="MaxRestarts"/> times.
/// After exhausting restarts, <see cref="State"/> transitions to <see cref="LspServerState.Error"/>.
/// </para>
/// <para>
/// ContentModified (JSON-RPC error -32801): <see cref="SendRequestAsync"/> retries the
/// request up to 3 additional times with short back-off delays before propagating failure.
/// </para>
/// </remarks>
public sealed class LspServerInstance : IAsyncDisposable
{
    private const int MaxRestarts              = 3;
    private const int ContentModifiedErrorCode = -32801;
    private const int MaxContentModifiedRetries = 3;

    private static readonly TimeSpan[] RestartDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1_000),
        TimeSpan.FromMilliseconds(2_000),
    ];

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    /// <summary>Configuration used to launch this server.</summary>
    public LspServerConfig Config { get; }

    /// <summary>Current lifecycle state of the server.</summary>
    public LspServerState State { get; private set; } = LspServerState.Stopped;

    private int _restartCount;
    private bool _intentionalStop;
    private bool _disposed;

    // -----------------------------------------------------------------------
    // Process / transport
    // -----------------------------------------------------------------------

    private Process?    _process;
    private Task?       _readLoopTask;

    private readonly SemaphoreSlim _writeLock    = new(1, 1);
    private          int           _nextId       = 1;

    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending    = new();
    private readonly SemaphoreSlim                                      _pendingLock = new(1, 1);

    // Per-file document version counters for textDocument/didChange.
    private readonly Dictionary<string, int> _fileVersions
        = new(StringComparer.OrdinalIgnoreCase);

    // CTS that drives the read loop; replaced on every StartAsync call.
    private CancellationTokenSource _cts = new();

    private readonly LspDiagnosticRegistry _registry;

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Initialises a new <see cref="LspServerInstance"/> with the supplied configuration
    /// and diagnostic registry.
    /// </summary>
    /// <param name="config">Server configuration (command, args, extensions, workdir).</param>
    /// <param name="registry">Shared registry that receives <c>publishDiagnostics</c> events.</param>
    public LspServerInstance(LspServerConfig config, LspDiagnosticRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(registry);
        Config    = config;
        _registry = registry;
    }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    /// <summary>
    /// Starts the LSP server process and performs the <c>initialize</c>/<c>initialized</c>
    /// handshake. Transitions <see cref="State"/> from <see cref="LspServerState.Stopped"/>
    /// (or any non-Running state) to <see cref="LspServerState.Running"/>.
    /// </summary>
    /// <param name="ct">Cancellation token for the startup phase.</param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the process cannot be started or the handshake fails.
    /// </exception>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (State is LspServerState.Running) return;

        State             = LspServerState.Starting;
        _intentionalStop  = false;

        try
        {
            var workDir = string.IsNullOrWhiteSpace(Config.WorkDir)
                ? Directory.GetCurrentDirectory()
                : Config.WorkDir;

            var psi = new ProcessStartInfo(Config.Command)
            {
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                WorkingDirectory       = workDir,
                CreateNoWindow         = true,
                StandardInputEncoding  = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in Config.Args)
                psi.ArgumentList.Add(arg);

            _process = Process.Start(psi)
                ?? throw new InvalidOperationException(
                    $"Failed to start LSP server '{Config.Name}': {Config.Command}");

            // Recreate CTS so the new read loop gets a fresh token.
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            _readLoopTask = ReadLoopAsync(_cts.Token);

            // --- LSP initialize handshake ---
            var rootUri = ToFileUri(workDir);
            _ = await SendRequestAsync("initialize", new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"]   = rootUri,
                ["capabilities"] = new JsonObject
                {
                    ["textDocument"] = new JsonObject
                    {
                        ["publishDiagnostics"] = new JsonObject(),
                        ["hover"]              = new JsonObject
                        {
                            ["contentFormat"] = new JsonArray("plaintext"),
                        },
                        ["definition"] = new JsonObject(),
                        ["references"] = new JsonObject(),
                        ["completion"] = new JsonObject(),
                    },
                },
                ["initializationOptions"] = new JsonObject(),
            }, ct).ConfigureAwait(false);

            await SendNotificationAsync("initialized", new JsonObject(), ct)
                .ConfigureAwait(false);

            State         = LspServerState.Running;
            _restartCount = 0;
        }
        catch (Exception ex)
        {
            State = LspServerState.Error;
            await Console.Error.WriteLineAsync(
                    $"[LSP:{Config.Name}] Start failed: {ex.Message}")
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends a graceful <c>shutdown</c> notification, closes stdin, waits up to 2 s for
    /// the process to exit, then kills it if still running.
    /// Transitions <see cref="State"/> to <see cref="LspServerState.Stopped"/>.
    /// </summary>
    public async Task StopAsync()
    {
        if (State is LspServerState.Stopped) return;

        _intentionalStop = true;

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);

            if (_process is not null && !_process.HasExited)
            {
                try
                {
                    await SendNotificationAsync("shutdown", null, CancellationToken.None)
                        .ConfigureAwait(false);
                    _process.StandardInput.Close();
                    await Task.WhenAny(
                        _process.WaitForExitAsync(),
                        Task.Delay(2_000)).ConfigureAwait(false);
                    if (!_process.HasExited) _process.Kill();
                }
                catch { /* best-effort */ }
            }

            if (_readLoopTask is not null)
            {
                try { await _readLoopTask.ConfigureAwait(false); }
                catch { /* read loop already cancelled */ }
            }
        }
        finally
        {
            _process?.Dispose();
            _process      = null;
            _readLoopTask = null;
            _fileVersions.Clear();
            State = LspServerState.Stopped;
        }
    }

    // -----------------------------------------------------------------------
    // File notification API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sends a <c>textDocument/didOpen</c> notification to the server.
    /// </summary>
    /// <param name="fileUri">The <c>file:///</c> URI of the opened file.</param>
    /// <param name="languageId">LSP language identifier (e.g. <c>csharp</c>).</param>
    /// <param name="content">Full text content of the file.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task NotifyDidOpenAsync(
        string fileUri, string languageId, string content,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileUri);
        ArgumentNullException.ThrowIfNull(languageId);
        ArgumentNullException.ThrowIfNull(content);

        if (State is not LspServerState.Running) return;

        _fileVersions[fileUri] = 1;

        await SendNotificationAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"]        = fileUri,
                ["languageId"] = languageId,
                ["version"]    = 1,
                ["text"]       = content,
            },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a <c>textDocument/didChange</c> notification with the full updated content.
    /// </summary>
    /// <param name="fileUri">The <c>file:///</c> URI of the changed file.</param>
    /// <param name="content">New full text content of the file.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task NotifyDidChangeAsync(
        string fileUri, string content,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileUri);
        ArgumentNullException.ThrowIfNull(content);

        if (State is not LspServerState.Running) return;

        var version = _fileVersions.TryGetValue(fileUri, out var v) ? v + 1 : 1;
        _fileVersions[fileUri] = version;

        await SendNotificationAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"]     = fileUri,
                ["version"] = version,
            },
            ["contentChanges"] = new JsonArray
            {
                new JsonObject { ["text"] = content },
            },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a <c>textDocument/didSave</c> notification to the server.
    /// </summary>
    /// <param name="fileUri">The <c>file:///</c> URI of the saved file.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task NotifyDidSaveAsync(string fileUri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileUri);

        if (State is not LspServerState.Running) return;

        await SendNotificationAsync("textDocument/didSave", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fileUri },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a <c>textDocument/didClose</c> notification to the server and
    /// removes the file's version tracking entry.
    /// </summary>
    /// <param name="fileUri">The <c>file:///</c> URI of the closed file.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task NotifyDidCloseAsync(string fileUri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileUri);

        if (State is not LspServerState.Running) return;

        _fileVersions.Remove(fileUri);

        await SendNotificationAsync("textDocument/didClose", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fileUri },
        }, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // JSON-RPC transport
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sends a JSON-RPC request and awaits the response.
    /// Retries up to <see cref="MaxContentModifiedRetries"/> times on error -32801 (ContentModified).
    /// Times out after 10 s per attempt.
    /// </summary>
    private async Task<JsonObject> SendRequestAsync(
        string method, JsonObject? paramsObj, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= MaxContentModifiedRetries; attempt++)
        {
            int id;
            TaskCompletionSource<JsonObject> tcs;

            await _pendingLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                id  = _nextId++;
                tcs = new TaskCompletionSource<JsonObject>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[id] = tcs;
            }
            finally { _pendingLock.Release(); }

            var msg = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"]      = id,
                ["method"]  = method,
            };

            // DeepClone so each msg instance exclusively owns its params node.
            // JsonNode forbids assigning the same instance to two parents.
            if (paramsObj is not null)
                msg["params"] = (JsonObject)paramsObj.DeepClone();

            await WriteMessageAsync(msg, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            JsonObject response;
            try
            {
                response = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Timed out — remove the pending entry so it doesn't accumulate.
                await _pendingLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { _pending.Remove(id); }
                finally { _pendingLock.Release(); }
                throw new TimeoutException(
                    $"LSP request '{method}' (id={id}) timed out after 10 s.");
            }

            // Check for ContentModified error and retry if budget allows.
            if (attempt < MaxContentModifiedRetries
                && response.TryGetPropertyValue("error", out var errorNode)
                && errorNode is JsonObject errorObj
                && errorObj.TryGetPropertyValue("code", out var codeNode)
                && codeNode?.GetValue<int>() == ContentModifiedErrorCode)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct)
                    .ConfigureAwait(false);
                continue;
            }

            return response;
        }

        // Should be unreachable: the loop always returns on the final attempt.
        throw new InvalidOperationException(
            $"LSP request '{method}' failed with ContentModified after {MaxContentModifiedRetries} retries.");
    }

    /// <summary>
    /// Sends a JSON-RPC notification (no <c>id</c>; no response expected).
    /// </summary>
    private async Task SendNotificationAsync(
        string method, JsonObject? paramsObj, CancellationToken ct)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (paramsObj is not null) msg["params"] = paramsObj;
        await WriteMessageAsync(msg, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one LSP message (Content-Length header + JSON body) to the server's stdin.
    /// </summary>
    private async Task WriteMessageAsync(JsonObject msg, CancellationToken ct)
    {
        var body   = Encoding.UTF8.GetBytes(msg.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process is null || _process.HasExited) return;

            var stdin = _process.StandardInput.BaseStream;
            await stdin.WriteAsync(header, ct).ConfigureAwait(false);
            await stdin.WriteAsync(body,   ct).ConfigureAwait(false);
            await stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    // -----------------------------------------------------------------------
    // Read loop
    // -----------------------------------------------------------------------

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_process is null) return;

        var stream = _process.StandardOutput.BaseStream;

        try
        {
            while (!ct.IsCancellationRequested && !_process.HasExited)
            {
                // --- Parse LSP headers ---
                var contentLength = 0;

                while (true)
                {
                    var lineBuf = new List<byte>(64);
                    int b;
                    while ((b = await ReadByteAsync(stream, ct).ConfigureAwait(false)) != -1)
                    {
                        lineBuf.Add((byte)b);
                        // Lines end with \r\n; an empty \r\n line terminates the header block.
                        if (lineBuf.Count >= 2
                            && lineBuf[^2] == '\r'
                            && lineBuf[^1] == '\n')
                            break;
                    }

                    if (b == -1) return; // Stream closed.

                    var line = Encoding.ASCII.GetString(lineBuf.ToArray()).TrimEnd();
                    if (line.Length == 0) break; // Empty line = end of headers.

                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                }

                if (contentLength == 0) continue;

                // --- Read body ---
                var body = new byte[contentLength];
                var read = 0;
                while (read < contentLength)
                {
                    var n = await stream
                        .ReadAsync(body.AsMemory(read, contentLength - read), ct)
                        .ConfigureAwait(false);
                    if (n == 0) return; // Stream closed.
                    read += n;
                }

                // --- Dispatch message ---
                try
                {
                    var doc = JsonNode.Parse(body);
                    if (doc is not JsonObject obj) continue;

                    if (obj.TryGetPropertyValue("id", out var idNode) && idNode is not null)
                    {
                        // Response to a pending request.
                        var responseId = idNode.GetValue<int>();

                        await _pendingLock.WaitAsync(ct).ConfigureAwait(false);
                        TaskCompletionSource<JsonObject>? pendingTcs;
                        try
                        {
                            _pending.TryGetValue(responseId, out pendingTcs);
                            _pending.Remove(responseId);
                        }
                        finally { _pendingLock.Release(); }

                        pendingTcs?.TrySetResult(obj);
                    }
                    else if (obj.TryGetPropertyValue("method", out var methodNode)
                        && methodNode?.GetValue<string>() == "textDocument/publishDiagnostics"
                        && obj.TryGetPropertyValue("params", out var paramsNode)
                        && paramsNode is JsonObject diagParams)
                    {
                        // Diagnostic notification — route into the registry.
                        HandlePublishDiagnostics(diagParams);
                    }
                    // All other notifications are silently ignored.
                }
                catch { /* ignore individual message parse/dispatch errors */ }
            }
        }
        catch (OperationCanceledException) { /* intentional cancellation */ }
        catch { /* process exited or pipe broken */ }
        finally
        {
            // Cancel all in-flight requests so callers do not hang indefinitely.
            await _pendingLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                foreach (var pendingTcs in _pending.Values)
                    pendingTcs.TrySetCanceled();
                _pending.Clear();
            }
            finally { _pendingLock.Release(); }

            // Trigger crash recovery unless the exit was expected.
            if (!_intentionalStop && !_disposed)
                _ = Task.Run(HandleUnexpectedExitAsync);
        }
    }

    // -----------------------------------------------------------------------
    // Notification handlers
    // -----------------------------------------------------------------------

    private void HandlePublishDiagnostics(JsonObject diagParams)
    {
        if (!diagParams.TryGetPropertyValue("uri", out var uriNode)) return;
        var fileUri = uriNode?.GetValue<string>();
        if (string.IsNullOrEmpty(fileUri)) return;

        if (!diagParams.TryGetPropertyValue("diagnostics", out var diagsNode)
            || diagsNode is not JsonArray diagsArray)
        {
            // publishDiagnostics with no "diagnostics" key — clear the file.
            _registry.Clear(fileUri);
            return;
        }

        var result = new List<LspDiagnostic>(diagsArray.Count);

        foreach (var item in diagsArray)
        {
            if (item is not JsonObject d) continue;

            try
            {
                var range = d["range"]  as JsonObject;
                var start = range?["start"] as JsonObject;
                var end   = range?["end"]   as JsonObject;

                result.Add(new LspDiagnostic(
                    FileUri:        fileUri,
                    StartLine:      start?["line"]?.GetValue<int>()      ?? 0,
                    StartCharacter: start?["character"]?.GetValue<int>() ?? 0,
                    EndLine:        end?["line"]?.GetValue<int>()        ?? 0,
                    EndCharacter:   end?["character"]?.GetValue<int>()   ?? 0,
                    Severity:       d["severity"]?.GetValue<int>()       ?? 1,
                    Message:        d["message"]?.GetValue<string>()     ?? string.Empty,
                    Source:         d["source"]?.GetValue<string>()));
            }
            catch { /* skip malformed diagnostic entries */ }
        }

        _registry.AddDiagnostics(fileUri, result);
    }

    // -----------------------------------------------------------------------
    // Crash recovery
    // -----------------------------------------------------------------------

    private async Task HandleUnexpectedExitAsync()
    {
        if (_restartCount >= MaxRestarts)
        {
            State = LspServerState.Error;
            await Console.Error.WriteLineAsync(
                    $"[LSP:{Config.Name}] Exceeded max restarts ({MaxRestarts}). Server is in error state.")
                .ConfigureAwait(false);
            return;
        }

        State = LspServerState.Restarting;
        var delay = RestartDelays[_restartCount];
        _restartCount++;

        await Console.Error.WriteLineAsync(
                $"[LSP:{Config.Name}] Process exited unexpectedly. "
                + $"Restarting in {delay.TotalMilliseconds} ms "
                + $"(attempt {_restartCount}/{MaxRestarts}).")
            .ConfigureAwait(false);

        try
        {
            await Task.Delay(delay).ConfigureAwait(false);

            _process?.Dispose();
            _process      = null;
            _readLoopTask = null;

            await StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            State = LspServerState.Error;
            await Console.Error.WriteLineAsync(
                    $"[LSP:{Config.Name}] Restart attempt {_restartCount} failed: {ex.Message}")
                .ConfigureAwait(false);
        }
    }

    // -----------------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch { /* best-effort */ }
        finally
        {
            _cts.Dispose();
            _writeLock.Dispose();
            _pendingLock.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Static helpers
    // -----------------------------------------------------------------------

    private static async ValueTask<int> ReadByteAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[1];
        var n   = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
        return n == 0 ? -1 : buf[0];
    }

    private static string ToFileUri(string path) =>
        $"file:///{path.Replace('\\', '/').TrimStart('/')}";
}
