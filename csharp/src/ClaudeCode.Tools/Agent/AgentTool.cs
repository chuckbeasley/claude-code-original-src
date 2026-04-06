namespace ClaudeCode.Tools.Agent;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Configuration;
using ClaudeCode.Core.Tools;
using ClaudeCode.Services.Api;
using ClaudeCode.Services.Engine;
using ClaudeCode.Tools.SendMessage;

/// <summary>
/// Strongly-typed input for the <see cref="AgentTool"/>.
/// </summary>
public record AgentInput
{
    /// <summary>The task description or question for the sub-agent to handle.</summary>
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    /// <summary>Optional short description of the task (3–5 words) for display purposes.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Optional name of a named agent definition to use. When present the loader will match
    /// it against definitions loaded from <c>.claude/agents/</c>.
    /// </summary>
    [JsonPropertyName("subagent_type")]
    public string? SubagentType { get; init; }

    /// <summary>Optional model override for this specific invocation.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

/// <summary>
/// Result produced by a completed <see cref="AgentTool"/> run.
/// </summary>
/// <param name="Result">The full text output produced by the sub-agent.</param>
/// <param name="AgentId">The session identifier of the sub-agent engine that ran.</param>
/// <param name="TurnCount">Number of conversation turns completed by the sub-agent.</param>
public record AgentOutput(string Result, string AgentId, int TurnCount);

/// <summary>
/// Spawns an isolated sub-agent (a new <see cref="QueryEngine"/> instance) to handle
/// complex, multi-step tasks autonomously. The sub-agent gets its own conversation
/// history and a scoped tool registry.
/// </summary>
public sealed class AgentTool : Tool<AgentInput, AgentOutput>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            prompt = new
            {
                type = "string",
                description = "The task for the sub-agent to perform. Be specific and self-contained.",
            },
            description = new
            {
                type = "string",
                description = "Short description of the task (3-5 words) shown in the UI.",
            },
            subagent_type = new
            {
                type = "string",
                description = "Optional named agent type defined in .claude/agents/ to use.",
            },
            model = new
            {
                type = "string",
                description = "Optional model override for this invocation.",
            },
        },
        required = new[] { "prompt" },
    });

    private readonly IAnthropicClient _client;
    private readonly CostTracker _costTracker;
    private readonly ToolRegistry _parentRegistry;
    private readonly List<AgentDefinition> _agentDefinitions;

    /// <summary>
    /// Initializes a new <see cref="AgentTool"/>.
    /// </summary>
    /// <param name="client">Anthropic API client. Must not be <see langword="null"/>.</param>
    /// <param name="costTracker">Shared cost accumulator. Must not be <see langword="null"/>.</param>
    /// <param name="parentRegistry">
    /// The parent session's tool registry. Sub-agent registries are derived from this.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <param name="agentDefinitions">
    /// Pre-loaded agent definitions from <c>.claude/agents/</c>. May be <see langword="null"/>
    /// or empty when no definitions are present.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="client"/>, <paramref name="costTracker"/>, or
    /// <paramref name="parentRegistry"/> is <see langword="null"/>.
    /// </exception>
    public AgentTool(
        IAnthropicClient client,
        CostTracker costTracker,
        ToolRegistry parentRegistry,
        List<AgentDefinition>? agentDefinitions = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _costTracker = costTracker ?? throw new ArgumentNullException(nameof(costTracker));
        _parentRegistry = parentRegistry ?? throw new ArgumentNullException(nameof(parentRegistry));
        _agentDefinitions = agentDefinitions ?? [];
    }

    /// <inheritdoc/>
    public override string Name => "Agent";

    /// <inheritdoc/>
    public override string? SearchHint => "launch sub-agent for complex multi-step tasks";

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Launch a sub-agent to handle complex tasks autonomously");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Launches a new isolated agent to handle complex, multi-step tasks. " +
            "The sub-agent receives its own conversation history and a scoped set of tools. " +
            "Use this when a task requires multiple tool calls that can be delegated entirely.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null)
    {
        if (input is { } el
            && el.TryGetProperty("description", out var descEl)
            && descEl.GetString() is { Length: > 0 } desc)
        {
            return $"Agent ({desc})";
        }

        return "Agent";
    }

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is { } el
            && el.TryGetProperty("subagent_type", out var typeEl)
            && typeEl.GetString() is { Length: > 0 } agentType)
        {
            return $"Running {agentType} sub-agent";
        }

        return "Running sub-agent";
    }

    /// <inheritdoc/>
    public override AgentInput DeserializeInput(JsonElement json)
        => JsonSerializer.Deserialize<AgentInput>(json.GetRawText(), JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize AgentInput: result was null.");

    /// <inheritdoc/>
    public override async Task<ToolResult<AgentOutput>> ExecuteAsync(
        AgentInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // Resolve named agent definition when a subagent_type was requested.
        AgentDefinition? agentDef = null;
        if (input.SubagentType is { Length: > 0 } subagentType)
        {
            agentDef = _agentDefinitions.FirstOrDefault(
                a => a.Name.Equals(subagentType, StringComparison.OrdinalIgnoreCase));
        }

        // Model resolution priority: input override > agent definition > environment/default.
        var model = input.Model
            ?? agentDef?.Model
            ?? ModelResolver.Resolve();

        // System prompt: use agent definition's prompt when available, otherwise use a neutral default.
        var systemPrompt = agentDef?.SystemPrompt
            ?? "You are a helpful sub-agent. Complete the given task thoroughly and return a concise result.";

        // Create a registry scoped to this sub-agent (no recursive Agent tool by default).
        var agentRegistry = CreateAgentRegistry(agentDef);

        var engineConfig = new QueryEngineConfig(
            Model: model,
            Cwd: context.Cwd,
            CustomSystemPrompt: systemPrompt,
            MaxTokens: 16384,
            Tools: agentRegistry,
            McpManager: context.McpManager);

        var engine = new QueryEngine(_client, _costTracker, new SystemPromptBuilder(), engineConfig);
        var agentId = engine.SessionId;

        // Pre-create this sub-agent's message queue so that other agents can post
        // messages to it before it begins executing (race-free delivery).
        AgentMessageBus.GetQueue(agentId);

        // Stream the sub-agent's response and accumulate its text output.
        var resultBuilder = new StringBuilder();
        var turnCount = 0;

        await foreach (var evt in engine.SubmitAsync(input.Prompt, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case TextDeltaEvent text:
                    resultBuilder.Append(text.Text);
                    break;

                case MessageCompleteEvent:
                    turnCount++;
                    break;
            }
        }

        var rawResult = resultBuilder.ToString().Trim();
        var isError = rawResult.Length == 0;

        if (isError)
            rawResult = "(Agent produced no output)";

        // Truncate oversized results so they fit in a tool_result block without overwhelming the model.
        if (rawResult.Length > MaxResultSizeChars)
            rawResult = rawResult[..MaxResultSizeChars] + "\n...[truncated]";

        // Summarize long results via AI to give the parent engine more focused context.
        if (!isError && rawResult.Length > 2000)
        {
            var summaryService = context.AgentSummaryService
                as ClaudeCode.Services.AgentSummary.AgentSummaryService;
            if (summaryService is not null)
            {
                rawResult = await summaryService
                    .SummarizeAsync(agentId, rawResult, ct)
                    .ConfigureAwait(false);
            }
        }

        // Wrap result as a <task-notification> XML element so coordinator-mode agents can
        // parse sub-agent completions from the tool_result content stream.
        var taskId = $"task-{Guid.NewGuid():N}";
        var status = isError ? "failed" : "completed";
        var summary = rawResult.Length > 120 ? rawResult[..117] + "..." : rawResult;
        var toolUseId = context.ToolUseId ?? string.Empty;
        var xmlResult = $"""
            <task-notification>
              <task-id>{taskId}</task-id>
              <tool-use-id>{toolUseId}</tool-use-id>
              <status>{status}</status>
              <summary>{System.Security.SecurityElement.Escape(summary)}</summary>
              <result>{System.Security.SecurityElement.Escape(rawResult)}</result>
            </task-notification>
            """;

        // SubagentStop hook — fires after the sub-agent has produced its final result.
        var hookRunner = context.HookRunner as ClaudeCode.Services.Hooks.HookRunner;
        if (hookRunner is not null)
        {
            await hookRunner.RunAsync(new ClaudeCode.Services.Hooks.HookContext(
                Event: "SubagentStop",
                ToolName: "Agent",
                ToolInput: input.Prompt,
                ToolResult: rawResult,
                ToolIsError: isError,
                SessionId: engine.SessionId,
                Cwd: context.Cwd), ct).ConfigureAwait(false);
        }

        return new ToolResult<AgentOutput>
        {
            Data = new AgentOutput(xmlResult, agentId, turnCount),
        };
    }

    /// <inheritdoc/>
    public override string MapResultToString(AgentOutput result, string toolUseId)
        => result.Result;

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="ToolRegistry"/> for the sub-agent.
    /// When the <paramref name="agentDef"/> specifies an allow-list, only those tools are
    /// included. Otherwise all parent tools except <see cref="AgentTool"/> are registered,
    /// preventing unbounded recursive agent spawning.
    /// </summary>
    private ToolRegistry CreateAgentRegistry(AgentDefinition? agentDef)
    {
        var registry = new ToolRegistry();

        if (agentDef?.AllowedTools is { Count: > 0 } allowedTools)
        {
            foreach (var toolName in allowedTools)
            {
                var tool = _parentRegistry.GetTool(toolName);
                if (tool is not null)
                    registry.Register(tool);
            }
        }
        else
        {
            // Register all parent tools except this AgentTool to prevent recursive spawning.
            foreach (var tool in _parentRegistry.GetAll())
            {
                if (!string.Equals(tool.Name, Name, StringComparison.OrdinalIgnoreCase))
                    registry.Register(tool);
            }
        }

        return registry;
    }
}
