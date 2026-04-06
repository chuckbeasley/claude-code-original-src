namespace ClaudeCode.Tools.PlanMode;

/// <summary>
/// Process-wide flag indicating whether plan mode is currently active.
/// Shared by <see cref="EnterPlanModeTool"/> and <see cref="ExitPlanModeTool"/>.
/// </summary>
public static class PlanModeState
{
    /// <summary>
    /// <see langword="true"/> when the session is operating in plan mode (no tool calls
    /// that mutate state are permitted without explicit approval).
    /// </summary>
    public static bool IsActive { get; set; }
}
