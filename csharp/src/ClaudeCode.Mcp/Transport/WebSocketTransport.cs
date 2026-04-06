namespace ClaudeCode.Mcp.Transport;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ClaudeCode.Mcp.JsonRpc;

/// <summary>
/// MCP transport over WebSocket (ws:// or wss:// URL).
/// Sends JSON-RPC requests and receives responses over a persistent WebSocket connection.
/// </summary>
public sealed class WebSocketTransport : IMcpTransport, IAsyncDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly string _url;
    private readonly Channel<JsonElement> _incoming;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private int _nextId;

    /// <inheritdoc/>
    public bool IsRunning => _ws.State == WebSocketState.Open;

    private WebSocketTransport(ClientWebSocket ws, string url)
    {
        _ws = ws;
        _url = url;
        _incoming = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });
        _receiveLoop = RunReceiveLoopAsync(_cts.Token);
    }

    /// <summary>Connects to the given WebSocket URL and returns a ready transport.</summary>
    /// <param name="url">The ws:// or wss:// URL to connect to.</param>
    /// <param name="headers">Optional HTTP headers to include in the upgrade request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An initialized <see cref="WebSocketTransport"/> with a live connection.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="url"/> is null or whitespace.</exception>
    public static async Task<WebSocketTransport> ConnectAsync(
        string url, Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var ws = new ClientWebSocket();
        if (headers is not null)
            foreach (var (k, v) in headers)
                ws.Options.SetRequestHeader(k, v);

        await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
        return new WebSocketTransport(ws, url);
    }

    /// <inheritdoc/>
    public async Task<JsonRpcResponse> SendRequestAsync(
        string method, object? paramsObj = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new { jsonrpc = "2.0", id, method, @params = paramsObj };
        var json = JsonSerializer.Serialize(request,
            new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
            .ConfigureAwait(false);

        // Wait for a response with matching id.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        while (await _incoming.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
        {
            if (_incoming.Reader.TryRead(out var msg))
            {
                if (msg.TryGetProperty("id", out var msgIdEl) &&
                    msgIdEl.TryGetInt32(out var msgIdVal) &&
                    msgIdVal == id)
                {
                    JsonRpcError? error = null;
                    if (msg.TryGetProperty("error", out var errEl))
                    {
                        error = new JsonRpcError
                        {
                            Code = errEl.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : -1,
                            Message = errEl.TryGetProperty("message", out var msgEl)
                                ? msgEl.GetString() ?? "Unknown error"
                                : "Unknown error",
                        };
                    }

                    JsonElement? result = msg.TryGetProperty("result", out var resultEl) ? resultEl : null;
                    return new JsonRpcResponse { Id = id, Result = result, Error = error };
                }

                // Not our response — put it back (best-effort via re-enqueue).
                _incoming.Writer.TryWrite(msg);
            }
        }

        throw new OperationCanceledException("WebSocket transport closed while waiting for response.");
    }

    /// <inheritdoc/>
    public async Task SendNotificationAsync(
        string method, object? paramsObj = null, CancellationToken ct = default)
    {
        var notification = new { jsonrpc = "2.0", method, @params = paramsObj };
        var json = JsonSerializer.Serialize(notification,
            new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _incoming.Writer.TryComplete();

        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "disposing", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch { /* best-effort */ }

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { }
        }

        _ws.Dispose();
        _cts.Dispose();
    }

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];
        var sb = new StringBuilder();

        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                var doc = JsonDocument.Parse(sb.ToString());
                await _incoming.Writer.WriteAsync(doc.RootElement.Clone(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { _incoming.Writer.TryComplete(); }
    }
}
