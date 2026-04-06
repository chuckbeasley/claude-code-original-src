namespace ClaudeCode.Cli;

using ClaudeCode.Core.Permissions;
using ClaudeCode.Permissions;
using Spectre.Console;

/// <summary>
/// Presents an interactive permission dialog to the user when a tool requires approval.
/// </summary>
public interface IPermissionDialog
{
    /// <summary>
    /// Shows a permission prompt and returns the user's decision asynchronously.
    /// </summary>
    /// <param name="request">The ask decision from the permission evaluator. Must not be <see langword="null"/>.</param>
    /// <param name="toolName">The canonical tool name, used for session-level tracking. Must not be <see langword="null"/>.</param>
    /// <param name="toolInput">Human-readable representation of the tool input shown in the panel.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that resolves to a <see cref="PermissionDecision"/> reflecting
    /// the user's choice.
    /// </returns>
    Task<PermissionDecision> AskAsync(PermissionAsk request, string toolName, string toolInput);
}

/// <summary>
/// Spectre.Console-based interactive permission dialog.
/// Tracks "Allow Always" decisions in both an in-process set and the session-level
/// <see cref="IPermissionEvaluator"/> cache so that repeat invocations skip the prompt.
/// </summary>
public sealed class SpectrePermissionDialog : IPermissionDialog
{
    private const string ChoiceAllow = "Allow";
    private const string ChoiceAlwaysAllow = "Allow Always (this session)";
    private const string ChoiceDeny = "Deny";
    private const string UserDeniedReason = "user denied";
    private const string UserDeniedMessage = "User denied the operation";
    private const int MaxPanelContentLength = 500;

    private readonly IPermissionEvaluator _evaluator;

    /// <summary>
    /// Set of tool names the user has chosen to always allow for the current session.
    /// </summary>
    private readonly HashSet<string> _sessionAllowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initialises a new <see cref="SpectrePermissionDialog"/>.
    /// </summary>
    /// <param name="evaluator">
    /// The permission evaluator whose session cache is populated when the user approves an invocation.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="evaluator"/> is <see langword="null"/>.
    /// </exception>
    public SpectrePermissionDialog(IPermissionEvaluator evaluator)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    /// <inheritdoc/>
    public async Task<PermissionDecision> AskAsync(PermissionAsk request, string toolName, string toolInput)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(toolInput);

        // Fast path: user already approved this tool for the entire session.
        if (_sessionAllowed.Contains(toolName))
            return new PermissionAllowed(Reason: "session always-allow");

        // Run the interactive Spectre.Console prompt on the thread pool to avoid deadlocks.
        return await Task.Run(() =>
        {
            // Render the tool details panel.
            AnsiConsole.WriteLine();
            var panelContent = toolInput.Length > MaxPanelContentLength
                ? string.Concat(toolInput.AsSpan(0, MaxPanelContentLength), "...")
                : toolInput;

            var panel = new Panel(panelContent)
            {
                Header = new PanelHeader($"[yellow]{toolName.EscapeMarkup()}[/] wants to execute"),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Yellow),
            };
            AnsiConsole.Write(panel);

            // Present the interactive selection prompt.
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title(request.Message.EscapeMarkup())
                    .AddChoices(ChoiceAllow, ChoiceAlwaysAllow, ChoiceDeny));

            switch (choice)
            {
                case ChoiceAllow:
                    _evaluator.CacheApproval(toolName, toolInput, true);
                    return (PermissionDecision)new PermissionAllowed(Reason: "user approved");

                case ChoiceAlwaysAllow:
                    _evaluator.CacheApproval(toolName, toolInput, true);
                    return AllowAlways(toolName);

                default:
                    return new PermissionDenied(UserDeniedMessage, UserDeniedReason);
            }
        }).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private PermissionAllowed AllowAlways(string toolName)
    {
        _sessionAllowed.Add(toolName);
        return new PermissionAllowed(Reason: "user approved (always this session)");
    }
}
