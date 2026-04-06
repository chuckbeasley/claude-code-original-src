namespace ClaudeCode.Tools.Team;

using System.Text.Json;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>
/// Strongly-typed input for <see cref="TeamDeleteTool"/>.
/// This tool takes no parameters; the empty class satisfies the generic constraint.
/// </summary>
public record TeamDeleteInput;

/// <summary>Strongly-typed output for <see cref="TeamDeleteTool"/>.</summary>
/// <param name="DeletedTeam">The name of the team that was removed, or <see langword="null"/> if no team was active.</param>
/// <param name="Confirmation">Human-readable confirmation of the deletion.</param>
public record TeamDeleteOutput(string? DeletedTeam, string Confirmation);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Removes the currently active team from <see cref="TeamState.Teams"/>
/// and clears <see cref="TeamState.CurrentTeam"/>.
/// If no team is currently active, an informational message is returned rather than an error.
/// </summary>
public sealed class TeamDeleteTool : Tool<TeamDeleteInput, TeamDeleteOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "TeamDelete";

    /// <inheritdoc/>
    public override string? SearchHint => "delete the current active team";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Removes the currently active team. " +
            "If no team is active, a status message is returned.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `TeamDelete` to disband the current active team. " +
            "The tool takes no parameters. " +
            "After deletion, no team will be active until `TeamCreate` is called again.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "TeamDelete";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        string? current;
        lock (TeamState.SyncRoot)
        {
            current = TeamState.CurrentTeam;
        }

        return current is not null
            ? $"Deleting team '{current}'"
            : "Deleting current team";
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override TeamDeleteInput DeserializeInput(JsonElement json)
        => new TeamDeleteInput();

    /// <inheritdoc/>
    public override string MapResultToString(TeamDeleteOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Confirmation;
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<TeamDeleteOutput>> ExecuteAsync(
        TeamDeleteInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        string? deletedTeam;
        string confirmation;

        lock (TeamState.SyncRoot)
        {
            deletedTeam = TeamState.CurrentTeam;

            if (deletedTeam is not null)
            {
                TeamState.Teams.Remove(deletedTeam);
                TeamState.CurrentTeam = null;
                confirmation = $"Team '{deletedTeam}' has been deleted. No team is currently active.";
            }
            else
            {
                confirmation = "No active team to delete.";
            }
        }

        var output = new TeamDeleteOutput(deletedTeam, confirmation);
        return Task.FromResult(new ToolResult<TeamDeleteOutput> { Data = output });
    }
}
