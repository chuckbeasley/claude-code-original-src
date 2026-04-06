namespace ClaudeCode.Services.Engine;

using ClaudeCode.Core.Permissions;
using ClaudeCode.Core.Tools;
using ClaudeCode.Permissions;
using System.Text.Json;

/// <summary>
/// Immutable configuration for a single <see cref="QueryEngine"/> instance.
/// </summary>
/// <param name="Model">
/// The Anthropic model identifier to use for requests, e.g. "claude-sonnet-4-6".
/// </param>
/// <param name="Cwd">
/// The working directory used when building the system prompt (git context, CLAUDE.md lookup).
/// </param>
/// <param name="CustomSystemPrompt">
/// Optional override for the system prompt text. When <see langword="null"/>, the
/// <see cref="ClaudeCode.Configuration.SystemPromptBuilder"/> generates the prompt automatically.
/// </param>
/// <param name="MaxTokens">Maximum number of tokens to generate in each response.</param>
/// <param name="Tools">
/// Optional registry of tools the model may invoke. When <see langword="null"/>, the engine
/// operates in text-only mode and tool-use responses from the API are not executed.
/// </param>
/// <param name="PermissionEvaluator">
/// Optional evaluator that gates tool execution through the three-phase permission pipeline.
/// When <see langword="null"/>, tools execute without permission checks.
/// </param>
/// <param name="PermissionDialog">
/// Optional callback invoked when the evaluator returns <see cref="PermissionAsk"/>.
/// Receives the ask request, tool name, and serialised tool input; returns the user's decision.
/// When <see langword="null"/> and an ask is issued, the tool is denied.
/// </param>
/// <param name="McpManager">
/// Optional MCP server manager. Typed as <see langword="object"/> to avoid a circular project
/// dependency. Tools cast this to <c>McpServerManager</c> at their usage sites.
/// </param>
/// <param name="QuestionDialog">
/// Optional callback for presenting interactive questions to the user.
/// Receives the raw <c>questions</c> JSON array and returns a JSON string
/// mapping each question to the user's chosen answer.
/// When <see langword="null"/>, questions cannot be answered interactively.
/// </param>
public sealed record QueryEngineConfig(
    string Model,
    string Cwd,
    string? CustomSystemPrompt,
    int MaxTokens,
    ToolRegistry? Tools = null,
    IPermissionEvaluator? PermissionEvaluator = null,
    Func<PermissionAsk, string, string, Task<PermissionDecision>>? PermissionDialog = null,
    object? McpManager = null,
    Func<JsonElement, Task<string>>? QuestionDialog = null,
    /// <summary>
    /// When &gt; 0, enables extended thinking mode with the given token budget.
    /// Set from <c>settings.AlwaysThinkingEnabled</c> + <c>settings.ThinkingBudgetTokens</c>.
    /// </summary>
    int ThinkingBudgetTokens = 0,
    /// <summary>
    /// Optional hook runner. When non-null, hooks are executed at PreToolUse, PostToolUse, and Stop events.
    /// </summary>
    ClaudeCode.Services.Hooks.HookRunner? HookRunner = null,
    /// <summary>
    /// Controls thinking depth and token limits. Valid values: "low", "medium", "high", "max".
    /// Mapped to effective <c>MaxTokens</c> and <c>ThinkingBudgetTokens</c> inside <see cref="QueryEngine"/>.
    /// Defaults to "medium" (no extended thinking, config MaxTokens unchanged).
    /// </summary>
    string EffortLevel = "medium",
    /// <summary>
    /// When non-null, the named advisor model is noted in the system prompt to encourage
    /// concise, focused responses. Set by the <c>/advisor</c> command.
    /// </summary>
    string? AdvisorModel = null,
    /// <summary>
    /// Extra project directories added via the <c>/add-dir</c> command. Each directory's
    /// CLAUDE.md files are appended to the system prompt as additional context sections.
    /// A live <see cref="IReadOnlyList{T}"/> wrapper is passed so that directories added
    /// mid-session are reflected on subsequent turns without rebuilding the engine.
    /// </summary>
    IReadOnlyList<string>? ExtraDirectories = null,
    /// <summary>
    /// When <see langword="true"/>, appends the coordinator system prompt to every turn and
    /// wraps the first user message with coordinator context. Can also be activated via the
    /// <c>CLAUDE_CODE_COORDINATOR_MODE</c> environment variable without rebuilding the engine.
    /// </summary>
    bool CoordinatorMode = false,
    /// <summary>
    /// Optional session-scoped in-RAM memory. When non-null and containing at least one fact,
    /// <see cref="QueryEngine"/> appends <c>BuildPromptSection()</c> to the system prompt on
    /// each turn so the model is aware of facts stored during the current session.
    /// </summary>
    ClaudeCode.Services.Memory.SessionMemory? SessionMemory = null,
    /// <summary>
    /// When <see langword="true"/> (default), the engine automatically compacts context
    /// when estimated token usage approaches the model's limit, using the
    /// <see cref="ClaudeCode.Services.Compact.AutoCompactService"/> circuit breaker.
    /// </summary>
    bool AutoCompact = true,
    /// <summary>
    /// Optional micro-compaction service. When non-null, large tool results are truncated
    /// inline before being appended to the conversation history, reducing per-turn token bloat.
    /// </summary>
    ClaudeCode.Services.Compact.MicroCompactService? MicroCompact = null,
    /// <summary>
    /// Optional session-scoped key/value memory service. When non-null and containing facts,
    /// <see cref="QueryEngine"/> appends <c>BuildPromptSection()</c> after the coordinator
    /// system prompt so the model is aware of facts stored during the current session.
    /// </summary>
    ClaudeCode.Services.Memory.SessionMemoryService? SessionMemoryService = null,
    /// <summary>
    /// Optional agent summary service. When non-null, <see cref="QueryEngine"/> passes it
    /// into <see cref="ClaudeCode.Core.Tools.ToolUseContext"/> so that <c>AgentTool</c> can
    /// compress long sub-agent results before returning them to the parent engine.
    /// </summary>
    ClaudeCode.Services.AgentSummary.AgentSummaryService? AgentSummaryService = null,
    /// <summary>
    /// Optional tool-usage statistics collector. When non-null, <see cref="ToolExecutor"/>
    /// records per-tool call counts, error rates, and durations for display via <c>/stats</c>.
    /// </summary>
    ClaudeCode.Services.ToolUseSummary.ToolUseSummaryService? ToolUsageSummary = null);
