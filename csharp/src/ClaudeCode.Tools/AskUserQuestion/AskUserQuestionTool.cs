namespace ClaudeCode.Tools.AskUserQuestion;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for <see cref="AskUserQuestionTool"/>.</summary>
public record AskUserQuestionInput
{
    /// <summary>
    /// The questions to present to the user, supplied as a raw JSON array.
    /// Accepted as <see cref="JsonElement"/> to avoid prescribing a fixed schema for
    /// the question items — the actual interactive rendering is handled at a higher level.
    /// </summary>
    [JsonPropertyName("questions")]
    public required JsonElement Questions { get; init; }
}

/// <summary>Strongly-typed output for <see cref="AskUserQuestionTool"/>.</summary>
/// <param name="Acknowledgement">
/// A placeholder acknowledgement confirming the questions were received.
/// The real interactive dialog is handled at the UI/orchestration layer.
/// </param>
public record AskUserQuestionOutput(string Acknowledgement);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Presents a set of questions to the user in an interactive dialog.
/// In the real CLI, the orchestration layer intercepts this tool call and renders
/// the dialog before returning answers. This implementation returns a placeholder
/// acknowledgement so the tool contract is satisfied in non-interactive or test contexts.
/// </summary>
public sealed class AskUserQuestionTool : Tool<AskUserQuestionInput, AskUserQuestionOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            questions = new
            {
                description = "Array of questions to present to the user. " +
                              "Each element may be a string or a structured object " +
                              "with 'question' and optional 'options' fields.",
            },
        },
        required = new[] { "questions" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "AskUserQuestion";

    /// <inheritdoc/>
    public override string? SearchHint => "ask the user one or more questions interactively";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Presents one or more questions to the user in an interactive dialog and collects answers. " +
            "The `questions` field accepts a JSON array of question items.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `AskUserQuestion` when you need explicit input from the user before proceeding. " +
            "Pass a JSON array in `questions`; each element can be a plain string or a structured " +
            "object with 'question' and optional 'options' keys. " +
            "The tool returns an acknowledgement; actual answers are injected by the CLI layer.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "AskUserQuestion";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
        => "Waiting for user response";

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    public override bool IsConcurrencySafe(JsonElement input) => false;

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override AskUserQuestionInput DeserializeInput(JsonElement json)
    {
        // The 'questions' property is kept as a raw JsonElement so the orchestration
        // layer can pass it through without loss of structure.
        if (!json.TryGetProperty("questions", out var questions))
            throw new InvalidOperationException("Required property 'questions' is missing from the input.");

        return new AskUserQuestionInput { Questions = questions };
    }

    /// <inheritdoc/>
    public override string MapResultToString(AskUserQuestionOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Acknowledgement;
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        AskUserQuestionInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Questions.ValueKind == JsonValueKind.Undefined)
            return Task.FromResult(ValidationResult.Failure("The 'questions' field is required and must be a valid JSON value."));

        if (input.Questions.ValueKind != JsonValueKind.Array)
            return Task.FromResult(ValidationResult.Failure("The 'questions' field must be a JSON array."));

        if (input.Questions.GetArrayLength() == 0)
            return Task.FromResult(ValidationResult.Failure("The 'questions' array must contain at least one question."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<AskUserQuestionOutput>> ExecuteAsync(
        AskUserQuestionInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        // Parse questions for validation and human-readable formatting.
        var sb = new StringBuilder();
        int questionCount = 0;

        foreach (var question in input.Questions.EnumerateArray())
        {
            questionCount++;
            var questionText = question.TryGetProperty("question", out var q) ? q.GetString() : "?";
            sb.AppendLine($"Q{questionCount}: {questionText}");

            if (question.TryGetProperty("options", out var options))
            {
                int optIdx = 0;
                foreach (var option in options.EnumerateArray())
                {
                    optIdx++;
                    var label = option.TryGetProperty("label", out var l) ? l.GetString() : $"Option {optIdx}";
                    var desc = option.TryGetProperty("description", out var d) ? d.GetString() : "";
                    sb.AppendLine($"  {optIdx}. {label} — {desc}");
                }
            }

            sb.AppendLine();
        }

        // In non-interactive mode, questions cannot be answered — surface them as text.
        if (context.IsNonInteractive)
        {
            return new ToolResult<AskUserQuestionOutput>
            {
                Data = new AskUserQuestionOutput(
                    $"Cannot ask questions in non-interactive mode.\n\n{sb}"),
            };
        }

        // Interactive mode — invoke the question dialog callback if available.
        if (context.QuestionDialog is { } dialog)
        {
            try
            {
                var answersJson = await dialog(input.Questions).ConfigureAwait(false);
                return new ToolResult<AskUserQuestionOutput>
                {
                    Data = new AskUserQuestionOutput(answersJson),
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ToolResult<AskUserQuestionOutput>
                {
                    Data = new AskUserQuestionOutput($"Question dialog error: {ex.Message}"),
                };
            }
        }

        // When no dialog is wired, return the questions in a structured way
        // that won't mislead the model into treating them as answers
        return new ToolResult<AskUserQuestionOutput>
        {
            Data = new AskUserQuestionOutput(
                $"[QUESTIONS PENDING - no dialog handler configured]\n{sb.ToString().TrimEnd()}\n" +
                "These questions require user interaction. Assume default/first option for each and proceed."),
        };
    }
}
