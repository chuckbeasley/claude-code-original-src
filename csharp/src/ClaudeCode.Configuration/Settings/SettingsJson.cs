namespace ClaudeCode.Configuration.Settings;

using System.Text.Json;
using System.Text.Json.Serialization;

public record SettingsJson
{
    // Model & LLM
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("availableModels")]
    public List<string>? AvailableModels { get; init; }

    [JsonPropertyName("effortLevel")]
    public string? EffortLevel { get; init; }  // "low" | "medium" | "high" | "max"

    [JsonPropertyName("alwaysThinkingEnabled")]
    public bool? AlwaysThinkingEnabled { get; init; }

    [JsonPropertyName("fastMode")]
    public bool? FastMode { get; init; }

    // Permissions
    [JsonPropertyName("permissions")]
    public PermissionSettings? Permissions { get; init; }

    // Hooks
    [JsonPropertyName("hooks")]
    public Dictionary<string, List<HookMatcher>>? Hooks { get; init; }

    [JsonPropertyName("disableAllHooks")]
    public bool? DisableAllHooks { get; init; }

    // Environment
    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; init; }

    [JsonPropertyName("defaultShell")]
    public string? DefaultShell { get; init; }  // "bash" | "powershell"

    // Git
    [JsonPropertyName("includeCoAuthoredBy")]
    public bool? IncludeCoAuthoredBy { get; init; }

    [JsonPropertyName("includeGitInstructions")]
    public bool? IncludeGitInstructions { get; init; }

    // UI
    [JsonPropertyName("respectGitignore")]
    public bool? RespectGitignore { get; init; }

    [JsonPropertyName("cleanupPeriodDays")]
    public int? CleanupPeriodDays { get; init; }

    [JsonPropertyName("syntaxHighlightingDisabled")]
    public bool? SyntaxHighlightingDisabled { get; init; }

    // Auth
    [JsonPropertyName("apiKeyHelper")]
    public string? ApiKeyHelper { get; init; }

    // CLAUDE.md
    [JsonPropertyName("claudeMdExcludes")]
    public List<string>? ClaudeMdExcludes { get; init; }

    [JsonPropertyName("autoMemoryEnabled")]
    public bool? AutoMemoryEnabled { get; init; }

    // MCP
    [JsonPropertyName("enableAllProjectMcpServers")]
    public bool? EnableAllProjectMcpServers { get; init; }

    [JsonPropertyName("enabledMcpjsonServers")]
    public List<string>? EnabledMcpjsonServers { get; init; }

    [JsonPropertyName("disabledMcpjsonServers")]
    public List<string>? DisabledMcpjsonServers { get; init; }

    // Plugins
    [JsonPropertyName("enabledPlugins")]
    public Dictionary<string, JsonElement>? EnabledPlugins { get; init; }

    // Worktrees
    [JsonPropertyName("worktree")]
    public WorktreeSettings? Worktree { get; init; }

    // Remote
    [JsonPropertyName("remote")]
    public RemoteSettings? Remote { get; init; }

    // MCP server definitions
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerEntryJson>? McpServers { get; init; }

    // Catch-all for unknown fields (passthrough)
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
