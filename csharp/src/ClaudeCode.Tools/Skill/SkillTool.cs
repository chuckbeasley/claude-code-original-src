namespace ClaudeCode.Tools.Skill;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;
using ClaudeCode.Services.Skills;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for <see cref="SkillTool"/>.</summary>
public record SkillInput
{
    /// <summary>The name of the skill to load, matching the filename (without extension) in <c>.claude/skills/</c>.</summary>
    [JsonPropertyName("skill")]
    public required string Skill { get; init; }

    /// <summary>Optional arguments to append after the skill's prompt content.</summary>
    [JsonPropertyName("args")]
    public string? Args { get; init; }
}

/// <summary>Strongly-typed output for <see cref="SkillTool"/>.</summary>
/// <param name="SkillName">The resolved canonical name of the skill.</param>
/// <param name="Prompt">The skill's full prompt content.</param>
/// <param name="FilePath">Absolute path to the skill's Markdown file.</param>
public record SkillOutput(string SkillName, string Prompt, string FilePath);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Loads a skill from a <c>.claude/skills/{name}.md</c> file and returns its prompt content.
/// Project-level skills take precedence over global skills when names collide.
/// The <see cref="SkillLoader"/> handles discovery from both project and global skill directories.
/// </summary>
public sealed class SkillTool : Tool<SkillInput, SkillOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            skill = new { type = "string", description = "The name of the skill to load (filename without .md extension)." },
            args  = new { type = "string", description = "Optional arguments appended to the skill's prompt." },
        },
        required = new[] { "skill" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "Skill";

    /// <inheritdoc/>
    public override string? SearchHint => "load and execute a skill from .claude/skills/";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Loads a skill by name from the `.claude/skills/` directory (project or global) " +
            "and returns the skill's prompt content. Optional `args` are appended after the prompt.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `Skill` to load a skill Markdown file and retrieve its prompt. " +
            "Provide `skill` (the filename without `.md`). " +
            "Project skills in `{cwd}/.claude/skills/` override global skills in `~/.claude/skills/`. " +
            "The returned prompt text should be injected as instructions for the current task.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "Skill";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null)
            return null;

        if (input.Value.TryGetProperty("skill", out var skill) &&
            skill.ValueKind == JsonValueKind.String)
        {
            return $"Loading skill '{skill.GetString()}'";
        }

        return "Loading skill";
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>Loading a skill file is a pure read operation.</remarks>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override SkillInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<SkillInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialise SkillInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(SkillOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        return string.IsNullOrEmpty(result.Prompt)
            ? $"Skill '{result.SkillName}' has no prompt content."
            : result.Prompt;
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        SkillInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Skill))
            return Task.FromResult(ValidationResult.Failure("The 'skill' field must not be empty or whitespace."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<SkillOutput>> ExecuteAsync(
        SkillInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        var skills = SkillLoader.LoadSkills(context.Cwd);
        var skill = skills.FirstOrDefault(
            s => s.Name.Equals(input.Skill, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
            throw new InvalidOperationException(
                $"Skill '{input.Skill}' not found. " +
                $"Searched '{context.Cwd}/.claude/skills/' and the global skills directory.");

        var prompt = string.IsNullOrWhiteSpace(input.Args)
            ? skill.Prompt
            : $"{skill.Prompt}\n\n{input.Args.Trim()}";

        var output = new SkillOutput(skill.Name, prompt, skill.FilePath);
        return Task.FromResult(new ToolResult<SkillOutput> { Data = output });
    }
}
