namespace ClaudeCode.Cli.Bridge;

using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeCode.Services.Engine;

/// <summary>
/// WebSocket bridge server that IDE extensions (VS Code, JetBrains) connect to for
/// bidirectional communication with the active REPL session.
/// </summary>
/// <remarks>
/// <para>
/// The server listens on <c>http://localhost:{port}/</c> (default port 7891, or the value of
/// the <c>CLAUDE_BRIDGE_PORT</c> environment variable) and accepts WebSocket upgrade requests.
/// </para>
/// <para>
/// <b>Authentication:</b> a random 24-byte bearer token is generated at construction and printed
/// by the <c>/bridge start</c> command. Clients must send
/// <c>Authorization: Bearer {token}</c> in the WebSocket handshake request.
/// </para>
/// <para>
/// <b>Protocol:</b> JSON messages with the shape <c>{ "type": "...", "payload": ... }</c>.
/// Supported message types:
/// <list type="bullet">
///   <item><c>ping</c> → responds with <c>{ "type": "pong" }</c>.</item>
///   <item><c>query</c> (payload: <c>{ "text": "..." }</c>) → submits the text as a user turn,
///         streams <c>{ "type": "token", "payload": "..." }</c> messages, ends with
///         <c>{ "type": "done" }</c>.</item>
///   <item><c>getStatus</c> → responds with
///         <c>{ "type": "status", "payload": { "model": "...", "sessionId": "...", "connected": true } }</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Concurrency note:</b> bridge queries are dispatched via the same <see cref="QueryEngine"/>
/// instance as the interactive REPL. Concurrent queries from the bridge and the REPL are not
/// serialised — callers should avoid issuing bridge queries while an interactive turn is in
/// progress.
/// </para>
/// </remarks>
internal sealed class BridgeServer : IDisposable
{
    private const int DefaultPort = 7891;
    private const string BridgePortEnvVar = "CLAUDE_BRIDGE_PORT";

    private readonly Func<string, CancellationToken, IAsyncEnumerable<QueryEvent>> _queryFunc;
    private readonly Func<(string model, string sessionId)> _statusFunc;
    private readonly HttpListener _listener;
    private readonly int _port;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private bool _disposed;

    // -------------------------------------------------------------------------
    // Public surface
    // -------------------------------------------------------------------------

    /// <summary>The random bearer token that IDE extensions must present to connect.</summary>
    public string Token { get; } = GenerateToken();

    /// <summary>The TCP port this server listens on.</summary>
    public int Port => _port;

    /// <summary>
    /// <see langword="true"/> while the accept loop is running (i.e. the server has been
    /// started and has not yet stopped).
    /// </summary>
    public bool IsRunning => _serverTask is { IsCompleted: false };

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Initialises a new <see cref="BridgeServer"/>.
    /// </summary>
    /// <param name="queryFunc">
    /// Delegate invoked for <c>query</c> messages. Receives the user text and a cancellation
    /// token, and must return a stream of <see cref="QueryEvent"/> values. Must not be
    /// <see langword="null"/>.
    /// </param>
    /// <param name="statusFunc">
    /// Delegate that returns the current <c>(model, sessionId)</c> pair for <c>getStatus</c>
    /// responses. Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="queryFunc"/> or <paramref name="statusFunc"/> is
    /// <see langword="null"/>.
    /// </exception>
    public BridgeServer(
        Func<string, CancellationToken, IAsyncEnumerable<QueryEvent>> queryFunc,
        Func<(string model, string sessionId)> statusFunc)
    {
        _queryFunc   = queryFunc   ?? throw new ArgumentNullException(nameof(queryFunc));
        _statusFunc  = statusFunc  ?? throw new ArgumentNullException(nameof(statusFunc));

        _port = int.TryParse(
            Environment.GetEnvironmentVariable(BridgePortEnvVar), out var p) ? p : DefaultPort;

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts the bridge server. No-ops when already running.
    /// </summary>
    /// <param name="ct">
    /// Cancellation token. Cancelling it is equivalent to calling <see cref="StopAsync"/>.
    /// </param>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return Task.CompletedTask;

        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener.Start();
        _serverTask = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the bridge server and waits for the accept loop to exit.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is { } cts)
            await cts.CancelAsync().ConfigureAwait(false);

        try { _listener.Stop(); } catch { /* best-effort */ }

        if (_serverTask is not null)
            await _serverTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { StopAsync().GetAwaiter().GetResult(); } catch { /* best-effort */ }
        _cts?.Dispose();
    }

    // -------------------------------------------------------------------------
    // Private — accept loop
    // -------------------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException)   { break; }
            catch (OperationCanceledException) { break; }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            // Validate bearer token from the Authorization header.
            var auth = context.Request.Headers["Authorization"];
            if (auth is null
                || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                || auth[7..] != Token)
            {
                context.Response.StatusCode = 401;
                context.Response.Close();
                continue;
            }

            // Hand off to a fire-and-forget per-client handler.
            _ = HandleClientAsync(context, ct);
        }
    }

    // -------------------------------------------------------------------------
    // Private — WebSocket handler
    // -------------------------------------------------------------------------

    private async Task HandleClientAsync(HttpListenerContext context, CancellationToken ct)
    {
        WebSocket ws;
        try
        {
            var wsCtx = await context.AcceptWebSocketAsync(subProtocol: null)
                .ConfigureAwait(false);
            ws = wsCtx.WebSocket;
        }
        catch { return; }

        using (ws)
        {
            var buffer = new byte[8192];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await ws.CloseAsync(
                            WebSocketCloseStatus.NormalClosure, string.Empty, ct)
                            .ConfigureAwait(false);
                    }
                    catch { /* best-effort */ }
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await DispatchMessageAsync(ws, json, ct).ConfigureAwait(false);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Private — message dispatch
    // -------------------------------------------------------------------------

    private async Task DispatchMessageAsync(WebSocket ws, string json, CancellationToken ct)
    {
        JsonElement msg;
        try { msg = JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return; /* malformed JSON — ignore */ }

        if (!msg.TryGetProperty("type", out var typeEl)) return;

        switch (typeEl.GetString())
        {
            case "ping":
                await SendJsonAsync(ws, new { type = "pong" }, ct).ConfigureAwait(false);
                break;

            case "query":
                if (msg.TryGetProperty("payload", out var payloadEl)
                    && payloadEl.TryGetProperty("text", out var textEl))
                {
                    await HandleQueryAsync(ws, textEl.GetString() ?? string.Empty, ct)
                        .ConfigureAwait(false);
                }
                break;

            case "getStatus":
                var (model, sessionId) = _statusFunc();
                await SendJsonAsync(ws, new
                {
                    type    = "status",
                    payload = new { model, sessionId, connected = true },
                }, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleQueryAsync(WebSocket ws, string text, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _queryFunc(text, ct).ConfigureAwait(false))
            {
                if (evt is TextDeltaEvent delta)
                {
                    await SendJsonAsync(ws,
                        new { type = "token", payload = delta.Text }, ct)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* session is shutting down — do not send an error frame */
        }
        catch (Exception ex)
        {
            try
            {
                await SendJsonAsync(ws,
                    new { type = "error", payload = ex.Message }, ct)
                    .ConfigureAwait(false);
            }
            catch { /* best-effort */ }
        }
        finally
        {
            try
            {
                await SendJsonAsync(ws, new { type = "done" }, ct).ConfigureAwait(false);
            }
            catch { /* best-effort */ }
        }
    }

    // -------------------------------------------------------------------------
    // Private — helpers
    // -------------------------------------------------------------------------

    private static async Task SendJsonAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Generates a URL-safe base64 bearer token from 24 random bytes.</summary>
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
