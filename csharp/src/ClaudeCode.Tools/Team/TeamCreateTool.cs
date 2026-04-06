namespace ClaudeCode.Tools.Team;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for <see cref="TeamCreateTool"/>.</summary>
public record TeamCreateInput
{
    /// <summary>The canonical name for the new team.</summary>
    [JsonPropertyName("team_name")]
    public required string TeamName { get; init; }

    /// <summary>Optional human-readable description of the team's purpose.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Optional agent type identifier that governs team behaviour.</summary>
    [JsonPropertyName("agent_type")]
    public string? AgentType { get; init; }
}

/// <summary>Strongly-typed output for <see cref="TeamCreateTool"/>.</summary>
/// <param name="TeamName">The name of the team that was created.</param>
/// <param name="Confirmation">Human-readable confirmation of the creation.</param>
public record TeamCreateOutput(string TeamName, string Confirmation);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Creates a new agent team and registers it in <see cref="TeamState.Teams"/>.
/// The new team is also set as the current active team via <see cref="TeamState.CurrentTeam"/>.
/// Attempting to create a team with a name that already exists returns an error via validation.
/// </summary>
public sealed class TeamCreateTool : Tool<TeamCreateInput, TeamCreateOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            team_name   = new { type = "string", description = "The canonical name for the new team." },
            description = new { type = "string", description = "Optional human-readable description of the team's purpose." },
            agent_type  = new { type = "string", description = "Optional agent type identifier." },
        },
        required = new[] { "team_name" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "TeamCreate";

    /// <inheritdoc/>
    public override string? SearchHint => "create a new agent team";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Creates a new agent team with the given name and optional description and agent type. " +
            "The new team is set as the current active team.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `TeamCreate` to register a new team. " +
            "Provide `team_name` (required), optional `description`, and optional `agent_type`. " +
            "The created team becomes the active team. " +
            "Use `TeamDelete` to remove the current team.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "TeamCreate";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null)
            return null;

        if (input.Value.TryGetProperty("team_name", out var name) &&
            name.ValueKind == JsonValueKind.String)
        {
            return $"Creating team '{name.GetString()}'";
        }

        return "Creating team";
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override TeamCreateInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<TeamCreateInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialise TeamCreateInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(TeamCreateOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Confirmation;
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        TeamCreateInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.TeamName))
            return Task.FromResult(ValidationResult.Failure("The 'team_name' field must not be empty or whitespace."));

        lock (TeamState.SyncRoot)
        {
            if (TeamState.Teams.ContainsKey(input.TeamName))
                return Task.FromResult(
                    ValidationResult.Failure($"A team named '{input.TeamName}' already exists."));
        }

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<TeamCreateOutput>> ExecuteAsync(
        TeamCreateInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        var info = new TeamInfo(input.TeamName, input.Description, input.AgentType);

        lock (TeamState.SyncRoot)
        {
            TeamState.Teams[input.TeamName] = info;
            TeamState.CurrentTeam = input.TeamName;
        }

        var confirmation = $"Team '{input.TeamName}' created and set as the active team.";
        var output = new TeamCreateOutput(input.TeamName, confirmation);

        return Task.FromResult(new ToolResult<TeamCreateOutput> { Data = output });
    }
}
