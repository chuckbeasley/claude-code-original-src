namespace ClaudeCode.Core.State;

// ---------------------------------------------------------------------------
// Discriminated union — agent input routing target
// ---------------------------------------------------------------------------

/// <summary>
/// Determines which agent slot receives user input in the current session.
/// </summary>
public abstract record AgentInputTarget
{
    /// <summary>Input goes to the main engine (leader thread).</summary>
    public sealed record Leader : AgentInputTarget;

    /// <summary>Input goes to the currently viewed sub-agent transcript.</summary>
    /// <param name="TaskId">The task identifier of the viewed teammate agent.</param>
    public sealed record ViewedTeammate(string TaskId) : AgentInputTarget;

    /// <summary>Input goes to a named local agent.</summary>
    /// <param name="TaskId">The task identifier of the named agent.</param>
    /// <param name="AgentName">The display name of the agent.</param>
    public sealed record NamedAgent(string TaskId, string AgentName) : AgentInputTarget;
}

// ---------------------------------------------------------------------------
// Selectors
// ---------------------------------------------------------------------------

/// <summary>
/// Pure selector functions for deriving routing state from <see cref="AppState"/>.
/// Corresponds to <c>src/state/selectors.ts</c> in the TypeScript source.
/// </summary>
/// <remarks>
/// These selectors are infrastructure stubs. <see cref="AppState"/> does not yet carry
/// <c>ViewingAgentTaskId</c> or a teammate-task dictionary — those properties belong to
/// the multi-agent coordinator subsystem, which is partially implemented. Both methods
/// therefore return their safe defaults (<see langword="null"/> and
/// <see cref="AgentInputTarget.Leader"/> respectively) and can be expanded in place once
/// the full task-UI is wired up without changing any call site.
/// </remarks>
public static class AppStateSelectors
{
    /// <summary>
    /// Returns the viewing-agent task ID from <paramref name="state"/>, if the state
    /// carries a <c>ViewingAgentTaskId</c> property and the referenced task exists in
    /// the task dictionary as a teammate-type task.
    /// </summary>
    /// <param name="state">The current application state snapshot.</param>
    /// <returns>
    /// The non-empty task ID string when a teammate is actively viewed; otherwise
    /// <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// Stub implementation: returns <see langword="null"/> because <see cref="AppState"/>
    /// does not yet expose <c>ViewingAgentTaskId</c>. Expand this selector once that
    /// property is added.
    /// </remarks>
    public static string? GetViewedTeammateTaskId(AppState state) => null;

    /// <summary>
    /// Returns which agent slot should receive user input based on the current
    /// <paramref name="state"/>: <see cref="AgentInputTarget.Leader"/>,
    /// <see cref="AgentInputTarget.ViewedTeammate"/>, or
    /// <see cref="AgentInputTarget.NamedAgent"/>.
    /// </summary>
    /// <param name="state">The current application state snapshot.</param>
    /// <returns>
    /// An <see cref="AgentInputTarget"/> describing the target for the next user turn.
    /// </returns>
    /// <remarks>
    /// Stub implementation: always returns <see cref="AgentInputTarget.Leader"/> because
    /// <see cref="AppState"/> does not yet carry teammate-agent routing state. Expand
    /// this selector — using <see cref="GetViewedTeammateTaskId"/> — once the full task
    /// UI and coordinator subsystem are wired up.
    /// </remarks>
    public static AgentInputTarget GetActiveAgentForInput(AppState state) =>
        new AgentInputTarget.Leader();
}
