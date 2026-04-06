namespace ClaudeCode.Configuration.Settings;

using System.Text.Json.Serialization;

public record WorktreeSettings
{
    [JsonPropertyName("symlinkDirectories")]
    public List<string>? SymlinkDirectories { get; init; }

    [JsonPropertyName("sparsePaths")]
    public List<string>? SparsePaths { get; init; }
}

public record RemoteSettings
{
    [JsonPropertyName("defaultEnvironmentId")]
    public string? DefaultEnvironmentId { get; init; }
}
