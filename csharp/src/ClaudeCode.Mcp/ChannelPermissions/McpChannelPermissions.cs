namespace ClaudeCode.Mcp.ChannelPermissions;

/// <summary>
/// Per-channel (per-MCP-server) permission settings.
/// Allows granting or blocking specific tools from specific MCP servers.
/// </summary>
public class McpChannelPermissions
{
    private readonly Dictionary<string, ChannelPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces the entire policy for the named server.
    /// </summary>
    /// <param name="serverName">The MCP server name (case-insensitive).</param>
    /// <param name="policy">The policy to apply. Must not be <see langword="null"/>.</param>
    public void SetPolicy(string serverName, ChannelPolicy policy) => _policies[serverName] = policy;

    /// <summary>
    /// Returns <see langword="true"/> when the named tool from the named server is permitted
    /// under the current policy. Default when no policy is registered is to allow all tools.
    /// Block-list patterns take precedence over allow-list patterns.
    /// Supports exact names and glob patterns ending with <c>*</c>.
    /// </summary>
    /// <param name="serverName">The MCP server name (case-insensitive).</param>
    /// <param name="toolName">The tool name to check.</param>
    public bool IsToolAllowed(string serverName, string toolName)
    {
        if (!_policies.TryGetValue(serverName, out var policy)) return true; // default allow
        if (policy.DeniedTools.Any(p => MatchesPattern(p, toolName))) return false;
        if (policy.AllowedTools.Count > 0) return policy.AllowedTools.Any(p => MatchesPattern(p, toolName));
        return true;
    }

    /// <summary>
    /// Grants permission for the named tool from the named server,
    /// removing any existing denial for the same tool.
    /// </summary>
    /// <param name="serverName">The MCP server name (case-insensitive).</param>
    /// <param name="toolName">The tool name to allow.</param>
    public void Allow(string serverName, string toolName)
    {
        var p = _policies.GetValueOrDefault(serverName, new ChannelPolicy());
        p.AllowedTools.Add(toolName);
        p.DeniedTools.Remove(toolName);
        _policies[serverName] = p;
    }

    /// <summary>
    /// Blocks the named tool from the named server,
    /// removing any existing allowance for the same tool.
    /// </summary>
    /// <param name="serverName">The MCP server name (case-insensitive).</param>
    /// <param name="toolName">The tool name to deny.</param>
    public void Deny(string serverName, string toolName)
    {
        var p = _policies.GetValueOrDefault(serverName, new ChannelPolicy());
        p.DeniedTools.Add(toolName);
        p.AllowedTools.Remove(toolName);
        _policies[serverName] = p;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Matches <paramref name="toolName"/> against a single pattern.
    /// <c>"*"</c> matches everything; patterns ending with <c>*</c> match by prefix;
    /// all other patterns use ordinal case-insensitive equality.
    /// </summary>
    private static bool MatchesPattern(string pattern, string toolName)
    {
        if (pattern == "*")
            return true;
        if (pattern.EndsWith('*'))
            return toolName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(pattern, toolName, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Bulk pattern-based API (used when loading from settings.json)
    // -------------------------------------------------------------------------
    /// <summary>
    /// Replaces the allow-list for <paramref name="serverName"/> with the supplied glob/exact
    /// tool name patterns. When the enumerable is empty, any existing allow-list is cleared
    /// (implying allow-all for that server, subject to the block-list).
    /// </summary>
    /// <param name="serverName">The MCP server name (case-insensitive). Must not be null or whitespace.</param>
    /// <param name="toolPatterns">
    /// Patterns to permit. Supports exact names and glob suffixes ending with <c>*</c>.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="serverName"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="toolPatterns"/> is null.</exception>
    public void SetAllowList(string serverName, IEnumerable<string> toolPatterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(toolPatterns);

        var p = _policies.GetValueOrDefault(serverName, new ChannelPolicy());
        p.AllowedTools.Clear();
        foreach (var pattern in toolPatterns)
            p.AllowedTools.Add(pattern);
        _policies[serverName] = p;
    }

    /// <summary>
    /// Replaces the block-list for <paramref name="serverName"/> with the supplied glob/exact
    /// tool name patterns. Block-list entries always take precedence over allow-list entries.
    /// </summary>
    /// <param name="serverName">The MCP server name (case-insensitive). Must not be null or whitespace.</param>
    /// <param name="toolPatterns">
    /// Patterns to deny. Supports exact names and glob suffixes ending with <c>*</c>.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="serverName"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="toolPatterns"/> is null.</exception>
    public void SetBlockList(string serverName, IEnumerable<string> toolPatterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(toolPatterns);

        var p = _policies.GetValueOrDefault(serverName, new ChannelPolicy());
        p.DeniedTools.Clear();
        foreach (var pattern in toolPatterns)
            p.DeniedTools.Add(pattern);
        _policies[serverName] = p;
    }
}

/// <summary>
/// Holds the allow-list and deny-list for a single MCP server channel.
/// </summary>
public class ChannelPolicy
{
    /// <summary>
    /// Explicit allow-list. When non-empty, only tools in this set are permitted
    /// (subject to <see cref="DeniedTools"/> taking precedence).
    /// </summary>
    public HashSet<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Explicit deny-list. Tools in this set are always blocked regardless of
    /// <see cref="AllowedTools"/>.
    /// </summary>
    public HashSet<string> DeniedTools { get; init; } = [];
}
