namespace ClaudeCode.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCode.Mcp.Transport;

/// <summary>
/// Metadata about a tool exposed by an MCP server.
/// </summary>
/// <param name="Name">The tool's canonical name as reported by the server.</param>
/// <param name="Description">Human-readable description, or <see langword="null"/> if not provided.</param>
/// <param name="InputSchema">JSON Schema for the tool's input parameters, or <see langword="null"/> if not provided.</param>
public record McpToolInfo(string Name, string? Description, JsonElement? InputSchema);

/// <summary>
/// The result of a <c>tools/call</c> invocation against an MCP server.
/// </summary>
/// <param name="Content">The content block(s) returned by the server, or <see langword="null"/> on failure.</param>
/// <param name="IsError">
/// <see langword="true"/> when the server flagged the result as an error or when the transport returned a JSON-RPC error.
/// </param>
public record McpCallResult(JsonElement? Content, bool IsError);

/// <summary>Metadata about a resource exposed by an MCP server.</summary>
public record McpResourceInfo(string Uri, string Name, string? Description, string? MimeType);

/// <summary>The content of a resource read from an MCP server.</summary>
public record McpResourceContent(string Uri, string? MimeType, string Text);

/// <summary>
/// High-level MCP client that manages the lifecycle of a single server connection.
/// Use <see cref="ConnectAsync"/> to obtain an initialized instance.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly IMcpTransport _transport;

    /// <summary>
    /// Handles incoming <c>elicitation/create</c> requests from the server.
    /// Initialised lazily on first use so that sessions without elicitation carry no overhead.
    /// </summary>
    private readonly ElicitationHandler _elicitationHandler = new();

    /// <summary>
    /// The logical name assigned to this server connection (used to namespace tool names).
    /// </summary>
    public string ServerName { get; }

    /// <summary>
    /// Returns <see langword="true"/> after <c>initialize</c> / <c>notifications/initialized</c>
    /// have been exchanged successfully.
    /// </summary>
    public bool IsInitialized { get; private set; }

    private McpClient(IMcpTransport transport, string serverName)
    {
        _transport = transport;
        ServerName = serverName;
    }

    /// <summary>
    /// Starts the server process, performs the MCP handshake, and returns a ready client.
    /// </summary>
    /// <param name="serverName">Logical name for this server; used to namespace tool names.</param>
    /// <param name="command">Executable to launch.</param>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="workingDir">Working directory, or <see langword="null"/> for the current directory.</param>
    /// <param name="env">Optional environment variable overrides.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An initialized <see cref="McpClient"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="serverName"/> or <paramref name="command"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the MCP initialize handshake fails.</exception>
    public static async Task<McpClient> ConnectAsync(
        string serverName,
        string command,
        string[] args,
        string? workingDir = null,
        Dictionary<string, string>? env = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(args);

        var transport = StdioTransport.Start(command, args, workingDir, env);
        var client = new McpClient(transport, serverName);
        await client.InitializeAsync(ct).ConfigureAwait(false);
        return client;
    }

    /// <summary>
    /// Connects to an MCP server over HTTP/SSE transport and returns a ready client.
    /// </summary>
    /// <param name="serverName">Logical name for this server; used to namespace tool names.</param>
    /// <param name="url">Base URL of the SSE/HTTP MCP server endpoint.</param>
    /// <param name="headers">Optional HTTP headers to include with every request.</param>
    /// <param name="apiKey">Optional Bearer token for authentication.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An initialized <see cref="McpClient"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="serverName"/> or <paramref name="url"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the MCP initialize handshake fails.</exception>
    public static async Task<McpClient> ConnectHttpAsync(
        string serverName,
        string url,
        Dictionary<string, string>? headers = null,
        string? apiKey = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var transport = new Transport.HttpSseTransport(url, headers, apiKey);
        await transport.StartAsync(ct).ConfigureAwait(false);
        var client = new McpClient(transport, serverName);
        await client.InitializeAsync(ct).ConfigureAwait(false);
        return client;
    }

    /// <summary>
    /// Accepts an already-connected transport, performs the MCP handshake, and returns a ready client.
    /// Use this overload when the transport has already been initialized externally
    /// (e.g. <see cref="Transport.WebSocketTransport.ConnectAsync"/>).
    /// </summary>
    /// <param name="serverName">Logical name for this server; used to namespace tool names.</param>
    /// <param name="transport">An already-started transport. Ownership transfers to the returned client.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An initialized <see cref="McpClient"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="serverName"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transport"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the MCP initialize handshake fails.</exception>
    public static async Task<McpClient> ConnectWithTransportAsync(
        string serverName,
        IMcpTransport transport,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(transport);

        var client = new McpClient(transport, serverName);
        await client.InitializeAsync(ct).ConfigureAwait(false);
        return client;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var response = await _transport.SendRequestAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "ClaudeCode", version = "0.1.0" },
        }, ct).ConfigureAwait(false);

        if (response.Error is not null)
            throw new InvalidOperationException($"MCP initialize failed: {response.Error.Message}");

        await _transport.SendNotificationAsync("notifications/initialized", ct: ct).ConfigureAwait(false);
        IsInitialized = true;
    }

    /// <summary>
    /// Retrieves the list of tools exposed by this MCP server.
    /// Returns an empty list when the server returns an error or no tools.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
        var response = await _transport.SendRequestAsync("tools/list", ct: ct).ConfigureAwait(false);
        if (response.Error is not null || response.Result is null)
            return [];

        var tools = new List<McpToolInfo>();
        if (response.Result.Value.TryGetProperty("tools", out var toolsArray))
        {
            foreach (var tool in toolsArray.EnumerateArray())
            {
                var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null) continue;

                var desc = tool.TryGetProperty("description", out var d) ? d.GetString() : null;
                JsonElement? schema = tool.TryGetProperty("inputSchema", out var s) ? s : null;

                tools.Add(new McpToolInfo(name, desc, schema));
            }
        }

        return tools;
    }

    /// <summary>
    /// Invokes the named tool on the MCP server with the provided arguments.
    /// </summary>
    /// <param name="toolName">The server-side tool name (not the qualified wrapper name).</param>
    /// <param name="arguments">JSON arguments to pass, or <see langword="null"/> for no arguments.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="McpCallResult"/> containing the content and error flag.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="toolName"/> is null or whitespace.</exception>
    public async Task<McpCallResult> CallToolAsync(
        string toolName,
        JsonElement? arguments = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var response = await _transport.SendRequestAsync("tools/call", new
        {
            name = toolName,
            arguments,
        }, ct).ConfigureAwait(false);

        if (response.Error is not null)
            return new McpCallResult(null, true);

        if (response.Result is null)
            return new McpCallResult(null, false);

        var isError = response.Result.Value.TryGetProperty("isError", out var e) && e.GetBoolean();
        response.Result.Value.TryGetProperty("content", out var content);
        return new McpCallResult(content, isError);
    }

    /// <summary>
    /// Lists resources exposed by this MCP server via <c>resources/list</c>.
    /// Returns an empty list when the server returns an error or does not support resources.
    /// </summary>
    public async Task<List<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default)
    {
        var response = await _transport.SendRequestAsync("resources/list", ct: ct).ConfigureAwait(false);
        if (response.Error is not null || response.Result is null)
            return [];

        var resources = new List<McpResourceInfo>();
        if (response.Result.Value.TryGetProperty("resources", out var arr))
        {
            foreach (var res in arr.EnumerateArray())
            {
                var uri  = res.TryGetProperty("uri",  out var u) ? u.GetString() : null;
                var name = res.TryGetProperty("name", out var n) ? n.GetString() : null;
                var desc = res.TryGetProperty("description", out var d) ? d.GetString() : null;
                var mime = res.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
                if (uri is null) continue;
                resources.Add(new McpResourceInfo(uri, name ?? uri, desc, mime));
            }
        }
        return resources;
    }

    /// <summary>
    /// Reads the content of a resource via <c>resources/read</c>.
    /// Returns <see langword="null"/> when the server returns an error.
    /// </summary>
    public async Task<McpResourceContent?> ReadResourceAsync(string uri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        var response = await _transport.SendRequestAsync("resources/read", new { uri }, ct)
            .ConfigureAwait(false);

        if (response.Error is not null || response.Result is null)
            return null;

        // The result contains a "contents" array; take the first element.
        if (!response.Result.Value.TryGetProperty("contents", out var contents) ||
            contents.ValueKind != JsonValueKind.Array ||
            contents.GetArrayLength() == 0)
            return null;

        var first = contents[0];
        var resUri  = first.TryGetProperty("uri",      out var ru) ? ru.GetString() : uri;
        var mime    = first.TryGetProperty("mimeType", out var rm) ? rm.GetString() : null;
        string? text = null;

        if (first.TryGetProperty("text", out var textEl))
            text = textEl.GetString();
        else if (first.TryGetProperty("blob", out var blobEl))
            text = $"[base64 blob] {blobEl.GetString()?[..Math.Min(100, blobEl.GetString()?.Length ?? 0)]}...";

        return new McpResourceContent(resUri ?? uri, mime, text ?? "(empty)");
    }

    /// <summary>
    /// Returns <see langword="true"/> when the underlying transport is still running.
    /// </summary>
    public bool IsAlive => _transport.IsRunning;

    /// <summary>
    /// Handles an MCP elicitation request by delegating to <see cref="ElicitationHandler"/>
    /// to collect structured user input, then forwarding the response to the server via a
    /// <c>elicitation/response</c> notification.
    /// </summary>
    /// <remarks>
    /// The MCP elicitation/create protocol allows a server to request user input mid-operation.
    /// Because the current transport is client-initiated (request/response only), this method
    /// adapts the protocol: the collected input is returned and also forwarded via a
    /// <c>elicitation/response</c> notification.
    /// </remarks>
    /// <param name="schema">
    /// Optional JSON schema describing the expected input fields.
    /// When present and containing a <c>properties</c> object, each property is prompted individually.
    /// </param>
    /// <param name="message">Message to display to the user, or <see langword="null"/> for a default prompt.</param>
    /// <param name="requestId">The originating request ID, forwarded in the response notification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user's collected input serialized as a JSON string.</returns>
    public async Task<string> HandleElicitationAsync(
        JsonElement? schema,
        string? message,
        int? requestId = null,
        CancellationToken ct = default)
    {
        // Build a synthetic elicitation request element for ElicitationHandler.
        var requestBuilder = new System.Text.Json.Nodes.JsonObject();
        if (message is not null)
            requestBuilder["message"] = message;
        if (schema.HasValue)
            requestBuilder["requestedSchema"] = System.Text.Json.Nodes.JsonNode.Parse(schema.Value.GetRawText());

        var requestElement = System.Text.Json.JsonSerializer.SerializeToElement(requestBuilder);
        var responseElement = await _elicitationHandler.HandleAsync(requestElement, ct).ConfigureAwait(false);

        var userInput = responseElement.GetRawText();

        // Send collected input back to the server via notification (adapted from
        // SendResponseAsync — the current transport supports only client-initiated requests).
        await _transport.SendNotificationAsync(
            "elicitation/response",
            new { requestId, content = userInput },
            ct).ConfigureAwait(false);

        return userInput;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
