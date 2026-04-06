namespace ClaudeCode.Core.Tools;

using ClaudeCode.Core.Messages;
using ClaudeCode.Core.Permissions;
using ClaudeCode.Core.State;
using System.Text.Json;

/// <summary>
/// Immutable context object threaded into every tool invocation.
/// Carries all ambient state a tool needs to execute and make permission decisions
/// without reaching into global or static state.
/// </summary>
public record ToolUseContext
{
    /// <summary>
    /// The tool registry for the current session. Tools may look up sibling tools
    /// (e.g. to delegate sub-operations) via this registry.
    /// </summary>
    public required ToolRegistry ToolRegistry { get; init; }

    /// <summary>Top-level application state for the current session.</summary>
    public required AppState AppState { get; init; }

    /// <summary>
    /// Cache of file contents read during this conversation turn.
    /// Tools that read files populate this cache so that write tools can detect
    /// stale-read conditions before committing changes.
    /// </summary>
    public required FileStateCache ReadFileState { get; init; }

    /// <summary>
    /// Per-file edit history for the current session.
    /// The <see cref="FileEditTool"/> saves a snapshot before each edit so that
    /// undo operations can restore previous content.
    /// </summary>
    public FileHistory FileHistory { get; init; } = new();

    /// <summary>
    /// The current working directory from which relative paths are resolved.
    /// Always an absolute, normalised path.
    /// </summary>
    public required string Cwd { get; init; }

    /// <summary>
    /// Token used to signal cooperative cancellation to long-running tool operations.
    /// Tools must honour this token at each async boundary.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// <see langword="true"/> when the session is running headless (no interactive prompts).
    /// Tools that require user input must treat this as an implicit deny.
    /// </summary>
    public bool IsNonInteractive { get; init; }

    /// <summary><see langword="true"/> when verbose diagnostic output is requested.</summary>
    public bool Verbose { get; init; }

    /// <summary>
    /// Identifier of the model driving the main conversation loop, if known.
    /// Tools may use this to adjust behaviour based on model capabilities.
    /// </summary>
    public string? MainLoopModel { get; init; }

    /// <summary>
    /// The full conversation history as of this tool invocation, or <see langword="null"/>
    /// when the history is not available to the tool (e.g. in isolated sub-agents).
    /// </summary>
    public IReadOnlyList<Message>? Messages { get; init; }

    /// <summary>
    /// MCP server manager instance, if available. Cast to <c>McpServerManager</c> at usage sites.
    /// Typed as <see langword="object"/> to avoid a circular project dependency between
    /// <c>ClaudeCode.Core</c> and <c>ClaudeCode.Mcp</c>.
    /// </summary>
    public object? McpManager { get; init; }

    /// <summary>
    /// Optional callback for presenting interactive questions to the user.
    /// Receives the raw <c>questions</c> JSON array and returns a JSON string
    /// mapping each question index to the user's chosen answer.
    /// When <see langword="null"/>, questions cannot be answered interactively.
    /// </summary>
    public Func<JsonElement, Task<string>>? QuestionDialog { get; init; }

    /// <summary>
    /// Optional hook runner for firing lifecycle events during tool execution.
    /// Typed as <see langword="object"/> to avoid a circular project dependency between
    /// <c>ClaudeCode.Core</c> and <c>ClaudeCode.Services</c>.
    /// Cast to <c>ClaudeCode.Services.Hooks.HookRunner</c> at usage sites in higher-level assemblies.
    /// </summary>
    public object? HookRunner { get; init; }

    /// <summary>
    /// The <c>id</c> from the originating <c>tool_use</c> API block for this invocation.
    /// Set by <see cref="ClaudeCode.Services.Engine.QueryEngine"/> so that tools can correlate
    /// their result with the request. <see langword="null"/> when the context was not created
    /// from a live tool-use block (e.g. in tests or direct calls).
    /// </summary>
    public string? ToolUseId { get; init; }

    /// <summary>
    /// Optional agent summary service for compressing long sub-agent results.
    /// Typed as <see langword="object"/> to avoid a circular project dependency between
    /// <c>ClaudeCode.Core</c> and <c>ClaudeCode.Services</c>.
    /// Cast to <c>ClaudeCode.Services.AgentSummary.AgentSummaryService</c> at usage sites.
    /// </summary>
    public object? AgentSummaryService { get; init; }
}
