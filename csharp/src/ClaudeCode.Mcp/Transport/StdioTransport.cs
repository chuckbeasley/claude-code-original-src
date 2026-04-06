namespace ClaudeCode.Mcp.Transport;

using System.Diagnostics;
using System.Text.Json;
using ClaudeCode.Mcp.JsonRpc;

/// <summary>
/// MCP transport implementation that communicates with a child process over stdin/stdout
/// using newline-delimited JSON-RPC 2.0 messages.
/// </summary>
public sealed class StdioTransport : IMcpTransport
{
    private readonly Process _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextId;

    private StdioTransport(Process process) => _process = process;

    /// <summary>
    /// Starts a child process and returns a connected <see cref="StdioTransport"/>.
    /// </summary>
    /// <param name="command">Executable name or full path.</param>
    /// <param name="args">Arguments to pass to the process.</param>
    /// <param name="workingDir">Working directory for the process, or <see langword="null"/> to use the current directory.</param>
    /// <param name="env">Optional additional environment variables to merge into the child's environment.</param>
    /// <returns>A connected transport wrapping the started process.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the process fails to start.</exception>
    public static StdioTransport Start(
        string command,
        string[] args,
        string? workingDir = null,
        Dictionary<string, string>? env = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(args);

        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        if (env is not null)
        {
            foreach (var (key, value) in env)
                psi.Environment[key] = value;
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start MCP server: {command}");

        return new StdioTransport(process);
    }

    /// <summary>
    /// Sends a JSON-RPC request and waits for the matching response.
    /// Responses with a non-matching <c>id</c> (e.g. server-initiated notifications) are skipped.
    /// </summary>
    /// <param name="method">The JSON-RPC method name.</param>
    /// <param name="parameters">Optional parameter object; serialised to a <see cref="JsonElement"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="JsonRpcResponse"/> whose <c>id</c> matches this request.</returns>
    /// <exception cref="IOException">Thrown when the server closes stdout before a response is received.</exception>
    public async Task<JsonRpcResponse> SendRequestAsync(
        string method,
        object? parameters = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = parameters is not null ? JsonSerializer.SerializeToElement(parameters) : null,
        };

        var json = JsonSerializer.Serialize(request);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        // Read response lines until we get the response for our id.
        while (!ct.IsCancellationRequested)
        {
            var line = await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                throw new IOException("MCP server closed stdout");

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var response = JsonSerializer.Deserialize<JsonRpcResponse>(line);
                if (response?.Id == id)
                    return response;
                // Response id does not match — could be a notification or interleaved response; skip.
            }
            catch (JsonException)
            {
                // Skip malformed lines rather than aborting the session.
            }
        }

        throw new OperationCanceledException(ct);
    }

    /// <summary>
    /// Sends a JSON-RPC notification (no response expected).
    /// </summary>
    /// <param name="method">The JSON-RPC method name.</param>
    /// <param name="parameters">Optional parameter object; serialised to a <see cref="JsonElement"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendNotificationAsync(
        string method,
        object? parameters = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var notification = new JsonRpcNotification
        {
            Method = method,
            Params = parameters is not null ? JsonSerializer.SerializeToElement(parameters) : null,
        };

        var json = JsonSerializer.Serialize(notification);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the child process is still running.
    /// </summary>
    public bool IsRunning => !_process.HasExited;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            // Best-effort shutdown; never throw from DisposeAsync.
        }
        finally
        {
            _process.Dispose();
            _writeLock.Dispose();
        }
    }
}
