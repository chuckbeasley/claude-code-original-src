namespace ClaudeCode.Core.Tools;

using System.Text.Json;

/// <summary>
/// Non-generic contract for all tools that can be discovered and invoked by the
/// tool registry. Keeping this interface non-generic allows the registry to hold
/// heterogeneous tools without reflection gymnastics.
/// </summary>
/// <remarks>
/// Concrete tools should derive from <see cref="Tool{TInput,TOutput}"/> rather than
/// implementing this interface directly. Interface default members encode the
/// conventional behaviour; override only when a tool genuinely diverges.
/// </remarks>
public interface ITool
{
    /// <summary>
    /// Canonical machine-readable name, e.g. <c>"bash"</c> or <c>"read_file"</c>.
    /// Must be unique within a <see cref="ToolRegistry"/> and stable across sessions.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Optional alternative names the tool can be addressed by.
    /// Defaults to an empty array (no aliases).
    /// </summary>
    string[] Aliases => [];

    /// <summary>
    /// Optional hint used by semantic-search infrastructure to surface this tool.
    /// When <see langword="null"/> the tool is not boosted in search results.
    /// </summary>
    string? SearchHint => null;

    /// <summary>
    /// Returns <see langword="true"/> when the tool is available for use in the current
    /// session. Disabled tools are omitted from the API's tool list.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    bool IsEnabled() => true;

    /// <summary>
    /// Returns <see langword="true"/> when the tool does not mutate any external state
    /// for the given <paramref name="input"/>. Used by the planner to decide whether
    /// user confirmation is needed.
    /// Defaults to <see langword="false"/> (assume mutating).
    /// </summary>
    /// <param name="input">The serialised input the model intends to pass.</param>
    bool IsReadOnly(JsonElement input) => false;

    /// <summary>
    /// Returns <see langword="true"/> when multiple concurrent invocations of this tool
    /// with <paramref name="input"/> are safe (i.e. the tool is idempotent and stateless).
    /// Defaults to <see langword="false"/> (assume unsafe to parallelise).
    /// </summary>
    /// <param name="input">The serialised input the model intends to pass.</param>
    bool IsConcurrencySafe(JsonElement input) => false;

    /// <summary>
    /// Maximum number of characters the tool's result string may contain before the
    /// caller should truncate or summarise the output. Defaults to 100,000 characters.
    /// </summary>
    int MaxResultSizeChars => 100_000;

    /// <summary>
    /// Returns a Markdown description of what this tool does, suitable for inclusion
    /// in the system prompt sent to the model.
    /// </summary>
    Task<string> GetDescriptionAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the full prompt text injected into the system prompt to instruct the
    /// model on when and how to use this tool.
    /// </summary>
    Task<string> GetPromptAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the name shown in the UI for this tool, optionally customised per
    /// invocation (e.g. showing the target filename for a read-file tool).
    /// Defaults to <see cref="Name"/> when <paramref name="input"/> is <see langword="null"/>.
    /// </summary>
    /// <param name="input">Optional serialised input from the current invocation.</param>
    string UserFacingName(JsonElement? input = null);

    /// <summary>
    /// Returns a short present-tense description of what this specific invocation is
    /// doing, for display in the activity indicator (e.g. <c>"Reading config.json"</c>).
    /// Returns <see langword="null"/> when no activity description is available.
    /// </summary>
    /// <param name="input">Optional serialised input from the current invocation.</param>
    string? GetActivityDescription(JsonElement? input = null) => null;

    /// <summary>
    /// Returns the JSON Schema object that describes the tool's input parameters.
    /// This is serialised verbatim into the API request's <c>tools</c> array.
    /// </summary>
    JsonElement GetInputSchema();

    /// <summary>
    /// Deserialises <paramref name="input"/>, executes the tool, and returns the
    /// result as a plain string suitable for a <c>tool_result</c> API block.
    /// This non-generic execution path allows the engine to invoke any registered
    /// tool without reflection or knowledge of the concrete type parameters.
    /// </summary>
    /// <param name="input">The raw JSON element from the model's <c>tool_use</c> block <c>input</c> field.</param>
    /// <param name="context">Ambient context for the current tool invocation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The string result to send back to the model.</returns>
    Task<string> ExecuteRawAsync(JsonElement input, ToolUseContext context, CancellationToken ct = default);
}
