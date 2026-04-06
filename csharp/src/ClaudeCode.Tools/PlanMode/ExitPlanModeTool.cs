namespace ClaudeCode.Tools.PlanMode;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="ExitPlanModeTool"/>.</summary>
public record ExitPlanModeInput
{
    /// <summary>
    /// Optional set of prompt strings that are approved for use during execution.
    /// Accepted as a raw <see cref="JsonElement"/> to remain schema-agnostic.
    /// </summary>
    [JsonPropertyName("allowedPrompts")]
    public JsonElement? AllowedPrompts { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="ExitPlanModeTool"/>.</summary>
/// <param name="IsActive">Always <see langword="false"/> after a successful call.</param>
public record ExitPlanModeOutput(bool IsActive);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Deactivates plan mode by setting <see cref="PlanModeState.IsActive"/> to
/// <see langword="false"/>, allowing mutating tool calls to resume.
/// Optionally records a set of pre-approved prompts for subsequent execution.
/// </summary>
public sealed class ExitPlanModeTool : Tool<ExitPlanModeInput, ExitPlanModeOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            allowedPrompts = new
            {
                description = "Optional list of prompts approved for use during plan execution",
            },
        },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "ExitPlanMode";

    /// <inheritdoc/>
    public override string? SearchHint => "deactivate plan mode and resume execution";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Deactivates plan mode, allowing mutating tool calls to proceed.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Call `ExitPlanMode` once the user has approved your plan and you are ready to execute. " +
            "Optionally supply `allowedPrompts` to constrain which follow-up instructions are permitted. " +
            "After this call, mutating tool calls may resume.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "ExitPlanMode";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null) => "Exiting plan mode";

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override ExitPlanModeInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<ExitPlanModeInput>(json.GetRawText(), JsonOpts)
            ?? new ExitPlanModeInput();

    /// <inheritdoc/>
    public override string MapResultToString(ExitPlanModeOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return "Plan mode deactivated. Execution of mutating tool calls may now proceed.";
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<ExitPlanModeOutput>> ExecuteAsync(
        ExitPlanModeInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        PlanModeState.IsActive = false;

        return Task.FromResult(new ToolResult<ExitPlanModeOutput>
        {
            Data = new ExitPlanModeOutput(IsActive: false),
        });
    }
}
