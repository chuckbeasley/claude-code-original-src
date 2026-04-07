namespace ClaudeCode.Mcp.Transport;

using ClaudeCode.Mcp.JsonRpc;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>
/// MCP transport that uses HTTP+SSE (Server-Sent Events) protocol.
/// Compatible with MCP servers using the 2024-11-05 SSE transport specification.
///
/// Protocol:
///   GET  {url}          → opens the SSE event stream (server → client)
///   POST {url}/message  → sends a JSON-RPC request (client → server)
/// </summary>
public sealed class HttpSseTransport : IMcpTransport
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _messageUrl;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonRpcResponse>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _sseReader;
    private string? _sessionId;
    private int _nextId;
    private bool _disposed;

    public bool IsRunning => !_disposed && (_sseReader is null || !_sseReader.IsCompleted);

    public HttpSseTransport(string url, Dictionary<string, string>? headers = null, string? apiKey = null)
    {
        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromMinutes(10);

        if (apiKey is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                _http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
            }
        }

        // Normalize URL — remove trailing slash
        _url = url.TrimEnd('/');
        _messageUrl = _url + "/message";
    }

    /// <summary>
    /// Opens the SSE stream and starts the background reader.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        // Open SSE stream
        var req = new HttpRequestMessage(HttpMethod.Get, _url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Extract session ID from response headers or query param
        if (response.Headers.TryGetValues("mcp-session-id", out var ids))
        {
            _sessionId = ids.FirstOrDefault();
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        _sseReader = Task.Run(() => ReadSseStreamAsync(stream, _cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Sends a JSON-RPC request and waits for the matching response over SSE.
    /// </summary>
    public async Task<JsonRpcResponse> SendRequestAsync(
        string method,
        object? paramsObj = null,
        CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var payload = new { jsonrpc = "2.0", id, method, @params = paramsObj };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var postUrl = _sessionId is not null ? $"{_messageUrl}?sessionId={_sessionId}" : _messageUrl;
            var resp = await _http.PostAsync(postUrl, content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(TimeSpan.FromSeconds(30));
            using (cts2.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Sends a JSON-RPC notification (no response expected).
    /// </summary>
    public async Task SendNotificationAsync(string method, object? paramsObj = null, CancellationToken ct = default)
    {
        var payload = new { jsonrpc = "2.0", method, @params = paramsObj };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var postUrl = _sessionId is not null ? $"{_messageUrl}?sessionId={_sessionId}" : _messageUrl;
        await _http.PostAsync(postUrl, content, ct).ConfigureAwait(false);
    }

    private async Task ReadSseStreamAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new System.IO.StreamReader(stream);
        string? eventType = null;
        var dataLines = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventType = line[6..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    dataLines.AppendLine(line[5..].Trim());
                }
                else if (line.Length == 0 && dataLines.Length > 0)
                {
                    // Dispatch event
                    var data = dataLines.ToString().Trim();
                    dataLines.Clear();

                    if (eventType == "message" || eventType is null)
                    {
                        DispatchMessage(data);
                    }

                    eventType = null;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[MCP SSE] Parse error: {ex.Message}");
            // Continue reading next events
        }
        finally
        {
            // Cancel all pending requests
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetException(new InvalidOperationException("SSE connection closed."));
            }

            _pending.Clear();
        }
    }

    private void DispatchMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();

            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
            {
                var response = new JsonRpcResponse
                {
                    Id = id,
                    Result = root.TryGetProperty("result", out var result) ? result : null,
                    Error = root.TryGetProperty("error", out var error)
                        ? new JsonRpcError
                        {
                            Code = error.TryGetProperty("code", out var code) ? code.GetInt32() : -1,
                            Message = error.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "Unknown error",
                        }
                        : null,
                };

                if (_pending.TryGetValue(id, out var tcs))
                {
                    tcs.TrySetResult(response);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP SSE] Cleanup error: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_sseReader is not null)
        {
            await _sseReader.ConfigureAwait(false);
        }

        _cts.Dispose();
        _http.Dispose();
    }
}
