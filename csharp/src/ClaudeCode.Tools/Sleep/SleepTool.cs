namespace ClaudeCode.Tools.Sleep;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="SleepTool"/>.</summary>
public record SleepInput
{
    /// <summary>
    /// Duration to sleep in milliseconds.
    /// Must be a positive value and will be capped at <see cref="SleepTool.MaxDurationMs"/>.
    /// </summary>
    [JsonPropertyName("duration_ms")]
    public required int DurationMs { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="SleepTool"/>.</summary>
/// <param name="ActualDurationMs">The milliseconds actually slept (after capping).</param>
/// <param name="Message">Human-readable confirmation of the sleep.</param>
public record SleepOutput(int ActualDurationMs, string Message);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Pauses execution for the requested duration (capped at 5 minutes).
/// Useful for rate-limit back-off, polling delays, or coordinating timed sequences.
/// </summary>
public sealed class SleepTool : Tool<SleepInput, SleepOutput>
{
    /// <summary>Maximum permitted sleep duration: 5 minutes (300,000 ms).</summary>
    public const int MaxDurationMs = 300_000;

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            duration_ms = new
            {
                type = "integer",
                description = $"Sleep duration in milliseconds (capped at {MaxDurationMs} ms / 5 minutes)",
                minimum = 1,
                maximum = MaxDurationMs,
            },
        },
        required = new[] { "duration_ms" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "Sleep";

    /// <inheritdoc/>
    public override string[] Aliases => ["sleep", "wait", "delay"];

    /// <inheritdoc/>
    public override string? SearchHint => "pause execution for a specified duration";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            $"Pauses execution for the specified number of milliseconds " +
            $"(maximum {MaxDurationMs} ms / 5 minutes).");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            $"Use `Sleep` to pause execution. Provide `duration_ms` (milliseconds). " +
            $"Values exceeding {MaxDurationMs} ms are capped at 5 minutes. " +
            $"The tool is cancellation-aware and will abort early if the session is cancelled.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "Sleep";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;
        if (input.Value.TryGetProperty("duration_ms", out var dur) &&
            dur.ValueKind == JsonValueKind.Number &&
            dur.TryGetInt32(out int ms))
        {
            int capped = Math.Clamp(ms, 1, MaxDurationMs);
            return capped >= 1_000
                ? $"Sleeping for {capped / 1_000.0:G3} s"
                : $"Sleeping for {capped} ms";
        }
        return "Sleeping";
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>Sleeping does not mutate external state.</remarks>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    /// <remarks>Concurrent sleeps are independent and safe.</remarks>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        SleepInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.DurationMs <= 0)
            return Task.FromResult(ValidationResult.Failure(
                $"duration_ms must be a positive integer, got {input.DurationMs}."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override SleepInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<SleepInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize SleepInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(SleepOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Message;
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<SleepOutput>> ExecuteAsync(
        SleepInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        int durationMs = Math.Clamp(input.DurationMs, 1, MaxDurationMs);
        bool wasCapped = durationMs != input.DurationMs;

        await Task.Delay(durationMs, ct).ConfigureAwait(false);

        string message = wasCapped
            ? $"Slept for {durationMs} ms (requested {input.DurationMs} ms was capped at the {MaxDurationMs} ms maximum)."
            : $"Slept for {durationMs} ms.";

        return new ToolResult<SleepOutput> { Data = new SleepOutput(durationMs, message) };
    }
}
