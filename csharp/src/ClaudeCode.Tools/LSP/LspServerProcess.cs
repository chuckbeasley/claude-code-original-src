namespace ClaudeCode.Tools.LSP;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Manages a Language Server Protocol server process over stdio JSON-RPC 2.0.
/// Started lazily on first request, stopped on dispose.
/// </summary>
internal sealed class LspServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private int _nextId = 1;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // key = request id, value = TaskCompletionSource for the response
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _pendingLock = new(1, 1);

    // Tracks which file URIs have been sent textDocument/didOpen for this server session.
    private readonly HashSet<string> _openedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _openedFilesLock = new(1, 1);

    private Task? _readLoop;

    /// <summary>Returns <see langword="true"/> when the underlying process has exited.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>
    /// Starts the language server process, performs the LSP initialize/initialized handshake,
    /// and begins the background read loop.
    /// </summary>
    /// <param name="command">Executable name or path (e.g. <c>pylsp</c>).</param>
    /// <param name="args">Additional command-line arguments (e.g. <c>["--stdio"]</c>).</param>
    /// <param name="cwd">Working directory — used as the <c>rootUri</c> during initialization.</param>
    /// <param name="ct">Cancellation token for the start-up phase.</param>
    public static async Task<LspServerProcess> StartAsync(
        string command, string[] args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = cwd,
            StandardInputEncoding  = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start LSP server: {command}");

        var server = new LspServerProcess(proc);
        server._readLoop = server.ReadLoopAsync(ct);

        // Send initialize request; discard result — we only care that it completes.
        _ = await server.SendRequestAsync("initialize", new JsonObject
        {
            ["processId"]   = Environment.ProcessId,
            ["rootUri"]     = ToFileUri(cwd),
            ["capabilities"] = new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["hover"]      = new JsonObject { ["contentFormat"] = new JsonArray("plaintext") },
                    ["definition"] = new JsonObject(),
                    ["references"] = new JsonObject(),
                    ["completion"] = new JsonObject(),
                    ["diagnostic"] = new JsonObject(),
                },
            },
            ["initializationOptions"] = new JsonObject(),
        }, ct).ConfigureAwait(false);

        // Send initialized notification (no id = notification, not a request).
        await server.SendNotificationAsync("initialized", new JsonObject(), ct).ConfigureAwait(false);

        return server;
    }

    private LspServerProcess(Process process) => _process = process;

    // -----------------------------------------------------------------------
    // File tracking
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sends <c>textDocument/didOpen</c> the first time a file is referenced.
    /// Subsequent calls for the same URI are no-ops.
    /// </summary>
    /// <param name="filePath">Absolute filesystem path to the file.</param>
    /// <param name="fileUri">The <c>file:///</c> URI for the file (pre-computed by the caller).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task EnsureFileOpenedAsync(string filePath, string fileUri, CancellationToken ct)
    {
        await _openedFilesLock.WaitAsync(ct).ConfigureAwait(false);
        bool alreadyOpen;
        try
        {
            alreadyOpen = !_openedFiles.Add(fileUri);
        }
        finally { _openedFilesLock.Release(); }

        if (alreadyOpen) return;

        var content    = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        var languageId = GetLanguageId(Path.GetExtension(filePath));

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

    // -----------------------------------------------------------------------
    // JSON-RPC messaging
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sends a JSON-RPC request with the given <paramref name="method"/> and waits for the response.
    /// Times out after 10 seconds.
    /// </summary>
    /// <param name="method">LSP method name (e.g. <c>textDocument/hover</c>).</param>
    /// <param name="paramsObj">Optional request parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response object from the language server.</returns>
    public async Task<JsonObject> SendRequestAsync(
        string method, JsonObject? paramsObj, CancellationToken ct)
    {
        int id;
        TaskCompletionSource<JsonObject> tcs;

        await _pendingLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            id  = _nextId++;
            tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
        }
        finally { _pendingLock.Release(); }

        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id,
            ["method"]  = method,
        };
        if (paramsObj is not null) msg["params"] = paramsObj;

        await WriteMessageAsync(msg, ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a JSON-RPC notification (no <c>id</c> field; no response expected).
    /// </summary>
    /// <param name="method">LSP method name.</param>
    /// <param name="paramsObj">Optional notification parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendNotificationAsync(
        string method, JsonObject? paramsObj, CancellationToken ct)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (paramsObj is not null) msg["params"] = paramsObj;
        await WriteMessageAsync(msg, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Transport layer
    // -----------------------------------------------------------------------

    private async Task WriteMessageAsync(JsonObject msg, CancellationToken ct)
    {
        var body   = Encoding.UTF8.GetBytes(msg.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(header, ct).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.WriteAsync(body, ct).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var stream = _process.StandardOutput.BaseStream;

        try
        {
            while (!ct.IsCancellationRequested && !_process.HasExited)
            {
                // Read HTTP-style headers: lines ending in \r\n, terminated by an empty \r\n line.
                int contentLength = 0;

                while (true)
                {
                    var lineBuf = new List<byte>();
                    int b;
                    while ((b = await ReadByteAsync(stream, ct).ConfigureAwait(false)) != -1)
                    {
                        lineBuf.Add((byte)b);
                        if (lineBuf.Count >= 2 && lineBuf[^2] == '\r' && lineBuf[^1] == '\n')
                            break;
                    }

                    if (b == -1) return; // stream closed

                    var line = Encoding.ASCII.GetString(lineBuf.ToArray()).TrimEnd();
                    if (line.Length == 0) break; // empty line = end of headers

                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                }

                if (contentLength == 0) continue;

                // Read exactly contentLength bytes.
                var body = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int n = await stream.ReadAsync(body.AsMemory(read, contentLength - read), ct)
                        .ConfigureAwait(false);
                    if (n == 0) return; // stream closed
                    read += n;
                }

                // Parse and dispatch to the waiting request.
                try
                {
                    var doc = JsonNode.Parse(body);
                    if (doc is JsonObject obj
                        && obj.TryGetPropertyValue("id", out var idNode)
                        && idNode is not null)
                    {
                        int responseId = idNode.GetValue<int>();

                        await _pendingLock.WaitAsync(ct).ConfigureAwait(false);
                        _pending.TryGetValue(responseId, out var pendingTcs);
                        _pending.Remove(responseId);
                        _pendingLock.Release();

                        pendingTcs?.TrySetResult(obj);
                    }
                    // Notifications (no id) are silently ignored.
                }
                catch { /* ignore individual parse errors */ }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* process exited or pipe closed */ }
        finally
        {
            // Cancel all in-flight requests so callers do not hang indefinitely.
            await _pendingLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var tcs in _pending.Values)
                tcs.TrySetCanceled();
            _pending.Clear();
            _pendingLock.Release();
        }
    }

    private static async ValueTask<int> ReadByteAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[1];
        int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
        return n == 0 ? -1 : buf[0];
    }

    // -----------------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                // Best-effort graceful shutdown notification.
                await SendNotificationAsync("shutdown", null, CancellationToken.None)
                    .ConfigureAwait(false);

                _process.StandardInput.Close();

                await Task.WhenAny(
                    _process.WaitForExitAsync(),
                    Task.Delay(2_000)).ConfigureAwait(false);

                if (!_process.HasExited) _process.Kill();
            }
        }
        catch { }
        finally
        {
            _process.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Static helpers
    // -----------------------------------------------------------------------

    internal static string ToFileUri(string path) =>
        $"file:///{path.Replace('\\', '/').TrimStart('/')}";

    private static string GetLanguageId(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".py"           => "python",
            ".cs"           => "csharp",
            ".rs"           => "rust",
            ".go"           => "go",
            var e           => e.TrimStart('.'),
        };
}
