namespace ClaudeCode.Configuration.Settings;

using System.Text.Json;
using System.Text.Json.Serialization;

public record GlobalConfig
{
    [JsonPropertyName("numStartups")]
    public int NumStartups { get; init; }

    [JsonPropertyName("theme")]
    public string Theme { get; init; } = "dark";

    [JsonPropertyName("verbose")]
    public bool Verbose { get; init; }

    [JsonPropertyName("editorMode")]
    public string? EditorMode { get; init; }

    [JsonPropertyName("preferredNotifChannel")]
    public string PreferredNotifChannel { get; init; } = "auto";

    [JsonPropertyName("primaryApiKey")]
    public string? PrimaryApiKey { get; init; }

    [JsonPropertyName("autoUpdates")]
    public bool? AutoUpdates { get; init; }

    [JsonPropertyName("autoConnectIde")]
    public bool? AutoConnectIde { get; init; }

    [JsonPropertyName("tipsHistory")]
    public Dictionary<string, int>? TipsHistory { get; init; }

    [JsonPropertyName("projects")]
    public Dictionary<string, ProjectConfig>? Projects { get; init; }

    [JsonPropertyName("features")]
    public Dictionary<string, bool>? Features { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public record ProjectConfig
{
    [JsonPropertyName("allowedTools")]
    public List<string>? AllowedTools { get; init; }

    [JsonPropertyName("mcpContextUris")]
    public List<string>? McpContextUris { get; init; }

    [JsonPropertyName("hasTrustDialogAccepted")]
    public bool? HasTrustDialogAccepted { get; init; }

    [JsonPropertyName("hasCompletedProjectOnboarding")]
    public bool? HasCompletedProjectOnboarding { get; init; }

    [JsonPropertyName("lastCost")]
    public double? LastCost { get; init; }

    [JsonPropertyName("lastSessionId")]
    public string? LastSessionId { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
