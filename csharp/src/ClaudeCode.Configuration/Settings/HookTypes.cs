namespace ClaudeCode.Configuration.Settings;

using System.Text.Json.Serialization;

public record HookMatcher
{
    [JsonPropertyName("matcher")]
    public string? Matcher { get; init; }

    [JsonPropertyName("hooks")]
    public List<HookCommand>? Commands { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BashHookCommand), "command")]
[JsonDerivedType(typeof(PromptHookCommand), "prompt")]
[JsonDerivedType(typeof(HttpHookCommand), "http")]
public abstract record HookCommand;

public record BashHookCommand : HookCommand
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("shell")]
    public string? Shell { get; init; }

    [JsonPropertyName("timeout")]
    public int? Timeout { get; init; }

    [JsonPropertyName("statusMessage")]
    public string? StatusMessage { get; init; }

    [JsonPropertyName("once")]
    public bool? Once { get; init; }
}

public record PromptHookCommand : HookCommand
{
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("timeout")]
    public int? Timeout { get; init; }
}

public record HttpHookCommand : HookCommand
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    [JsonPropertyName("timeout")]
    public int? Timeout { get; init; }
}
