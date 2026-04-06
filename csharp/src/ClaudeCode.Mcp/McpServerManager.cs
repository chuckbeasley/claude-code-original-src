namespace ClaudeCode.Mcp;

using ClaudeCode.Configuration.Settings;
using ClaudeCode.Mcp.ChannelPermissions;

/// <summary>
/// Configuration for a single MCP server process.
/// </summary>
/// <param name="Command">Executable name or full path to launch.</param>
/// <param name="Args">Arguments to pass to the process.</param>
/// <param name="WorkingDir">Working directory for the process, or <see langword="null"/> to use the current directory.</param>
/// <param name="Env">Optional environment variable overrides to merge into the child's environment.</param>
public record McpServerConfig(
    string Command,
    string[] Args,
    string? WorkingDir = null,
    Dictionary<string, string>? Env = null);

/// <summary>
/// Manages the lifecycle of multiple named MCP server connections.
/// Connections are keyed case-insensitively by server name.
/// </summary>
public sealed class McpServerManager : IAsyncDisposable
{
    private readonly Dictionary<string, McpClient> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, McpServerConfig> _configs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _authUrls =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, McpServerEntryJson> _entryConfigs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-channel (per-server) tool permission settings.
    /// Use <see cref="McpChannelPermissions.Allow"/> / <see cref="McpChannelPermissions.Deny"/>
    /// to gate specific tools from specific servers.
    /// </summary>
    public McpChannelPermissions ChannelPermissions { get; } = new();

    /// <summary>
    /// Connects to the named MCP server if not already connected (or if the existing connection is dead),
    /// and returns the active <see cref="McpClient"/>.
    /// </summary>
    /// <param name="name">Logical name for the server; used to namespace tool names.</param>
    /// <param name="config">Server process configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A ready, initialized <see cref="McpClient"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is <see langword="null"/>.</exception>
    public async Task<McpClient> ConnectAsync(
        string name,
        McpServerConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);

        if (_clients.TryGetValue(name, out var existing) && existing.IsAlive)
            return existing;

        var client = await McpClient.ConnectAsync(
            name, config.Command, config.Args, config.WorkingDir, config.Env, ct)
            .ConfigureAwait(false);

        _clients[name] = client;
        _configs[name] = config;
        return client;
    }

    /// <summary>
    /// Returns the active <see cref="McpClient"/> for the given name,
    /// or <see langword="null"/> if no connection with that name exists.
    /// </summary>
    /// <param name="name">Case-insensitive server name.</param>
    public McpClient? GetClient(string name) => _clients.GetValueOrDefault(name);

    /// <summary>
    /// Returns a read-only view of all registered server connections, keyed by name.
    /// </summary>
    public IReadOnlyDictionary<string, McpClient> GetAll() => _clients;

    /// <summary>
    /// Returns the <see cref="McpServerConfig"/> used when connecting the named server,
    /// or <see langword="null"/> if the server was connected via HTTP/SSE or is not registered.
    /// </summary>
    /// <param name="name">Case-insensitive server name.</param>
    public McpServerConfig? GetServerConfig(string name) => _configs.GetValueOrDefault(name);

    /// <summary>Registers an OAuth authorization URL for the named server.</summary>
    public void SetAuthorizationUrl(string name, string authorizationUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationUrl);
        _authUrls[name] = authorizationUrl;
    }

    /// <summary>Returns the OAuth authorization URL for the named server, or null.</summary>
    public string? GetAuthorizationUrl(string name) => _authUrls.GetValueOrDefault(name);

    /// <summary>
    /// Stores the full <see cref="McpServerEntryJson"/> settings for the named server,
    /// making OAuth fields (<c>TokenUrl</c>, <c>ClientId</c>, <c>Scopes</c>) queryable
    /// at authentication time.
    /// </summary>
    /// <param name="name">Logical server name (case-insensitive).</param>
    /// <param name="entry">The settings entry to store. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is <see langword="null"/>.</exception>
    public void RegisterEntryConfig(string name, McpServerEntryJson entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(entry);
        _entryConfigs[name] = entry;
    }

    /// <summary>
    /// Returns the <see cref="McpServerEntryJson"/> previously registered for the named server,
    /// or <see langword="null"/> if no entry config has been registered.
    /// </summary>
    /// <param name="name">Case-insensitive server name.</param>
    public McpServerEntryJson? GetServerEntryConfig(string name) =>
        _entryConfigs.GetValueOrDefault(name);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort shutdown; continue disposing remaining clients.
            }
        }

        _clients.Clear();
    }
}
