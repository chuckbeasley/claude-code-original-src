namespace ClaudeCode.Core.Messages;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Abstract base for all content block types in the Anthropic API message format.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ToolUseBlock), "tool_use")]
[JsonDerivedType(typeof(ToolResultBlock), "tool_result")]
[JsonDerivedType(typeof(ThinkingBlock), "thinking")]
[JsonDerivedType(typeof(ImageBlock), "image")]
public abstract record ContentBlock;

/// <summary>Plain text content block.</summary>
public record TextBlock(
    [property: JsonPropertyName("text")] string Text
) : ContentBlock;

/// <summary>Tool invocation request from the assistant.</summary>
public record ToolUseBlock(
    [property: JsonPropertyName("id")]    string Id,
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("input")] JsonElement Input
) : ContentBlock;

/// <summary>
/// Result returned to the assistant after executing a tool.
/// <see cref="Content"/> is optional — when omitted the block carries only the
/// <see cref="ToolUseId"/> reference and the optional <see cref="IsError"/> flag.
/// </summary>
public record ToolResultBlock : ContentBlock
{
    /// <summary>The <c>id</c> of the <see cref="ToolUseBlock"/> this result belongs to.</summary>
    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    /// <summary>Content blocks that constitute the tool output, or <see langword="null"/> when empty.</summary>
    [JsonPropertyName("content")]
    public IReadOnlyList<ContentBlock>? Content { get; init; }

    /// <summary><see langword="true"/> when the tool execution produced an error.</summary>
    [JsonPropertyName("is_error")]
    public bool? IsError { get; init; }
}

/// <summary>Extended thinking block containing the model's internal reasoning.</summary>
public record ThinkingBlock(
    [property: JsonPropertyName("thinking")]  string Thinking,
    [property: JsonPropertyName("signature")] string Signature
) : ContentBlock;

/// <summary>Image content block.</summary>
public record ImageBlock(
    [property: JsonPropertyName("source")] ImageSource Source
) : ContentBlock;

/// <summary>Describes the origin and encoding of image data.</summary>
public record ImageSource(
    [property: JsonPropertyName("type")]       string Type,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("data")]       string Data
);
