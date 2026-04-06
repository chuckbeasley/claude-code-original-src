namespace ClaudeCode.Mcp.Transport;

using ClaudeCode.Mcp.JsonRpc;

/// <summary>Common interface for MCP transports (stdio, HTTP/SSE, etc.).</summary>
public interface IMcpTransport : IAsyncDisposable
{
    bool IsRunning { get; }
    Task<JsonRpcResponse> SendRequestAsync(string method, object? paramsObj = null, CancellationToken ct = default);
    Task SendNotificationAsync(string method, object? paramsObj = null, CancellationToken ct = default);
}
