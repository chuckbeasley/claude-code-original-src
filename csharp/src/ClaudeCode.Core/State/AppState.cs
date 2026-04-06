namespace ClaudeCode.Core.State;

using ClaudeCode.Core.Permissions;

/// <summary>
/// Immutable snapshot of all runtime application state for a single Claude Code session.
/// Updated exclusively through <see cref="AppStateStore.Update"/> to guarantee
/// consistent, observer-visible transitions.
/// </summary>
public record AppState
{
    // -------------------------------------------------------------------------
    // Settings
    // -------------------------------------------------------------------------

    /// <summary>Anthropic API key for the current session, or <see langword="null"/> when not yet configured.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Model identifier used for the main conversation loop.</summary>
    public string MainLoopModel { get; init; } = "claude-sonnet-4-6";

    /// <summary>Whether verbose diagnostic output is enabled.</summary>
    public bool Verbose { get; init; }

    // -------------------------------------------------------------------------
    // Permission
    // -------------------------------------------------------------------------

    /// <summary>Accumulated per-session tool permission decisions.</summary>
    public ToolPermissionContext ToolPermissions { get; init; } = new();

    // -------------------------------------------------------------------------
    // UI
    // -------------------------------------------------------------------------

    /// <summary>Text to display in the status line, or <see langword="null"/> when no status is active.</summary>
    public string? StatusLineText { get; init; }

    /// <summary>When <see langword="true"/>, the UI renders in brief (non-verbose) output mode.</summary>
    public bool IsBriefOnly { get; init; }

    // -------------------------------------------------------------------------
    // Session
    // -------------------------------------------------------------------------

    /// <summary>Stable identifier for the current conversation session, or <see langword="null"/> before the session is initialised.</summary>
    public string? SessionId { get; init; }

    /// <summary>Working directory for the session, or <see langword="null"/> before the session is initialised.</summary>
    public string? Cwd { get; init; }
}
