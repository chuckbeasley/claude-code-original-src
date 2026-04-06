namespace ClaudeCode.Commands;

/// <summary>
/// Base class for all slash commands available in the interactive REPL.
/// </summary>
public abstract class SlashCommand
{
    /// <summary>The primary name of the command, including the leading slash (e.g. "/help").</summary>
    public abstract string Name { get; }

    /// <summary>Additional names that invoke this command (e.g. ["/h", "/?"]).</summary>
    public virtual string[] Aliases => [];

    /// <summary>Short description shown in help output.</summary>
    public abstract string Description { get; }

    /// <summary>
    /// Execute the command.
    /// </summary>
    /// <param name="context">All context and services available to the command.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if the command was handled; <see langword="false"/> to pass through.</returns>
    public abstract Task<bool> ExecuteAsync(CommandContext context, CancellationToken ct = default);
}

/// <summary>Context passed to slash commands during execution.</summary>
public sealed class CommandContext
{
    /// <summary>The raw user input string, unmodified.</summary>
    public required string RawInput { get; init; }

    /// <summary>Tokenized arguments split on whitespace, not including the command name token.</summary>
    public required string[] Args { get; init; }

    /// <summary>Current working directory of the session.</summary>
    public required string Cwd { get; init; }

    /// <summary>Writes a plain-text line to the console.</summary>
    public required Action<string> Write { get; init; }

    /// <summary>Writes a Spectre.Console markup line to the console.</summary>
    public required Action<string> WriteMarkup { get; init; }

    // -------------------------------------------------------------------------
    // Optional services — nullable because not all commands need all services
    // -------------------------------------------------------------------------

    /// <summary>Session cost and token usage tracker. May be <see langword="null"/>.</summary>
    public ClaudeCode.Services.Api.CostTracker? CostTracker { get; init; }

    /// <summary>Configuration provider. May be <see langword="null"/>.</summary>
    public object? ConfigProvider { get; init; }

    /// <summary>The resolved model ID currently in use. May be <see langword="null"/>.</summary>
    public string? CurrentModel { get; init; }

    /// <summary>Number of messages in the current conversation history.</summary>
    public int ConversationMessageCount { get; init; }

    /// <summary>Application version string (e.g. "0.1.0"). May be <see langword="null"/>.</summary>
    public string? Version { get; init; }

    // -------------------------------------------------------------------------
    // Actions
    // -------------------------------------------------------------------------

    /// <summary>Clears the console screen. May be <see langword="null"/>.</summary>
    public Action? ClearScreen { get; init; }

    /// <summary>Signals the REPL to exit after the current command completes. May be <see langword="null"/>.</summary>
    public Action? RequestExit { get; init; }

    /// <summary>
    /// Triggers a compaction of the conversation history and returns the result.
    /// The delegate returns a <see cref="ClaudeCode.Services.Compact.CompactionResult"/> when
    /// compaction occurred, or <see langword="null"/> when the history was too short.
    /// May be <see langword="null"/> when the REPL has not yet started a session.
    /// </summary>
    public Func<CancellationToken, Task<object?>>? CompactFunc { get; init; }

    /// <summary>
    /// Replaces the active conversation history with a previously saved message list,
    /// enabling session restore from the <c>/resume</c> command.
    /// May be <see langword="null"/> when no engine session is active.
    /// </summary>
    public Action<List<ClaudeCode.Services.Api.MessageParam>>? RestoreSession { get; init; }

    /// <summary>The most recent assistant response text. Used by /copy. May be null.</summary>
    public string? LastAssistantResponse { get; init; }

    /// <summary>
    /// Read-only view of the full conversation message list. Used by /export.
    /// May be null when the engine has not yet started.
    /// </summary>
    public IReadOnlyList<ClaudeCode.Services.Api.MessageParam>? ConversationMessages { get; init; }

    /// <summary>
    /// Switches the active model for the current session.
    /// Used by /fast and /model commands to change model on the fly.
    /// May be null when model switching is not supported.
    /// </summary>
    public Action<string>? SwitchModel { get; init; }

    /// <summary>
    /// Returns the name of the currently active theme (e.g. "default", "dark", "light").
    /// </summary>
    public string? CurrentTheme { get; init; }

    /// <summary>
    /// Switches the UI theme. May be null when theming is not supported.
    /// </summary>
    public Action<string>? SwitchTheme { get; init; }

    /// <summary>
    /// Triggers a full plugin reload: re-scans the plugin directories and registers
    /// any newly discovered tools into the active <see cref="ClaudeCode.Core.Tools.ToolRegistry"/>.
    /// May be <see langword="null"/> when plugin infrastructure is not wired up.
    /// </summary>
    public Action? ReloadPlugins { get; init; }

    /// <summary>
    /// Triggers a full plugin reload for both tools AND commands.
    /// May be <see langword="null"/> when not wired.
    /// </summary>
    public Action? ReloadPluginsAndCommands { get; init; }

    /// <summary>
    /// Starts or stops the speech-to-text input service.
    /// Receives <see langword="true"/> to start listening, <see langword="false"/> to stop.
    /// May be <see langword="null"/> when voice infrastructure is not wired.
    /// </summary>
    public Action<bool>? ToggleVoiceInput { get; init; }

    /// <summary>
    /// The names of any plugin-contributed commands currently registered.
    /// Used by /help to show a "Plugin Commands" section.
    /// May be <see langword="null"/> when no plugin commands are registered.
    /// </summary>
    public IReadOnlyList<string>? PluginCommandNames { get; init; }

    /// <summary>
    /// Stores a side-question to be appended to the user's next prompt turn.
    /// Used by the <c>/btw</c> command to schedule an additional question without
    /// interrupting the current input flow. May be <see langword="null"/>.
    /// </summary>
    public Action<string>? SetPendingBtw { get; init; }

    // -------------------------------------------------------------------------
    // Bridge server control (populated by ReplSession when bridge is wired)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts the WebSocket bridge server. Accepts a <see cref="CancellationToken"/> so the
    /// caller can cancel startup. <see langword="null"/> when bridge infrastructure is not wired.
    /// </summary>
    public Func<CancellationToken, Task>? BridgeStart { get; init; }

    /// <summary>
    /// Stops the WebSocket bridge server. <see langword="null"/> when bridge infrastructure
    /// is not wired.
    /// </summary>
    public Func<Task>? BridgeStop { get; init; }

    /// <summary>
    /// Returns a value tuple <c>(isRunning, port, token)</c> describing the current bridge
    /// server state. <see langword="null"/> when bridge infrastructure is not wired.
    /// </summary>
    public Func<(bool isRunning, int port, string token)>? BridgeGetStatus { get; init; }

    /// <summary>
    /// Session-scoped in-RAM memory store. Used by <c>/remember</c>, <c>/memories</c>,
    /// and <c>/forget</c> commands to store and retrieve facts for the current session.
    /// May be <see langword="null"/> when session memory is not wired.
    /// </summary>
    public ClaudeCode.Services.Memory.SessionMemory? Memory { get; init; }

    /// <summary>
    /// Grants permission for a specific tool from a named MCP server.
    /// Receives the server name and tool name as arguments.
    /// May be <see langword="null"/> when MCP channel permissions are not wired.
    /// </summary>
    public Action<string, string>? McpAllow { get; init; }

    /// <summary>
    /// Blocks a specific tool from a named MCP server.
    /// Receives the server name and tool name as arguments.
    /// May be <see langword="null"/> when MCP channel permissions are not wired.
    /// </summary>
    public Action<string, string>? McpDeny { get; init; }

    // -------------------------------------------------------------------------
    // API / engine access
    // -------------------------------------------------------------------------

    /// <summary>
    /// Anthropic API client for commands that require one-shot API calls
    /// (e.g. <c>/summary</c>). May be <see langword="null"/> when the client is not
    /// wired into the command context.
    /// </summary>
    public ClaudeCode.Services.Api.IAnthropicClient? AnthropicClient { get; init; }

    /// <summary>
    /// Submits a user-turn string to the active query engine, streaming the response
    /// to the console exactly as a typed message would. Used by commands that need
    /// to trigger a full conversation turn (e.g. <c>/autofix-pr</c>).
    /// May be <see langword="null"/> when no engine session is active.
    /// </summary>
    public Func<string, CancellationToken, Task>? SubmitTurn { get; init; }

    /// <summary>
    /// The unique identifier of the active engine session. Used by commands that
    /// persist session state (e.g. <c>/summary</c>).
    /// May be <see langword="null"/> when no session is active.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Tool usage statistics for the current session, collected by <c>ToolExecutor</c>.
    /// Used by <c>/stats</c> to display per-tool call counts, error rates, and average durations.
    /// May be <see langword="null"/> when statistics collection is not wired.
    /// </summary>
    public ClaudeCode.Services.ToolUseSummary.ToolUseSummaryService? ToolUsageSummary { get; init; }

    /// <summary>
    /// When a command wants to pre-populate the next user query (e.g. <c>/autofix-pr</c>),
    /// it invokes this callback with the prompt text. The REPL submits it to the engine
    /// as the next user turn immediately after the command completes.
    /// May be <see langword="null"/> when the feature is not wired by the host.
    /// </summary>
    public Action<string>? SetNextPrompt { get; init; }
}

/// <summary>
/// Maintains a case-insensitive map of command names and aliases to <see cref="SlashCommand"/> instances.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, SlashCommand> _commands =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers <paramref name="command"/> under its <see cref="SlashCommand.Name"/> and all
    /// <see cref="SlashCommand.Aliases"/>. Re-registering an existing name overwrites the previous entry.
    /// </summary>
    /// <param name="command">The command to register. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="command"/> is <see langword="null"/>.</exception>
    public void Register(SlashCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands[command.Name] = command;
        foreach (var alias in command.Aliases)
            _commands[alias] = command;
    }

    /// <summary>
    /// Removes a command and its aliases from the registry. No-op if not found.
    /// </summary>
    /// <param name="name">The primary name or alias of the command to remove (case-insensitive).</param>
    public void Unregister(string name)
    {
        if (_commands.TryGetValue(name, out var cmd))
        {
            _commands.Remove(cmd.Name); // always remove primary name
            _commands.Remove(name);     // remove the key passed in (no-op if same as primary)
            foreach (var alias in cmd.Aliases)
                _commands.Remove(alias);
        }
    }

    /// <summary>
    /// Looks up a command by name or alias. Returns <see langword="null"/> when not found.
    /// </summary>
    /// <param name="name">The name or alias to look up (case-insensitive).</param>
    public SlashCommand? Get(string name) => _commands.GetValueOrDefault(name);

    /// <summary>Returns distinct command instances (each command appears once even if it has aliases).</summary>
    public IEnumerable<SlashCommand> GetAll() => _commands.Values.Distinct();

    /// <summary>Returns all registered names and aliases in alphabetical order.</summary>
    public IEnumerable<string> GetCommandNames() => _commands.Keys.Order();
}
