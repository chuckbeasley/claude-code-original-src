namespace ClaudeCode.Configuration;

using System.Text.Json;
using ClaudeCode.Configuration.Settings;

/// <summary>
/// Loads and merges settings from all sources in priority order.
/// This class is stateless — each call to a Load method reads from disk.
/// </summary>
public sealed class SettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads and merges settings from all sources in priority order.
    /// Sources are applied lowest-to-highest: user &lt; project &lt; local &lt; flags &lt; policy.
    /// Policy settings always win; they are never overridable by users.
    /// </summary>
    /// <param name="cwd">The current working directory used to locate project-scoped files.</param>
    /// <param name="flagSettings">Optional settings injected from CLI flags or SDK callers.</param>
    /// <returns>A merged <see cref="SettingsJson"/> with all sources applied.</returns>
    public SettingsJson LoadMergedSettings(string cwd, SettingsJson? flagSettings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);

        var merged = new SettingsJson();

        // 1. User settings — lowest priority
        var userSettings = LoadSettingsFile(ConfigPaths.UserSettingsPath);
        if (userSettings is not null)
            merged = MergeSettings(merged, userSettings);

        // 2. Project settings — checked into VCS
        var projectSettings = LoadSettingsFile(ConfigPaths.ProjectSettingsPath(cwd));
        if (projectSettings is not null)
            merged = MergeSettings(merged, projectSettings);

        // 3. Local settings — machine-local overrides, not checked in
        var localSettings = LoadSettingsFile(ConfigPaths.LocalSettingsPath(cwd));
        if (localSettings is not null)
            merged = MergeSettings(merged, localSettings);

        // 3.5 Environment variable overrides
        var envSettings = LoadEnvironmentSettings();
        if (envSettings is not null)
            merged = MergeSettings(merged, envSettings);

        // 4. Flag settings — from CLI args or SDK callers
        if (flagSettings is not null)
            merged = MergeSettings(merged, flagSettings);

        // 5. Policy settings — highest priority, cannot be overridden by users
        var policySettings = LoadPolicySettings();
        if (policySettings is not null)
            merged = MergeSettings(merged, policySettings);

        return merged;
    }

    /// <summary>
    /// Loads the global config (session state) from <see cref="ConfigPaths.GlobalConfigPath"/>.
    /// Returns a default instance if the file does not exist or cannot be parsed.
    /// </summary>
    public GlobalConfig LoadGlobalConfig()
    {
        var path = ConfigPaths.GlobalConfigPath;
        if (!File.Exists(path))
            return new GlobalConfig();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GlobalConfig>(json, JsonOptions) ?? new GlobalConfig();
        }
        catch (JsonException)
        {
            // Corrupted config — return safe default so the app can still start.
            return new GlobalConfig();
        }
    }

    /// <summary>
    /// Persists the global config to <see cref="ConfigPaths.GlobalConfigPath"/>,
    /// creating the containing directory if it does not exist.
    /// </summary>
    /// <param name="config">The config to serialize. Must not be null.</param>
    public void SaveGlobalConfig(GlobalConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var path = ConfigPaths.GlobalConfigPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var writeOptions = new JsonSerializerOptions(JsonOptions) { WriteIndented = true };
        var json = JsonSerializer.Serialize(config, writeOptions);
        File.WriteAllText(path, json);
    }

    // -------------------------------------------------------------------------
    // Internal — exposed as internal for unit testing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Merges two <see cref="SettingsJson"/> records. The <paramref name="overlay"/>
    /// takes precedence over <paramref name="baseSettings"/> for every field.
    /// Lists are concatenated (base first). Dictionaries are merged (overlay wins on key collision).
    /// Scalar nullables keep the base value when the overlay value is null.
    /// </summary>
    internal static SettingsJson MergeSettings(SettingsJson baseSettings, SettingsJson overlay)
    {
        return baseSettings with
        {
            Model = overlay.Model ?? baseSettings.Model,
            AvailableModels = MergeLists(baseSettings.AvailableModels, overlay.AvailableModels),
            EffortLevel = overlay.EffortLevel ?? baseSettings.EffortLevel,
            AlwaysThinkingEnabled = overlay.AlwaysThinkingEnabled ?? baseSettings.AlwaysThinkingEnabled,
            FastMode = overlay.FastMode ?? baseSettings.FastMode,
            Permissions = MergePermissions(baseSettings.Permissions, overlay.Permissions),
            Hooks = MergeDictOfLists(baseSettings.Hooks, overlay.Hooks),
            DisableAllHooks = overlay.DisableAllHooks ?? baseSettings.DisableAllHooks,
            Env = MergeDicts(baseSettings.Env, overlay.Env),
            DefaultShell = overlay.DefaultShell ?? baseSettings.DefaultShell,
            IncludeCoAuthoredBy = overlay.IncludeCoAuthoredBy ?? baseSettings.IncludeCoAuthoredBy,
            IncludeGitInstructions = overlay.IncludeGitInstructions ?? baseSettings.IncludeGitInstructions,
            RespectGitignore = overlay.RespectGitignore ?? baseSettings.RespectGitignore,
            CleanupPeriodDays = overlay.CleanupPeriodDays ?? baseSettings.CleanupPeriodDays,
            SyntaxHighlightingDisabled = overlay.SyntaxHighlightingDisabled ?? baseSettings.SyntaxHighlightingDisabled,
            ApiKeyHelper = overlay.ApiKeyHelper ?? baseSettings.ApiKeyHelper,
            ClaudeMdExcludes = MergeLists(baseSettings.ClaudeMdExcludes, overlay.ClaudeMdExcludes),
            AutoMemoryEnabled = overlay.AutoMemoryEnabled ?? baseSettings.AutoMemoryEnabled,
            EnableAllProjectMcpServers = overlay.EnableAllProjectMcpServers ?? baseSettings.EnableAllProjectMcpServers,
            EnabledMcpjsonServers = MergeLists(baseSettings.EnabledMcpjsonServers, overlay.EnabledMcpjsonServers),
            DisabledMcpjsonServers = MergeLists(baseSettings.DisabledMcpjsonServers, overlay.DisabledMcpjsonServers),
            EnabledPlugins = MergeDicts(baseSettings.EnabledPlugins, overlay.EnabledPlugins),
            Worktree = overlay.Worktree ?? baseSettings.Worktree,
            Remote = overlay.Remote ?? baseSettings.Remote,
            McpServers = MergeDicts(baseSettings.McpServers, overlay.McpServers),
            // ExtensionData: unknown fields from the overlay replace the base.
            // We intentionally do not attempt to deep-merge opaque JsonElement trees.
            ExtensionData = overlay.ExtensionData ?? baseSettings.ExtensionData,
        };
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private SettingsJson? LoadSettingsFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);

            // Strip UTF-8 BOM if present (common when files are saved by Windows editors)
            if (json.Length > 0 && json[0] == '\uFEFF')
                json = json[1..];

            return JsonSerializer.Deserialize<SettingsJson>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Malformed file — skip it silently so a corrupt local file
            // cannot prevent the application from starting.
            return null;
        }
    }

    private SettingsJson? LoadPolicySettings()
    {
        // Primary managed-settings file takes absolute precedence.
        var managedPath = ConfigPaths.ManagedSettingsPath;
        if (File.Exists(managedPath))
            return LoadSettingsFile(managedPath);

        // Drop-in directory: managed-settings.d/*.json, applied in lexicographic order.
        var dropInDir = Path.ChangeExtension(managedPath, null) + ".d";
        if (Directory.Exists(dropInDir))
        {
            var merged = new SettingsJson();
            var files = Directory.GetFiles(dropInDir, "*.json").OrderBy(f => f, StringComparer.Ordinal);
            foreach (var file in files)
            {
                var settings = LoadSettingsFile(file);
                if (settings is not null)
                    merged = MergeSettings(merged, settings);
            }
            return merged;
        }

        return null;
    }

    private static List<string>? MergeLists(List<string>? baseList, List<string>? overlay)
    {
        if (overlay is null) return baseList;
        if (baseList is null) return overlay;
        return [.. baseList, .. overlay];
    }

    private static Dictionary<string, TValue>? MergeDicts<TValue>(
        Dictionary<string, TValue>? baseDict,
        Dictionary<string, TValue>? overlay)
    {
        if (overlay is null) return baseDict;
        if (baseDict is null) return overlay;

        var result = new Dictionary<string, TValue>(baseDict, StringComparer.Ordinal);
        foreach (var (key, value) in overlay)
            result[key] = value;
        return result;
    }

    private static Dictionary<string, List<TItem>>? MergeDictOfLists<TItem>(
        Dictionary<string, List<TItem>>? baseDict,
        Dictionary<string, List<TItem>>? overlay)
    {
        if (overlay is null) return baseDict;
        if (baseDict is null) return overlay;

        var result = new Dictionary<string, List<TItem>>(baseDict, StringComparer.Ordinal);
        foreach (var (key, value) in overlay)
        {
            if (result.TryGetValue(key, out var existing))
                result[key] = [.. existing, .. value];
            else
                result[key] = value;
        }
        return result;
    }

    private static PermissionSettings? MergePermissions(
        PermissionSettings? basePerms,
        PermissionSettings? overlay)
    {
        if (overlay is null) return basePerms;
        if (basePerms is null) return overlay;

        return basePerms with
        {
            Allow = MergeLists(basePerms.Allow, overlay.Allow),
            Deny = MergeLists(basePerms.Deny, overlay.Deny),
            Ask = MergeLists(basePerms.Ask, overlay.Ask),
            DefaultMode = overlay.DefaultMode ?? basePerms.DefaultMode,
            DisableBypassPermissionsMode = overlay.DisableBypassPermissionsMode ?? basePerms.DisableBypassPermissionsMode,
            AdditionalDirectories = MergeLists(basePerms.AdditionalDirectories, overlay.AdditionalDirectories),
        };
    }

    /// <summary>
    /// Reads well-known CLAUDE_CODE_* and ANTHROPIC_* environment variables and
    /// maps them to settings fields. Environment settings sit between local settings
    /// (priority 3) and CLI flag settings (priority 4).
    /// </summary>
    private static SettingsJson? LoadEnvironmentSettings()
    {
        var hasAny = false;
        var s = new SettingsJson();

        // Model selection
        var model = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL")
                 ?? Environment.GetEnvironmentVariable("CLAUDE_MODEL");
        if (!string.IsNullOrWhiteSpace(model))
        {
            s = s with { Model = model };
            hasAny = true;
        }

        // Default permission mode
        var permMode = Environment.GetEnvironmentVariable("CLAUDE_CODE_DEFAULT_PERMISSION_MODE");
        if (!string.IsNullOrWhiteSpace(permMode))
        {
            var perms = s.Permissions ?? new PermissionSettings();
            s = s with { Permissions = perms with { DefaultMode = permMode } };
            hasAny = true;
        }

        // Disable all hooks
        var disableHooks = Environment.GetEnvironmentVariable("CLAUDE_CODE_DISABLE_HOOKS");
        if (!string.IsNullOrWhiteSpace(disableHooks) &&
            (disableHooks == "1" || disableHooks.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            s = s with { DisableAllHooks = true };
            hasAny = true;
        }

        // Default shell
        var shell = Environment.GetEnvironmentVariable("CLAUDE_CODE_DEFAULT_SHELL");
        if (!string.IsNullOrWhiteSpace(shell))
        {
            s = s with { DefaultShell = shell };
            hasAny = true;
        }

        // Respect gitignore
        var respectGitignore = Environment.GetEnvironmentVariable("CLAUDE_CODE_RESPECT_GITIGNORE");
        if (!string.IsNullOrWhiteSpace(respectGitignore))
        {
            s = s with { RespectGitignore = respectGitignore != "0" && !respectGitignore.Equals("false", StringComparison.OrdinalIgnoreCase) };
            hasAny = true;
        }

        // Thinking mode
        var thinkingEnabled = Environment.GetEnvironmentVariable("CLAUDE_CODE_ALWAYS_THINKING");
        if (!string.IsNullOrWhiteSpace(thinkingEnabled) &&
            (thinkingEnabled == "1" || thinkingEnabled.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            s = s with { AlwaysThinkingEnabled = true };
            hasAny = true;
        }

        return hasAny ? s : null;
    }
}
