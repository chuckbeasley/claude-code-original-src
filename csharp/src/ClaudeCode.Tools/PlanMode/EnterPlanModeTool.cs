namespace ClaudeCode.Tools.PlanMode;

using System.Text.Json;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>
/// Empty input record for <see cref="EnterPlanModeTool"/>.
/// The tool takes no parameters.
/// </summary>
public record EnterPlanModeInput;

/// <summary>Strongly-typed output for the <see cref="EnterPlanModeTool"/>.</summary>
/// <param name="IsActive">Always <see langword="true"/> after a successful call.</param>
public record EnterPlanModeOutput(bool IsActive);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Activates plan mode for the current session by setting
/// <see cref="PlanModeState.IsActive"/> to <see langword="true"/>.
/// While plan mode is active, the model is expected to reason and plan without
/// executing mutating tool calls.
/// </summary>
public sealed class EnterPlanModeTool : Tool<EnterPlanModeInput, EnterPlanModeOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "EnterPlanMode";

    /// <inheritdoc/>
    public override string? SearchHint => "activate plan mode";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Activates plan mode, signalling that only read-only reasoning should occur until ExitPlanMode is called.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Call `EnterPlanMode` before drafting a plan of action. " +
            "While plan mode is active you should reason, outline steps, and confirm with the user " +
            "before executing any mutating tool calls. " +
            "Call `ExitPlanMode` when the user approves the plan and execution should begin.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "EnterPlanMode";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null) => "Entering plan mode";

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>Activating plan mode does not mutate external state.</remarks>
    public override bool IsReadOnly(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override EnterPlanModeInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<EnterPlanModeInput>(json.GetRawText(), JsonOpts)
            ?? new EnterPlanModeInput();

    /// <inheritdoc/>
    public override string MapResultToString(EnterPlanModeOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return "Plan mode activated. No mutating tool calls will be made until ExitPlanMode is called.";
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<EnterPlanModeOutput>> ExecuteAsync(
        EnterPlanModeInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        PlanModeState.IsActive = true;

        return Task.FromResult(new ToolResult<EnterPlanModeOutput>
        {
            Data = new EnterPlanModeOutput(IsActive: true),
        });
    }
}
