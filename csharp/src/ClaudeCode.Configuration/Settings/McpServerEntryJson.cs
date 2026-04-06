namespace ClaudeCode.Configuration.Settings;

using System.Text.Json.Serialization;

/// <summary>
/// JSON schema for a single MCP server entry in settings.json under "mcpServers".
/// </summary>
public sealed class McpServerEntryJson
{
    /// <summary>Transport type: "stdio" (default), "sse", or "http".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    // --- stdio fields ---

    /// <summary>Executable to launch (stdio transport).</summary>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    /// <summary>Arguments to pass to the process (stdio transport).</summary>
    [JsonPropertyName("args")]
    public string[]? Args { get; init; }

    /// <summary>Working directory override (stdio transport).</summary>
    [JsonPropertyName("workingDir")]
    public string? WorkingDir { get; init; }

    /// <summary>Environment variable overrides (stdio transport).</summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; init; }

    // --- HTTP/SSE fields ---

    /// <summary>Base URL for SSE or HTTP transport.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>Optional Bearer token for HTTP/SSE transport authentication.</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; init; }

    /// <summary>Optional HTTP headers for HTTP/SSE transport.</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>Whether this server is disabled. Default false.</summary>
    [JsonPropertyName("disabled")]
    public bool? Disabled { get; init; }

    // --- OAuth / Authentication fields ---

    /// <summary>OAuth authorization endpoint URL for PKCE flow.</summary>
    [JsonPropertyName("authorizationUrl")]
    public string? AuthorizationUrl { get; init; }

    /// <summary>OAuth token exchange endpoint URL.</summary>
    [JsonPropertyName("tokenUrl")]
    public string? TokenUrl { get; init; }

    /// <summary>OAuth client ID for the PKCE flow.</summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    /// <summary>OAuth scopes to request during the PKCE authorization flow.</summary>
    [JsonPropertyName("scopes")]
    public List<string>? Scopes { get; init; }

    // --- Connection fields ---

    /// <summary>Connection timeout in seconds. Default is 30.</summary>
    [JsonPropertyName("timeout")]
    public int? Timeout { get; init; }

    /// <summary>
    /// When true, tools from this server are treated as trusted
    /// and bypass interactive permission prompts.
    /// </summary>
    [JsonPropertyName("trust")]
    public bool? Trust { get; init; }

    /// <summary>
    /// Explicit enabled flag. When <see langword="false"/> the server is
    /// treated as disabled regardless of the <see cref="Disabled"/> flag.
    /// When <see langword="null"/> the server is considered enabled by default.
    /// Uses a mutable setter so the MCP subcommands can toggle it in-place
    /// on a deserialized instance before re-serialising to disk.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    // --- Channel permission fields ---

    /// <summary>
    /// Optional allow-list of tool name patterns (exact or glob ending with <c>*</c>).
    /// When non-null and non-empty, only matching tools are permitted on this server channel.
    /// <see cref="BlockedTools"/> takes precedence when a tool matches both lists.
    /// </summary>
    [JsonPropertyName("allowedTools")]
    public List<string>? AllowedTools { get; init; }

    /// <summary>
    /// Optional block-list of tool name patterns (exact or glob ending with <c>*</c>).
    /// Tools matching any pattern in this list are always denied on this server channel,
    /// regardless of <see cref="AllowedTools"/>.
    /// </summary>
    [JsonPropertyName("blockedTools")]
    public List<string>? BlockedTools { get; init; }
}
