namespace ClaudeCode.Tools.Team;

/// <summary>
/// Represents the configuration of a single agent team.
/// </summary>
/// <param name="Name">The canonical team name.</param>
/// <param name="Description">Optional human-readable description of the team's purpose.</param>
/// <param name="AgentType">Optional agent type identifier that governs team behaviour.</param>
public record TeamInfo(string Name, string? Description, string? AgentType);

/// <summary>
/// Static shared state for the team management tools.
/// Teams are stored in-process for the duration of the session.
/// </summary>
/// <remarks>
/// Access to <see cref="Teams"/> and <see cref="CurrentTeam"/> is not inherently thread-safe
/// beyond the guarantees provided by <see cref="Dictionary{TKey,TValue}"/> reads on a
/// single-threaded orchestration loop. If multi-threaded access is required, wrap operations
/// in a lock on <see cref="SyncRoot"/>.
/// </remarks>
public static class TeamState
{
    /// <summary>Synchronization root for multi-threaded access to team state.</summary>
    public static readonly object SyncRoot = new();

    /// <summary>
    /// All registered teams, keyed case-insensitively by name.
    /// </summary>
    public static Dictionary<string, TeamInfo> Teams { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The name of the currently active team, or <see langword="null"/> when no team is active.
    /// </summary>
    public static string? CurrentTeam { get; set; }
}
