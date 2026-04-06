namespace ClaudeCode.Tools.SyntheticOutput;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="SyntheticOutputTool"/>.</summary>
public record StructuredOutputInput
{
    /// <summary>Arbitrary JSON data to pass through as structured output.</summary>
    [JsonPropertyName("data")]
    public required JsonElement Data { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="SyntheticOutputTool"/>.</summary>
/// <param name="Data">The JSON data echoed back from the input.</param>
public record StructuredOutputOutput(JsonElement Data);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// SDK-mode structured output tool that accepts arbitrary JSON and returns it as-is.
/// Intended for use in programmatic SDK integrations where the model produces
/// structured data that the calling application should receive verbatim.
/// </summary>
public sealed class SyntheticOutputTool : Tool<StructuredOutputInput, StructuredOutputOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            data = new { description = "Arbitrary JSON data to pass through as structured output" },
        },
        required = new[] { "data" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "StructuredOutput";

    /// <inheritdoc/>
    public override string[] Aliases => ["structured_output", "synthetic_output"];

    /// <inheritdoc/>
    public override string? SearchHint => "pass through structured JSON data for SDK mode output";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Accepts arbitrary JSON data and returns it verbatim. " +
            "Used in SDK mode to produce structured output consumed by the calling application.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `StructuredOutput` in SDK mode to emit a structured JSON payload. " +
            "Provide any JSON as `data`; it is returned unchanged to the caller.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "StructuredOutput";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
        => "Producing structured output";

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        StructuredOutputInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // JsonElement is a value type; Undefined means the property was missing entirely.
        if (input.Data.ValueKind == JsonValueKind.Undefined)
            return Task.FromResult(ValidationResult.Failure("data must be provided."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override StructuredOutputInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<StructuredOutputInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize StructuredOutputInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(StructuredOutputOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Data.GetRawText();
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<StructuredOutputOutput>> ExecuteAsync(
        StructuredOutputInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        return Task.FromResult(new ToolResult<StructuredOutputOutput>
        {
            Data = new StructuredOutputOutput(input.Data),
        });
    }
}
