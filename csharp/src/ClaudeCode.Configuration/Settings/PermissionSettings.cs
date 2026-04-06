namespace ClaudeCode.Configuration.Settings;

using System.Text.Json.Serialization;

public record PermissionSettings
{
    [JsonPropertyName("allow")]
    public List<string>? Allow { get; init; }

    [JsonPropertyName("deny")]
    public List<string>? Deny { get; init; }

    [JsonPropertyName("ask")]
    public List<string>? Ask { get; init; }

    [JsonPropertyName("defaultMode")]
    public string? DefaultMode { get; init; }

    [JsonPropertyName("disableBypassPermissionsMode")]
    public string? DisableBypassPermissionsMode { get; init; }

    [JsonPropertyName("additionalDirectories")]
    public List<string>? AdditionalDirectories { get; init; }
}
