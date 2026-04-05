namespace ClaudeCode.Configuration;

using ClaudeCode.Configuration.Settings;

/// <summary>
/// Runtime feature-flag system. Loads once at startup via <see cref="Load"/>.
/// Resolution order: environment variable &gt; settings.json entry &gt; hardcoded default (false).
/// Env var convention: CLAUDE_FEATURE_&lt;UPPERCASE_FLAG&gt; = 1|true|0|false|"".
/// </summary>
public static class FeatureFlags
{
    // Known flags with their hardcoded defaults.
    private static readonly Dictionary<string, bool> _defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cron"]           = false,
        ["sleep"]          = false,
        ["coordinator"]    = false,
        ["agent-triggers"] = false,
        ["voice"]          = false,
        ["kairos"]         = false,
        ["bridge"]         = false,
        ["proactive"]      = false,
    };

    // Effective flag table after merging all sources. Assigned atomically in Load().
    private static volatile Dictionary<string, bool> _flags = new(StringComparer.OrdinalIgnoreCase);

    // Source attribution table. Assigned atomically alongside _flags in Load().
    private static volatile Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);

    static FeatureFlags()
    {
        // Pre-populate with defaults so IsEnabled works before Load() is called.
        foreach (var (k, v) in _defaults)
            _flags[k] = v;
    }

    /// <summary>
    /// (Re-)initialises the flag table from <paramref name="config"/> and environment variables.
    /// Safe to call multiple times; each call rebuilds the table from scratch and assigns atomically.
    /// </summary>
    public static void Load(GlobalConfig? config)
    {
        var flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Start with hardcoded defaults.
        foreach (var (k, v) in _defaults)
            flags[k] = v;

        // Layer in settings.json overrides.
        if (config?.Features is { } settingsFlags)
            foreach (var (k, v) in settingsFlags)
                flags[k] = v;

        // Env vars take highest precedence.
        foreach (var key in flags.Keys.ToList())
        {
            var envName = $"CLAUDE_FEATURE_{key.ToUpperInvariant().Replace('-', '_')}";
            var raw = Environment.GetEnvironmentVariable(envName);
            if (raw is null) continue;

            // Empty string or "0" or "false" (case-insensitive) → false; anything else → true.
            flags[key] = !string.IsNullOrEmpty(raw)
                          && !raw.Equals("0", StringComparison.Ordinal)
                          && !raw.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        // Track source attribution for GetAll.
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in flags.Keys)
        {
            var envName = $"CLAUDE_FEATURE_{key.ToUpperInvariant().Replace('-', '_')}";
            if (Environment.GetEnvironmentVariable(envName) is not null)
                sources[key] = $"env ({envName})";
            else if (config?.Features?.ContainsKey(key) == true)
                sources[key] = "settings.json";
            else
                sources[key] = "default";
        }

        _sources = sources; // atomic assignment
        _flags   = flags;   // atomic assignment
    }

    /// <summary>
    /// Returns <see langword="true"/> when the named flag is enabled.
    /// Unknown flags return <see langword="false"/>.
    /// </summary>
    public static bool IsEnabled(string flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        return _flags.TryGetValue(flag, out var v) && v;
    }

    /// <summary>
    /// Returns a snapshot of all flag names and their effective values,
    /// along with the source that determined each value.
    /// Used by <c>/config features</c>.
    /// </summary>
    /// <param name="config">Kept for API compatibility; source attribution is tracked internally by <see cref="Load"/>.</param>
    public static IReadOnlyList<(string Flag, bool Value, string Source)> GetAll(GlobalConfig? config)
    {
        _ = config;          // source attribution is now captured in Load(); param retained for compatibility
        var current = _flags;    // snapshot
        var sources  = _sources; // snapshot

        var result = new List<(string Flag, bool Value, string Source)>();
        foreach (var key in _defaults.Keys.Union(current.Keys, StringComparer.OrdinalIgnoreCase))
        {
            bool   value  = current.TryGetValue(key, out var v) ? v : false;
            string source = sources.TryGetValue(key, out var s) ? s : "default";
            result.Add((key, value, source));
        }

        return result.OrderBy(r => r.Flag).ToList();
    }
}
