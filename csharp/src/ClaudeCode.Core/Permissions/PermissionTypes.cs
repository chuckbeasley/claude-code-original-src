namespace ClaudeCode.Core.Permissions;

/// <summary>
/// Governs how the permission system responds to tool invocations session-wide.
/// </summary>
public enum PermissionMode
{
    /// <summary>Standard interactive mode: prompt the user when rules do not match.</summary>
    Default,

    /// <summary>All permission checks are bypassed; tools execute unconditionally.</summary>
    BypassPermissions,

    /// <summary>Never prompt; deny anything not explicitly allowed.</summary>
    DontAsk,

    /// <summary>Planning mode: read-only tools are allowed; write tools are denied.</summary>
    Plan,

    /// <summary>File edits are pre-approved; other write operations still prompt.</summary>
    AcceptEdits,

    /// <summary>Autonomous mode: all operations auto-approved without prompting.</summary>
    Auto
}

/// <summary>The concrete action a permission rule prescribes.</summary>
public enum PermissionBehavior
{
    /// <summary>Permit the operation without prompting.</summary>
    Allow,

    /// <summary>Reject the operation unconditionally.</summary>
    Deny,

    /// <summary>Prompt the user interactively before proceeding.</summary>
    Ask
}

/// <summary>Identifies where a <see cref="PermissionRule"/> originated.</summary>
public enum PermissionRuleSource
{
    /// <summary>Defined in the policy/enterprise settings layer.</summary>
    PolicySettings,

    /// <summary>Defined in the local (machine-scoped) settings file.</summary>
    LocalSettings,

    /// <summary>Defined in the per-project settings file.</summary>
    ProjectSettings,

    /// <summary>Defined in the user's personal settings file.</summary>
    UserSettings,

    /// <summary>Injected via a configuration flag (e.g. <c>--allow-tool</c>).</summary>
    FlagSettings,

    /// <summary>Supplied as a CLI argument for the current invocation.</summary>
    CliArg,

    /// <summary>Issued by a slash command during the interactive session.</summary>
    Command,

    /// <summary>Granted interactively during the current session.</summary>
    Session
}

// ---------------------------------------------------------------------------
// Permission decision hierarchy
// ---------------------------------------------------------------------------

/// <summary>
/// Discriminated union representing the outcome of a permission check.
/// Callers switch on the concrete derived type to determine next action.
/// </summary>
public abstract record PermissionDecision;

/// <summary>
/// The requested operation is permitted to proceed.
/// <para>
/// When <see cref="UpdatedInput"/> is non-null the tool should substitute it
/// for the original input (e.g. after a path rewrite).
/// </para>
/// </summary>
/// <param name="UpdatedInput">Optional replacement input object, or <see langword="null"/> to use the original.</param>
/// <param name="Reason">Human-readable explanation recorded in the audit log.</param>
public record PermissionAllowed(object? UpdatedInput = null, string? Reason = null) : PermissionDecision;

/// <summary>
/// The requested operation is denied.
/// </summary>
/// <param name="Message">User-facing explanation of why the operation was denied.</param>
/// <param name="Reason">Optional internal reason for audit or telemetry.</param>
public record PermissionDenied(string Message, string? Reason = null) : PermissionDecision;

/// <summary>
/// The permission system cannot resolve the request without user input.
/// The host should present <see cref="Message"/> and any <see cref="Suggestions"/> to the user.
/// </summary>
/// <param name="Message">The prompt shown to the user.</param>
/// <param name="Suggestions">Optional list of pre-formed responses the user can select.</param>
public record PermissionAsk(string Message, IReadOnlyList<PermissionSuggestion>? Suggestions = null) : PermissionDecision;

// ---------------------------------------------------------------------------
// Supporting types
// ---------------------------------------------------------------------------

/// <summary>A selectable option presented to the user during an interactive permission prompt.</summary>
/// <param name="Label">Short label rendered as a keyboard shortcut or button caption.</param>
/// <param name="Description">Longer text explaining what accepting this suggestion will do.</param>
public record PermissionSuggestion(string Label, string Description);

/// <summary>
/// A single rule that maps a tool name to a <see cref="PermissionBehavior"/>,
/// optionally scoped by a pattern in <see cref="RuleContent"/>.
/// </summary>
/// <param name="Source">Where this rule was loaded from.</param>
/// <param name="Behavior">The action the rule prescribes.</param>
/// <param name="ToolName">The tool name this rule applies to.</param>
/// <param name="RuleContent">Optional glob or regex pattern further scoping the rule, e.g. a path filter.</param>
public record PermissionRule(
    PermissionRuleSource Source,
    PermissionBehavior Behavior,
    string ToolName,
    string? RuleContent = null);

/// <summary>
/// The full permission configuration applicable to a single tool invocation,
/// derived by merging all rule sources in precedence order.
/// </summary>
public record ToolPermissionContext
{
    /// <summary>Session-wide permission mode that applies before individual rules are evaluated.</summary>
    public PermissionMode Mode { get; init; } = PermissionMode.Default;

    /// <summary>Rules that unconditionally allow the tool to run.</summary>
    public IReadOnlyList<PermissionRule> AllowRules { get; init; } = [];

    /// <summary>Rules that unconditionally deny the tool from running.</summary>
    public IReadOnlyList<PermissionRule> DenyRules { get; init; } = [];

    /// <summary>Rules that require interactive user confirmation before the tool runs.</summary>
    public IReadOnlyList<PermissionRule> AskRules { get; init; } = [];
}
