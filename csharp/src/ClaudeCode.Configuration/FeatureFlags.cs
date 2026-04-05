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

    // Effective flag table after merging all sources.
    private static readonly Dictionary<string, bool> _flags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// (Re-)initialises the flag table from <paramref name="config"/> and environment variables.
    /// Safe to call multiple times; each call rebuilds the table from scratch.
    /// </summary>
    public static void Load(GlobalConfig? config)
    {
        _flags.Clear();

        // Start with hardcoded defaults.
        foreach (var (k, v) in _defaults)
            _flags[k] = v;

        // Layer in settings.json overrides.
        if (config?.Features is { } settingsFlags)
            foreach (var (k, v) in settingsFlags)
                _flags[k] = v;

        // Env vars take highest precedence.
        foreach (var key in _flags.Keys.ToList())
        {
            var envName = $"CLAUDE_FEATURE_{key.ToUpperInvariant().Replace('-', '_')}";
            var raw = Environment.GetEnvironmentVariable(envName);
            if (raw is null) continue;

            // Empty string or "0" or "false" (case-insensitive) → false; anything else → true.
            _flags[key] = !string.IsNullOrEmpty(raw)
                          && !raw.Equals("0", StringComparison.Ordinal)
                          && !raw.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the named flag is enabled.
    /// Unknown flags return <see langword="false"/>.
    /// </summary>
    public static bool IsEnabled(string flag)
        => _flags.TryGetValue(flag, out var v) && v;

    /// <summary>
    /// Returns a snapshot of all flag names and their effective values,
    /// along with the source that determined each value.
    /// Used by <c>/config features</c>.
    /// </summary>
    public static IReadOnlyList<(string Flag, bool Value, string Source)> GetAll(GlobalConfig? config)
    {
        var result = new List<(string Flag, bool Value, string Source)>();

        foreach (var key in _defaults.Keys.Union(_flags.Keys, StringComparer.OrdinalIgnoreCase).Distinct())
        {
            var envName = $"CLAUDE_FEATURE_{key.ToUpperInvariant().Replace('-', '_')}";
            var envRaw = Environment.GetEnvironmentVariable(envName);
            bool value = _flags.TryGetValue(key, out var v) ? v : false;

            string source;
            if (envRaw is not null)
                source = $"env ({envName})";
            else if (config?.Features?.ContainsKey(key) == true)
                source = "settings.json";
            else
                source = "default";

            result.Add((key, value, source));
        }

        return result.OrderBy(r => r.Flag).ToList();
    }
}
