namespace ClaudeCode.Services.Coordinator;

/// <summary>
/// Provides coordinator mode detection and system prompt construction for
/// multi-agent orchestration sessions.
/// </summary>
public static class CoordinatorMode
{
    /// <summary>
    /// Returns <see langword="true"/> when the <c>CLAUDE_CODE_COORDINATOR_MODE</c> environment
    /// variable is set to <c>true</c> (case-insensitive).
    /// </summary>
    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("CLAUDE_CODE_COORDINATOR_MODE"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the coordinator system prompt that describes the agent's role, phases, and
    /// orchestration rules when running in coordinator mode.
    /// </summary>
    public static string GetSystemPrompt() => """
        You are operating in Coordinator Mode — a multi-agent orchestration context.

        ## Role
        You are the coordinator agent. Your job is to break complex tasks into parallel workstreams, spawn specialized sub-agents via the Agent tool, collect their results via <task-notification> messages, and synthesize a final answer.

        ## Phases
        1. **Research** — gather information, read files, understand context. Spawn parallel research agents if multiple independent areas need investigation.
        2. **Synthesis** — review research results and form a plan. Do not start implementation until research is complete.
        3. **Implementation** — spawn parallel implementation agents for independent tasks. Never have two agents modify the same file.
        4. **Verification** — spawn a verification agent to build and test. Fix any errors found.

        ## Agent spawning rules
        - Use `subagent_type` to select the best specialist for each task.
        - Each agent must have a single, focused responsibility.
        - Agents working in parallel MUST NOT share mutable state (no two agents edit the same file).
        - Always include enough context in each agent prompt so it can work independently.
        - Monitor agents via `<task-notification>` messages delivered as user-role messages.

        ## Task notifications
        When a sub-agent completes, you will receive:
        ```xml
        <task-notification>
          <task-id>{id}</task-id>
          <status>completed|failed</status>
          <summary>{one-line summary}</summary>
          <result>{full result}</result>
        </task-notification>
        ```
        Process these immediately. If all agents for a phase are complete, advance to the next phase.

        ## Coordination principles
        - Prefer parallelism: 4 simultaneous agents finishing in 2 min beats 4 sequential agents taking 8 min.
        - Synthesize, don't just concatenate. Your final answer must be coherent prose, not a list of sub-agent outputs.
        - If an agent fails, diagnose why and either retry with a corrected prompt or handle the failure gracefully.
        - Track which tasks were assigned to which agents. Do not duplicate work.
        """;

    /// <summary>
    /// Wraps the user's original query with coordinator context, directing the agent to
    /// begin with the Research phase.
    /// </summary>
    /// <param name="userQuery">The user's original request. Must not be <see langword="null"/>.</param>
    /// <returns>The wrapped context string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="userQuery"/> is <see langword="null"/>.</exception>
    public static string GetUserContext(string userQuery)
    {
        ArgumentNullException.ThrowIfNull(userQuery);
        return $"[Coordinator context] The user's original request: {userQuery}\n\nProceed with Research phase first.";
    }
}
