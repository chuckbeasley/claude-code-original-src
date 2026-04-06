namespace ClaudeCode.Mcp;

using System.Text;
using System.Text.Json;
using ClaudeCode.Core.Tools;

/// <summary>
/// Wraps a remote MCP tool as a local <see cref="ITool"/> implementation so it can be
/// registered in a <see cref="ToolRegistry"/> and invoked by the query engine exactly
/// like any built-in tool.
/// </summary>
/// <remarks>
/// The qualified tool name follows the convention <c>mcp__{serverName}__{toolName}</c>,
/// matching the format used by the TypeScript Claude Code client.
/// </remarks>
public sealed class McpToolWrapper : ITool
{
    private readonly McpClient _client;
    private readonly McpToolInfo _toolInfo;
    private readonly string _qualifiedName;
    private readonly McpServerManager? _manager;

    /// <summary>
    /// Initializes a new <see cref="McpToolWrapper"/>.
    /// </summary>
    /// <param name="client">The <see cref="McpClient"/> that owns this tool.</param>
    /// <param name="toolInfo">Metadata describing the remote tool.</param>
    /// <param name="manager">
    /// Optional <see cref="McpServerManager"/> used to enforce channel-level permission policies.
    /// When <see langword="null"/>, no channel permission checks are performed.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> or <paramref name="toolInfo"/> is <see langword="null"/>.</exception>
    public McpToolWrapper(McpClient client, McpToolInfo toolInfo, McpServerManager? manager = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(toolInfo);

        _client = client;
        _toolInfo = toolInfo;
        _manager = manager;
        _qualifiedName = $"mcp__{client.ServerName}__{toolInfo.Name}";
    }

    /// <inheritdoc/>
    public string Name => _qualifiedName;

    /// <inheritdoc/>
    public string[] Aliases => [];

    /// <inheritdoc/>
    public string? SearchHint => _toolInfo.Description;

    /// <inheritdoc/>
    public int MaxResultSizeChars => 100_000;

    /// <inheritdoc/>
    public Task<string> GetDescriptionAsync(CancellationToken ct = default) =>
        Task.FromResult(_toolInfo.Description ?? $"MCP tool from {_client.ServerName}");

    /// <inheritdoc/>
    public Task<string> GetPromptAsync(CancellationToken ct = default) =>
        Task.FromResult(_toolInfo.Description ?? string.Empty);

    /// <inheritdoc/>
    public string UserFacingName(JsonElement? input = null) => _toolInfo.Name;

    /// <inheritdoc/>
    public string? GetActivityDescription(JsonElement? input = null) =>
        $"Running {_toolInfo.Name}";

    /// <inheritdoc/>
    public JsonElement GetInputSchema() =>
        _toolInfo.InputSchema ?? JsonSerializer.SerializeToElement(new { type = "object" });

    /// <inheritdoc/>
    /// <remarks>
    /// A tool is available only while its backing server process is alive.
    /// </remarks>
    public bool IsEnabled() => _client.IsAlive;

    /// <inheritdoc/>
    /// <remarks>
    /// Conservative default: all MCP tools are treated as potentially mutating.
    /// </remarks>
    public bool IsReadOnly(JsonElement input) => false;

    /// <inheritdoc/>
    /// <remarks>
    /// Conservative default: MCP tools are not assumed to be safe for concurrent invocation.
    /// </remarks>
    public bool IsConcurrencySafe(JsonElement input) => false;

    /// <inheritdoc/>
    public async Task<string> ExecuteRawAsync(
        JsonElement input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Channel-permission check: deny tools blocked by the per-server policy.
        if (_manager?.ChannelPermissions.IsToolAllowed(_client.ServerName, _toolInfo.Name) == false)
            throw new InvalidOperationException(
                $"Tool '{_toolInfo.Name}' from server '{_client.ServerName}' is blocked by channel permissions.");

        var result = await _client.CallToolAsync(_toolInfo.Name, input, ct).ConfigureAwait(false);

        if (result.IsError)
            return $"MCP tool error: {ExtractText(result.Content)}";

        return ExtractText(result.Content);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts a plain-text string from an MCP content block, which may be an array of
    /// typed content blocks, a plain string, or an arbitrary JSON value.
    /// </summary>
    private static string ExtractText(JsonElement? content)
    {
        if (content is null)
            return string.Empty;

        // Array of content blocks — extract all "text" blocks and concatenate.
        if (content.Value.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.Value.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    block.TryGetProperty("text", out var txt))
                {
                    if (sb.Length > 0)
                        sb.AppendLine();
                    sb.Append(txt.GetString());
                }
            }
            return sb.ToString();
        }

        // Plain string — return directly.
        if (content.Value.ValueKind == JsonValueKind.String)
            return content.Value.GetString() ?? string.Empty;

        // Fallback: return raw JSON for unexpected shapes.
        return content.Value.GetRawText();
    }
}
